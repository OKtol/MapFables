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

/// <summary>
/// Серверная система. Читает локальную mapdb сервера и отправляет пиксели
/// целевым игрокам через встроенный механизм движка SendMapDataToClient,
/// который доставит данные в MapFablesChunkLayer.OnDataFromServer.
/// </summary>
internal class MapFablesServerSystem : ModSystem
{
    private ICoreServerAPI sapi = null!;
    private WorldMapManager wmm = null!;

    // Максимум чанков в одном пакете
    private const int BatchSize = 200;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);
        sapi = api;
        wmm = api.ModLoader.GetModSystem<WorldMapManager>();

        api.ChatCommands
            .Create("sharemap")
            .WithDescription("Поделиться своей картой со всеми онлайн-игроками")
            .RequiresPlayer()
            .RequiresPrivilege(Privilege.chat)
            .HandleWith(OnShareMapCommand);

        api.Logger.Notification("[MapFables] Server-side loaded. Use /sharemap to share map.");
    }

    private List<(int x, int z, int[] pixels)> GetAllMapPieces()
    {
        var result = new List<(int, int, int[])>();

        string savegameId = sapi.WorldManager.SaveGame.SavegameIdentifier;
        string mapDbPath = Path.Combine(GamePaths.DataPath, "Maps", savegameId + ".db");

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
            cmd.CommandText = "SELECT COUNT(*) FROM mappiece";
            long rowCount = (long)cmd.ExecuteScalar()!;
            sapi.Logger.Notification("[MapFables] mappiece table contains {0} rows", rowCount);

            if (rowCount == 0) return result;

            cmd.CommandText = "SELECT position, data FROM mappiece";
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                long pos = reader.GetInt64(0);
                byte[] blob = (byte[])reader[1];

                var piece = SerializerUtil.Deserialize<MapPieceDB>(blob);
                if (piece?.Pixels == null || piece.Pixels.Length == 0) continue;

                // pos = (x << 32) | (z & 0xFFFFFFFF)
                int x = (int)(pos >> 32);
                int z = (int)(pos & 0xFFFFFFFF);
                result.Add((x, z, piece.Pixels));
            }

            sapi.Logger.Notification("[MapFables] Loaded {0} chunks from mapdb", result.Count);
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
        if (caller == null) return TextCommandResult.Error("Player not found");

        // Собираем список получателей заранее — до Task.Run
        var targets = new List<IServerPlayer>();
        foreach (IServerPlayer p in sapi.World.AllOnlinePlayers)
        {
            if (p != caller) targets.Add(p);
        }

        if (targets.Count == 0)
            return TextCommandResult.Success("[MapFables] Нет других игроков онлайн.");

        caller.SendMessage(GlobalConstants.GeneralChatGroup,
            "[MapFables] Читаю данные карты, подождите...",
            EnumChatType.Notification);

        // Чтение БД в фоновом потоке — не блокируем сервер
        Task.Run(() =>
        {
            var allPieces = GetAllMapPieces();

            if (allPieces.Count == 0)
            {
                sapi.Event.EnqueueMainThreadTask(() =>
                    caller.SendMessage(GlobalConstants.GeneralChatGroup,
                        "[MapFables] База данных карты пуста.",
                        EnumChatType.Notification),
                    "mapfables_empty");
                return;
            }

            // Находим наш слой карты в WorldMapManager
            // Слой должен быть зарегистрирован — MapFablesModSystem делает это в Start()
            MapLayer? ourLayer = null;
            sapi.Event.EnqueueMainThreadTask(() =>
            {
                ourLayer = wmm.MapLayers.Find(l => l is MapFablesChunkLayer);
            }, "mapfables_findlayer");

            // Небольшая пауза чтобы EnqueueMainThreadTask выполнился
            // прежде чем мы начнём отправлять батчи
            System.Threading.Thread.Sleep(50);

            if (ourLayer == null)
            {
                sapi.Logger.Error("[MapFables] MapFablesChunkLayer not found in MapLayers!");
                sapi.Event.EnqueueMainThreadTask(() =>
                    caller.SendMessage(GlobalConstants.GeneralChatGroup,
                        "[MapFables] Ошибка: слой карты не найден.",
                        EnumChatType.Notification),
                    "mapfables_error");
                return;
            }

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

                byte[] data = SerializerUtil.Serialize(packet);

                // Отправка через официальный канал движка —
                // попадёт в MapFablesChunkLayer.OnDataFromServer у каждого получателя
                var capturedLayer = ourLayer;
                int capturedBatch = i;
                sapi.Event.EnqueueMainThreadTask(() =>
                {
                    foreach (var target in targets)
                    {
                        wmm.SendMapDataToClient(capturedLayer, target, data);
                    }
                    sapi.Logger.Notification(
                        "[MapFables] Sent batch at {0}, size {1} to {2} player(s)",
                        capturedBatch, batchChunks.Count, targets.Count);
                }, "mapfables_send");

                totalSent += batchChunks.Count;
            }

            int capturedTotal = totalSent;
            int capturedCount = targets.Count;
            sapi.Event.EnqueueMainThreadTask(() =>
            {
                caller.SendMessage(GlobalConstants.GeneralChatGroup,
                    $"[MapFables] Отправлено {capturedTotal} чанков {capturedCount} игрок(ам).",
                    EnumChatType.Notification);
                sapi.Logger.Notification(
                    "[MapFables] {0} shared {1} chunks to {2} players.",
                    caller.PlayerName, capturedTotal, capturedCount);
            }, "mapfables_done");
        });

        return TextCommandResult.Success();
    }
}
