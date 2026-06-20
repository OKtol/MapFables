using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace MapFables
{
    public class MapFablesModSystem : ModSystem
    {
        private ICoreServerAPI? sapi;
        private ICoreClientAPI? capi;
        private IServerNetworkChannel? serverChannel;

        /// <summary>
        /// Статическая очередь пикселей: сервер (фоновый поток) кладёт сюда данные,
        /// MapFablesChunkLayer.OnTick() (главный поток) забирает и создаёт текстуры.
        /// ConcurrentQueue потокобезопасна без дополнительных локов.
        /// </summary>
        internal static readonly ConcurrentQueue<(int x, int z, int[] pixels)> ReceivedPixels = new();

        public override void Start(ICoreAPI api)
        {
            api.Logger.Notification("[MapFables] ModSystem Start()");
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

            // Регистрируем канал на сервере
            serverChannel = api.Network
                .RegisterChannel("mapfables")
                .RegisterMessageType<MapDataPacket>()
                as IServerNetworkChannel;

            // Регистрируем слой карты — WorldMapManager должен знать о нём до OnLvlFinalize
            var wmm = api.ModLoader.GetModSystem<WorldMapManager>();
            wmm.RegisterMapLayer<MapFablesChunkLayer>("mapfableschunk", 1.0);

            // Регистрируем команду /sharemap
            api.ChatCommands
                .Create("sharemap")
                .WithDescription("Поделиться открытой картой со всеми игроками")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .HandleWith(OnShareMapCommand);

            api.Logger.Notification("[MapFables] Server side started, command /sharemap registered.");
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            // Регистрируем канал на клиенте и вешаем обработчик входящего пакета
            api.Network
                .RegisterChannel("mapfables")
                .RegisterMessageType<MapDataPacket>()
                .SetMessageHandler<MapDataPacket>(OnClientPacket);

            // Регистрируем слой карты на клиенте
            var wmm = api.ModLoader.GetModSystem<WorldMapManager>();
            wmm.RegisterMapLayer<MapFablesChunkLayer>("mapfableschunk", 1.0);

            api.Logger.Notification("[MapFables] Client side started, layer registered.");
        }

        /// <summary>
        /// Клиент получил пакет с чанками.
        /// Кладём данные в очередь — текстуры создаются в MapFablesChunkLayer.OnTick()
        /// на главном потоке, потому что OpenGL не потокобезопасен.
        /// </summary>
        private void OnClientPacket(MapDataPacket packet)
        {
            if (packet?.Chunks == null || packet.Chunks.Count == 0) return;

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
                capi.Logger.Notification("[MapFables] Received {0} chunks from {1}.", added, packet.FromPlayer);
            }
        }

        private TextCommandResult OnShareMapCommand(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player == null)
                return TextCommandResult.Error("Только игроки могут использовать эту команду.");

            // Запускаем чтение БД в фоновом потоке, чтобы не блокировать игровой тик
            Task.Run(() => ShareMapAsync(player));
            return TextCommandResult.Success("Отправляю карту...");
        }

        private void ShareMapAsync(IServerPlayer sender)
        {
            if (sapi == null) return;
            try
            {
                // Получаем идентификатор сохранения — проверено в рабочей версии MapFables
                string? saveId = sapi.WorldManager.SaveGame?.SavegameIdentifier;
                if (string.IsNullOrEmpty(saveId))
                {
                    sender.SendMessage(GlobalConstants.GeneralChatGroup,
                        "Не удалось определить идентификатор сохранения.", EnumChatType.Notification);
                    return;
                }

                string mapDbPath = Path.Combine(GamePaths.DataPath, "Maps", saveId + ".db");
                if (!File.Exists(mapDbPath))
                {
                    sender.SendMessage(GlobalConstants.GeneralChatGroup,
                        $"Файл карты не найден: {mapDbPath}", EnumChatType.Notification);
                    return;
                }

                var chunks = ReadChunksFromDb(mapDbPath);
                sapi.Logger.Notification("[MapFables] Read {0} valid chunks from DB.", chunks.Count);

                if (chunks.Count == 0)
                {
                    sender.SendMessage(GlobalConstants.GeneralChatGroup,
                        "Карта пуста — нечего отправлять.", EnumChatType.Notification);
                    return;
                }

                // Рассылаем батчами по 20 чанков, чтобы не перегружать сеть одним огромным пакетом
                int sentTotal = 0;
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
                        SendBatchToAll(batch);
                        sentTotal += batch.Chunks.Count;
                        batch.Chunks = new List<ChunkImageData>();
                        // Небольшая пауза между батчами, чтобы не перегрузить сетевую очередь
                        System.Threading.Thread.Sleep(50);
                    }
                }

                // Отправляем остаток
                if (batch.Chunks.Count > 0)
                {
                    SendBatchToAll(batch);
                    sentTotal += batch.Chunks.Count;
                }

                sender.SendMessage(GlobalConstants.GeneralChatGroup,
                    $"Карта отправлена ({sentTotal} чанков).", EnumChatType.Notification);
            }
            catch (Exception ex)
            {
                sapi.Logger.Error("[MapFables] Ошибка при отправке карты: {0}", ex);
                sender.SendMessage(GlobalConstants.GeneralChatGroup,
                    "Ошибка при отправке карты.", EnumChatType.Notification);
            }
        }

        /// <summary>
        /// Читает все чанки из SQLite напрямую.
        /// Используем прямое подключение вместо MapDB, чтобы избежать конкурентного доступа
        /// к уже открытому файлу БД (ChunkMapLayer держит его открытым).
        ///
        /// Формат поля position (установлен экспериментально для VS 1.21.6):
        ///   position = (Z << 27) | X
        ///   - биты  0-26: координата X
        ///   - биты 27-53: координата Z
        /// </summary>
        private List<(int x, int z, int[] pixels)> ReadChunksFromDb(string dbPath)
        {
            var result = new List<(int x, int z, int[] pixels)>();

            // Mode=ReadOnly — открываем только для чтения, не блокируем ChunkMapLayer
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT position, data FROM mappiece";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(1)) continue;

                long pos = reader.GetInt64(0);

                // Декодируем координаты чанка из упакованного position
                // Формула верифицирована экспериментально
                int cx = (int)(pos & 0x7FFFFFF);          // биты 0-26: X
                int cz = (int)((pos >> 27) & 0x7FFFFFF);  // биты 27-53: Z

                byte[] blob = (byte[])reader[1];
                try
                {
                    var piece = SerializerUtil.Deserialize<MapPieceDB>(blob);
                    if (piece?.Pixels != null &&
                        piece.Pixels.Length == GlobalConstants.ChunkSize * GlobalConstants.ChunkSize)
                    {
                        result.Add((cx, cz, piece.Pixels));
                    }
                }
                catch (Exception ex)
                {
                    sapi!.Logger.Warning("[MapFables] Не удалось десериализовать чанк ({0},{1}): {2}",
                        cx, cz, ex.Message);
                }
            }

            return result;
        }

        private void SendBatchToAll(MapDataPacket batch)
        {
            foreach (var player in sapi!.World.AllOnlinePlayers)
            {
                serverChannel?.SendPacket(batch, player as IServerPlayer);
            }
        }
    }
}
