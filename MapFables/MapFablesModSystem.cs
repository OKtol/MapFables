using Microsoft.Data.Sqlite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace MapFables
{
    public class MapFablesModSystem : ModSystem
    {
        private ICoreServerAPI? sapi;
        private ICoreClientAPI? capi;

        private string channelName = string.Empty;

        /// <summary>Очередь пикселей для клиентского слоя (главный поток).</summary>
        public static ConcurrentQueue<ChunkImageData> ReceivedPixels { get; set; } = new();

        public override void Start(ICoreAPI api)
        {
            channelName = Mod.Info.ModID + ".network";

            api.Network
                .RegisterChannel(channelName)
                .RegisterMessageType<MapDataPacket>();

            var wmm = api.ModLoader.GetModSystem<WorldMapManager>();
            wmm.RegisterMapLayer<MapFablesChunkLayer>("mapfableschunk", 1.0);

            api.Logger.Notification("[MapFables] Mod system loaded.");
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

            sapi.ChatCommands
                .Create("sharemap")
                .WithDescription("Share your explored map with other players")
                .RequiresPrivilege(Privilege.chat)
                .HandleWith(OnShareMapCommand);

            sapi.Logger.Notification("[MapFables] Server layer registered.");
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            capi.Network
                .GetChannel(channelName)
                .SetMessageHandler<MapDataPacket>(OnClientPacket);

            capi.Logger.Notification("[MapFables] Client layer registered.");
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
                ReceivedPixels.Enqueue(chunk);
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
            if (args.Caller.Player is not IServerPlayer player)
                return TextCommandResult.Error("Only players can use this command.");

            ShareMap(player);
            return TextCommandResult.Success("Sharing map...");
        }

        private void ShareMap(IServerPlayer sender)
        {
            var channel = sapi!.Network.GetChannel(channelName)!;

            try
            {
                if (!TryGetMapDbPath(sender, out string mapDbPath))
                    return;

                var pieces = LoadMapPiecesFromDb(mapDbPath);

                sapi.Logger.Notification("[MapFables] Read {0} valid chunks from DB.", pieces.Count);

                var chunks = Enumerable.Chunk(pieces, 20)
                    .Select(x => new MapDataPacket
                    {
                        FromPlayer = sender.PlayerName,
                        Chunks = [.. x]
                    });

                foreach (var packet in chunks)
                    channel.BroadcastPacket(packet);

                sender.SendMessage(GlobalConstants.GeneralChatGroup,
                    $"Shared {pieces.Count} map chunks with everyone.", EnumChatType.Notification);
            }
            catch (Exception ex)
            {
                sapi.Logger.Error("[MapFables] Error sharing map: {0}", ex);
                sender.SendMessage(GlobalConstants.GeneralChatGroup, "Failed to share map.", EnumChatType.Notification);
            }
        }

        private List<ChunkImageData> LoadMapPiecesFromDb(string mapDbPath)
        {
            var chunks = new List<ChunkImageData>();

            var connection = new SqliteConnection($"Data Source={mapDbPath};Mode=ReadOnly");
            connection.Open();

            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT position, data FROM mappiece";

            var reader = cmd.ExecuteReader();
            int debugCounter3 = 0;
            while (reader.Read())
            {
                long pos = reader.GetInt64(0);
                var decodedPos = DecodePosition(pos);

                if (reader.IsDBNull(1)) continue;

                byte[] blob = (byte[])reader[1];
                try
                {
                    var piece = SerializerUtil.Deserialize<MapPieceDB>(blob);
                    if (piece?.Pixels != null && piece.Pixels.Length == GlobalConstants.ChunkSize * GlobalConstants.ChunkSize)
                        chunks.Add(new ChunkImageData 
                        { 
                            X = decodedPos.x,
                            Z = decodedPos.z, 
                            Pixels = piece.Pixels 
                        });
                }
                catch (Exception ex)
                {
                    sapi!.Logger.Warning("[MapFables] Failed to deserialize chunk ({0},{1}): {2}", decodedPos.x, decodedPos.z, ex.Message);
                }
                if (debugCounter3 < 5)
                {
                    sapi!.Logger.Notification("[MapFables] Chunk {0}: ({1},{2}) world pos ({3},{4})",
                        debugCounter3, decodedPos.x, decodedPos.z, decodedPos.x * GlobalConstants.ChunkSize, decodedPos.z * GlobalConstants.ChunkSize);
                    debugCounter3++;
                }
            }
            return chunks;
        }

        private static (int x, int z) DecodePosition(long position)
        {
            int x = (int)(position & 0x7FFFFFF);              // Lower 27 bits = X
            int z = (int)((position >> 27) & 0x7FFFFFF);      // Bits 27-53 = Z (Y in FastVec2i)
            return (x, z);
        }

        private bool TryGetMapDbPath(IServerPlayer sender, out string mapDbPath)
        {
            string? saveId = sapi!.WorldManager.SaveGame?.SavegameIdentifier;
            if (string.IsNullOrEmpty(saveId))
            {
                sender.SendMessage(GlobalConstants.GeneralChatGroup, "Savegame not identified.", EnumChatType.Notification);
                mapDbPath = string.Empty;
                return false;
            }

            mapDbPath = Path.Combine(GamePaths.DataPath, "Maps", saveId + ".db");
            if (!File.Exists(mapDbPath))
            {
                sender.SendMessage(GlobalConstants.GeneralChatGroup, $"Map database not found at {mapDbPath}", EnumChatType.Notification);
                return false;
            }

            return true;
        }
    }
}