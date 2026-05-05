using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace MapFables;

internal class MapFablesServerSystem : ModSystem
{
    private IServerNetworkChannel serverChannel = null!;
    private ICoreServerAPI sapi = null!;

    // Максимальное количество чанков в одном пакете.
    // Разбиваем на батчи, чтобы не перегрузить сеть/память.
    private const int BatchSize = 200;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);
        sapi = api;

        serverChannel = api.Network.GetChannel(MapFablesModSystem.NetworkChannelName);

        api.ChatCommands
            .Create("sharemap")
            .WithDescription("Поделиться всеми чанками карты из базы данных со всеми онлайн-игроками")
            .RequiresPlayer()
            // controlserver — только администратор может делиться картой
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(OnShareMapCommand);

        api.Logger.Notification("[MapFables] Server-side loaded. Use /sharemap to share map data.");
    }

    private List<(int x, int z, int[] pixels)> GetAllMapPieces()
    {
        var result = new List<(int, int, int[])>();

        string savegameId = sapi.WorldManager.SaveGame.SavegameIdentifier;
        string mapsFolder = Path.Combine(GamePaths.DataPath, "Maps");
        string mapDbPath = Path.Combine(mapsFolder, savegameId + ".db");

        if (!File.Exists(mapDbPath))
        {
            sapi.Logger.Error("[MapFables] Map database not found at: {0}", mapDbPath);
            return result;
        }

        try
        {
            using var conn = new SqliteConnection($"Data Source={mapDbPath};Mode=ReadOnly");
            conn.Open();

            using var cmd = conn.CreateCommand();

            // Сначала проверяем количество записей
            cmd.CommandText = "SELECT COUNT(*) FROM mappiece";
            long rowCount = (long)cmd.ExecuteScalar()!;
            sapi.Logger.Notification("[MapFables] mappiece table contains {0} rows", rowCount);

            if (rowCount == 0)
                return result;

            // Читаем все записи
            cmd.CommandText = "SELECT position, data FROM mappiece";
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                long pos = reader.GetInt64(0);
                byte[] blob = (byte[])reader[1];

                var piece = SerializerUtil.Deserialize<MapPieceDB>(blob);
                if (piece?.Pixels == null || piece.Pixels.Length == 0)
                    continue;

                // Позиция кодируется как: pos = (x << 32) | (z & 0xFFFFFFFF)
                int x = (int)(pos >> 32);
                int z = (int)(pos & 0xFFFFFFFF);
                result.Add((x, z, piece.Pixels));
            }

            sapi.Logger.Notification("[MapFables] Loaded {0} map chunks from DB", result.Count);
        }
        catch (Exception ex)
        {
            sapi.Logger.Error("[MapFables] Error reading map database: {0}", ex.Message);
        }

        return result;
    }

    private TextCommandResult OnShareMapCommand(TextCommandCallingArgs args)
    {
        var caller = args.Caller.Player as IServerPlayer;
        if (caller == null)
            return TextCommandResult.Error("Player not found");

        // Получаем список онлайн-игроков заранее (до async),
        // чтобы не обращаться к API из другого потока
        var targets = new List<IServerPlayer>();
        foreach (IServerPlayer p in sapi.World.AllOnlinePlayers)
        {
            if (p != caller)
                targets.Add(p);
        }

        if (targets.Count == 0)
        {
            return TextCommandResult.Success("[MapFables] Нет других игроков онлайн.");
        }

        caller.SendMessage(
            GlobalConstants.GeneralChatGroup,
            "[MapFables] Читаю данные карты, подождите...",
            EnumChatType.Notification);

        // Выносим чтение БД в фоновый поток, чтобы не блокировать сервер
        Task.Run(() =>
        {
            var allPieces = GetAllMapPieces();

            if (allPieces.Count == 0)
            {
                // Возвращаемся в главный поток для отправки сообщения
                sapi.Event.EnqueueMainThreadTask(() =>
                {
                    caller.SendMessage(
                        GlobalConstants.GeneralChatGroup,
                        "[MapFables] База данных карты пуста.",
                        EnumChatType.Notification);
                }, "mapfables_notify");
                return;
            }

            // Разбиваем на батчи и отправляем
            int totalSent = 0;
            for (int i = 0; i < allPieces.Count; i += BatchSize)
            {
                int end = Math.Min(i + BatchSize, allPieces.Count);
                var batchChunks = new List<ChunkImageData>(end - i);

                for (int j = i; j < end; j++)
                {
                    var (x, z, pixels) = allPieces[j];
                    batchChunks.Add(new ChunkImageData { X = x, Z = z, Pixels = pixels });
                }

                var packet = new MapDataPacket
                {
                    SenderName = caller.PlayerName,
                    Chunks = batchChunks
                };

                int batchIndex = i; // capture for lambda
                // Отправка пакетов должна происходить из главного потока
                sapi.Event.EnqueueMainThreadTask(() =>
                {
                    foreach (var target in targets)
                    {
                        serverChannel.SendPacket(packet, target);
                    }
                    sapi.Logger.Notification(
                        $"[MapFables] Sent batch starting at {batchIndex}, " +
                        $"size {packet.Chunks.Count} to {targets.Count} player(s)");
                }, "mapfables_send");

                totalSent += batchChunks.Count;
            }

            int capturedTotal = totalSent;
            int capturedTargets = targets.Count;
            sapi.Event.EnqueueMainThreadTask(() =>
            {
                caller.SendMessage(
                    GlobalConstants.GeneralChatGroup,
                    $"[MapFables] Отправлено {capturedTotal} чанков {capturedTargets} игрок(ам).",
                    EnumChatType.Notification);
                sapi.Logger.Notification(
                    $"[MapFables] {caller.PlayerName} shared {capturedTotal} chunks " +
                    $"to {capturedTargets} players.");
            }, "mapfables_done");
        });

        return TextCommandResult.Success();
    }
}
