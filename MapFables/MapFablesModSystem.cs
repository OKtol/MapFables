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
using Vintagestory.GameContent;

namespace MapFables
{
    public class MapFablesModSystem : ModSystem
    {
        private ICoreServerAPI? sapi;
        private ICoreClientAPI? capi;
        private IServerNetworkChannel? channel;
        internal static readonly ConcurrentQueue<(int x, int z, byte[] pixels)> ReceivedPixels = new();

        public override void Start(ICoreAPI api)
        {
            api.Logger.Notification("[MapFables] Mod system loaded.");
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

            var wmm = api.ModLoader.GetModSystem<WorldMapManager>();
            wmm.RegisterMapLayer<MapFablesChunkLayer>("mapfableschunk", 1.0);
            api.Logger.Notification("[MapFables] Map layer registered.");

            channel = api.Network.RegisterChannel("mapfables")
                .RegisterMessageType<MapDataPacket>();

            api.ChatCommands
                .Create("sharemap")
                .WithDescription("Share your explored map with other players")
                .RequiresPrivilege(Privilege.chat)
                .HandleWith(OnShareMapCommand);
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            var clientChannel = api.Network.RegisterChannel("mapfables")
                .RegisterMessageType<MapDataPacket>();
            clientChannel.SetMessageHandler<MapDataPacket>(OnClientPacket);
            api.Logger.Notification("[MapFables] Client channel registered.");
        }

        private void OnClientPacket(MapDataPacket packet)
        {
            if (packet?.Chunks == null || packet.Chunks.Count == 0)
                return;

            int added = 0;
            foreach (var chunk in packet.Chunks)
            {
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
                sapi.Logger.Notification("[MapFables] Map DB path: {0}", mapDbPath);

                if (!File.Exists(mapDbPath))
                {
                    sender.SendMessage(GlobalConstants.GeneralChatGroup, $"Map database not found at {mapDbPath}", EnumChatType.Notification);
                    return;
                }

                using var connection = new SqliteConnection($"Data Source={mapDbPath};Mode=ReadOnly");
                connection.Open();

                var chunks = new List<(int X, int Z, byte[]? Pixels)>();
                using var cmd = connection.CreateCommand();
                // Правильный запрос к таблице mappiece
                cmd.CommandText = "SELECT position, data FROM mappiece";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    long pos = reader.GetInt64(0);
                    int cx = (int)(pos >> 32);
                    int cz = (int)(pos & 0xFFFFFFFF);
                    byte[]? pixels = reader.IsDBNull(1) ? null : (byte[])reader[1];
                    chunks.Add((cx, cz, pixels));
                }

                sapi.Logger.Notification("[MapFables] Read {0} chunks from DB.", chunks.Count);

                int sentCount = 0;
                var batch = new MapDataPacket
                {
                    FromPlayer = sender.PlayerName,
                    Chunks = new List<ChunkImageData>()
                };

                foreach (var (x, z, pixels) in chunks)
                {
                    batch.Chunks.Add(new ChunkImageData
                    {
                        X = x,
                        Z = z,
                        Pixels = pixels ?? Array.Empty<byte>(),
                        Width = GlobalConstants.ChunkSize,
                        Height = GlobalConstants.ChunkSize
                    });
                    if (batch.Chunks.Count >= 20)
                    {
                        foreach (var player in sapi.World.AllOnlinePlayers)
                        {
                            channel?.SendPacket(batch, player as IServerPlayer);
                        }
                        sentCount += batch.Chunks.Count;
                        batch.Chunks.Clear();
                        System.Threading.Thread.Sleep(50);
                    }
                }
                if (batch.Chunks.Count > 0)
                {
                    foreach (var player in sapi.World.AllOnlinePlayers)
                    {
                        channel?.SendPacket(batch, player as IServerPlayer);
                    }
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
    }
}