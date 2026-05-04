using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace MapFables;

internal class MapFablesServerSystem : ModSystem
{
    private IServerNetworkChannel serverChannel = null!;
    private ICoreServerAPI sapi = null!;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);
        sapi = api;

        serverChannel = api.Network.GetChannel(MapFablesModSystem.NetworkChannelName);

        api.ChatCommands.Create("sharemap")
            .WithDescription("Share all map chunks (from server database) with all online players")
            .RequiresPlayer()
            .RequiresPrivilege(Privilege.chat)
            .HandleWith(OnShareMapCommand);

        api.Logger.Notification("[MapFables] Server-side loaded. Use /sharemap to share the full map data.");
    }

    private List<(FastVec2i coord, int[] pixels)> GetAllMapPieces(MapDB mapDB)
    {
        var result = new List<(FastVec2i, int[])>();
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var field = typeof(SQLiteDBConnection).GetField("sqliteConn", flags);
        if (field == null) return result;
        var conn = field.GetValue(mapDB) as SqliteConnection;
        if (conn == null) return result;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT position, data FROM mappiece";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            long pos = reader.GetInt64(0);
            byte[] blob = (byte[])reader[1];
            var piece = SerializerUtil.Deserialize<MapPieceDB>(blob);
            if (piece?.Pixels != null && piece.Pixels.Length > 0)
            {
                int x = (int)(pos >> 32);
                int z = (int)(pos & 0xFFFFFFFF);
                result.Add((new FastVec2i(x, z), piece.Pixels));
            }
        }
        return result;
    }

    private TextCommandResult OnShareMapCommand(TextCommandCallingArgs args)
    {
        var caller = args.Caller.Player as IServerPlayer;
        if (caller == null) return TextCommandResult.Error("Player not found");

        // Получаем WorldMapManager и через рефлексию поле mapdb
        var worldMapManager = sapi.ModLoader.GetModSystem<WorldMapManager>();
        if (worldMapManager == null) return TextCommandResult.Error("WorldMapManager not found");

        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var mapDbField = typeof(WorldMapManager).GetField("mapdb", flags);
        if (mapDbField == null) return TextCommandResult.Error("MapDB field not found");
        var mapDB = mapDbField.GetValue(worldMapManager) as MapDB;
        if (mapDB == null) return TextCommandResult.Error("MapDB not accessible");

        var allPieces = GetAllMapPieces(mapDB);
        if (allPieces.Count == 0)
        {
            caller.SendMessage(GlobalConstants.GeneralChatGroup, "[MapFables] No map data found in database.", EnumChatType.Notification);
            return TextCommandResult.Success();
        }

        var chunks = new List<ChunkImageData>();
        foreach (var (coord, pixels) in allPieces)
        {
            chunks.Add(new ChunkImageData
            {
                X = coord.X,
                Z = coord.Y,
                Pixels = pixels
            });
        }

        var packet = new MapDataPacket
        {
            SenderName = caller.PlayerName,
            Chunks = chunks
        };

        int sent = 0;
        foreach (IServerPlayer target in sapi.World.AllOnlinePlayers)
        {
            if (target == caller) continue;
            serverChannel.SendPacket(packet, target);
            sent++;
        }

        caller.SendMessage(GlobalConstants.GeneralChatGroup, $"[MapFables] Sent {chunks.Count} chunks to {sent} player(s).", EnumChatType.Notification);
        sapi.Logger.Notification($"[MapFables] {caller.PlayerName} shared {chunks.Count} chunks.");
        return TextCommandResult.Success();
    }
}