using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace MapFables
{
    public class MapFablesModSystem : ModSystem
    {
        private ICoreServerAPI? sapi;
        private ICoreClientAPI? capi;
        private IServerNetworkChannel? channel;

        /// <summary>Очередь пикселей для клиентского слоя (главный поток).</summary>
        internal static readonly ConcurrentQueue<(int x, int z, int[] pixels)> ReceivedPixels = new();

        public override void Start(ICoreAPI api)
        {
            api.Logger.Notification("[MapFables] Mod system loaded.");
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            try
            {
                sapi = api;

                // Регистрируем клиентский слой, чтобы он знал о нашем канале
                var wmm = api.ModLoader.GetModSystem<WorldMapManager>();
                wmm.RegisterMapLayer<MapFablesChunkLayer>("mapfableschunk", 1.0);

                channel = api.Network.RegisterChannel("mapfables")
                    .RegisterMessageType<MapDataPacket>();

                api.ChatCommands
                .Create("sharemap")
                .WithDescription("Share your explored map with other players")
                .RequiresPrivilege(Privilege.chat)
                .HandleWith(OnShareMapCommand);
            }
            catch (Exception ex)
            {
                api.Logger.Error("[MapFables] Failed to start server side: {0}", ex);
            }
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            var clientChannel = api.Network.RegisterChannel("mapfables")
                .RegisterMessageType<MapDataPacket>();
            clientChannel.SetMessageHandler<MapDataPacket>(OnClientPacket);
            var wmm = api.ModLoader.GetModSystem<WorldMapManager>();
            wmm.RegisterMapLayer<MapFablesChunkLayer>("mapfableschunk", 1.0);

            api.Logger.Notification("[MapFables] Client layer registered.");
        }
        

        private void OnClientPacket(MapDataPacket packet)
        {
            if (packet?.Chunks == null || packet.Chunks.Count == 0) return;

            int added = 0;
            foreach (var chunk in packet.Chunks)
            {
                // Проверяем размер (1024 пикселя для 32×32)
                if (chunk.Pixels == null || chunk.Pixels.Length != GlobalConstants.ChunkSize * GlobalConstants.ChunkSize)
                    continue;
                ReceivedPixels.Enqueue((chunk.X, chunk.Z, chunk.Pixels));
                added++;
            }

            if (added > 0 && capi != null)
            {
                capi.ShowChatMessage($"[MapFables] Получено {added} чанков от {packet.FromPlayer}. Откройте карту (M).");
                capi.Logger.Notification("[MapFables] Client received {0} chunks from {1}.", added, packet.FromPlayer);
            }
        }

        private TextCommandResult OnShareMapCommand(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player == null)
                return TextCommandResult.Error("Only players can use this command.");

            Task.Run(() => ShareMapAsync(player));
            return TextCommandResult.Success("Sharing map...");
        }

        private void ShareMapAsync(IServerPlayer sender)
        {
            if (sapi == null) return;
            try
            {
                string? saveId = sapi.WorldManager.SaveGame?.SavegameIdentifier;
                if (string.IsNullOrEmpty(saveId))
                {
                    sender.SendMessage(GlobalConstants.GeneralChatGroup, "Savegame not identified.", EnumChatType.Notification);
                    return;
                }

                string mapDbPath = Path.Combine(GamePaths.DataPath, "Maps", saveId + ".db");
                if (!File.Exists(mapDbPath))
                {
                    sender.SendMessage(GlobalConstants.GeneralChatGroup, $"Map database not found at {mapDbPath}", EnumChatType.Notification);
                    return;
                }

                using var connection = new SqliteConnection($"Data Source={mapDbPath};Mode=ReadOnly");
                connection.Open();

                var chunks = new List<(int X, int Z, int[] Pixels)>();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT position, data FROM mappiece";
                using var reader = cmd.ExecuteReader();
                int debugCounter3 = 0;
                while (reader.Read())
                {
                    long pos = reader.GetInt64(0);
                    int cx = (int)(pos >> 27);
                    int cz = unchecked((int)(uint)(pos & 0xFFFFFFFF));

                    if (reader.IsDBNull(1)) continue;

                    byte[] blob = (byte[])reader[1];
                    try
                    {
                        var piece = SerializerUtil.Deserialize<MapPieceDB>(blob);
                        if (piece?.Pixels != null && piece.Pixels.Length == GlobalConstants.ChunkSize * GlobalConstants.ChunkSize)
                        {
                            chunks.Add((cx, cz, piece.Pixels)); // piece.Pixels уже int[]
                        }
                    }
                    catch (Exception ex)
                    {
                        sapi.Logger.Warning("[MapFables] Failed to deserialize chunk ({0},{1}): {2}", cx, cz, ex.Message);
                    }
                    if (debugCounter3 < 5)
                    {
                        sapi.Logger.Notification("[MapFables] Chunk {0}: ({1},{2}) world pos ({3},{4})",
           debugCounter3, cx, cz, cx * GlobalConstants.ChunkSize, cz * GlobalConstants.ChunkSize);
                        debugCounter3++;
                    }
                }

                sapi.Logger.Notification("[MapFables] Read {0} valid chunks from DB.", chunks.Count);

                // Рассылаем пакетами по 20 чанков
                int sentCount = 0;
                var batch = new MapDataPacket
                {
                    FromPlayer = sender.PlayerName,
                    Chunks = new List<ChunkImageData>()
                };

                foreach (var (x, z, pixels) in chunks)
                {
                    batch.Chunks.Add(new ChunkImageData { X = x, Z = z, Pixels = pixels });

                    if (batch.Chunks.Count >= 20)
                    {
                        SendBatch(batch);
                        sentCount += batch.Chunks.Count;
                        batch.Chunks.Clear();
                        System.Threading.Thread.Sleep(50);
                    }
                }
                if (batch.Chunks.Count > 0)
                {
                    SendBatch(batch);
                    sentCount += batch.Chunks.Count;
                }

                sender.SendMessage(GlobalConstants.GeneralChatGroup,
                    $"Shared {sentCount} map chunks with everyone.", EnumChatType.Notification);
            }
            catch (Exception ex)
            {
                sapi.Logger.Error("[MapFables] Error sharing map: {0}", ex);
                sender.SendMessage(GlobalConstants.GeneralChatGroup, "Failed to share map.", EnumChatType.Notification);
            }
        }

        private void SendBatch(MapDataPacket batch)
        {
            // Отправка всем онлайн игрокам (потенциально не потокобезопасно, но работает в большинстве случаев)
            foreach (var player in sapi!.World.AllOnlinePlayers)
            {
                channel?.SendPacket(batch, player as IServerPlayer);
            }
        }
    }
}