using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent; // для MapDB, FastVec2i

namespace MapSharer
{
    // ===== Классы данных для сетевой передачи =====
    public class MapDataPacket
    {
        public string PlayerName { get; set; } = "";
        public List<ChunkCoord> ExploredChunks { get; set; } = new List<ChunkCoord>();
    }

    public class ChunkCoord
    {
        public int X { get; set; }
        public int Z { get; set; }
        public ChunkCoord() { }
        public ChunkCoord(int x, int z) { X = x; Z = z; }
    }

    // ===== Главный мод =====
    public class MapSharerModSystem : ModSystem
    {
        private ICoreServerAPI? sapi;
        private ICoreClientAPI? capi;

        // ------ Серверная часть ------
        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);
            sapi = api;

            // Регистрация сетевого канала и обработчика (ретрансляция всем)
            var channel = api.Network.RegisterChannel("mapsharer:sharemap");
            channel.RegisterMessageType(typeof(MapDataPacket));
            channel.SetMessageHandler<MapDataPacket>((fromPlayer, packet) =>
            {
                if (packet?.ExploredChunks == null || packet.ExploredChunks.Count == 0) return;
                foreach (var target in api.World.AllOnlinePlayers)
                {
                    if (target == fromPlayer) continue;
                    api.Network.GetChannel("mapsharer:sharemap").SendPacket(packet, (IServerPlayer)target);
                }
            });

            // Команда /sharemap
            api.ChatCommands.Create("sharemap")
                .WithDescription("Поделиться своей исследованной картой с другими игроками")
                .RequiresPrivilege(Privilege.chat)
                .HandleWith(args =>
                {
                    var player = args.Caller.Player as IServerPlayer;
                    if (player == null)
                        return TextCommandResult.Error("Не удалось определить игрока");

                    var worldManager = api.WorldManager;
                    if (worldManager == null)
                    {
                        player.SendMessage(GlobalConstants.GeneralChatGroup, "[MapSharer] WorldManager недоступен", EnumChatType.Notification);
                        return TextCommandResult.Error("Ошибка");
                    }

                    // Все загруженные map-чанки
                    var allChunks = worldManager.AllLoadedMapchunks;
                    if (allChunks == null || allChunks.Count == 0)
                    {
                        player.SendMessage(GlobalConstants.GeneralChatGroup, "[MapSharer] Нет загруженных чанков. Исследуйте мир.", EnumChatType.Notification);
                        return TextCommandResult.Success();
                    }

                    // Собираем координаты чанков, исследованных игроком
                    var coords = new List<ChunkCoord>();
                    foreach (var kvp in allChunks)
                    {
                        long idx = kvp.Key;
                        Vec2i pos = worldManager.MapChunkPosFromChunkIndex2D(idx);
                        if (pos != null && worldManager.HasChunk(pos.X, 0, pos.Y, player))
                        {
                            coords.Add(new ChunkCoord(pos.X, pos.Y));
                        }
                    }

                    if (coords.Count == 0)
                    {
                        player.SendMessage(GlobalConstants.GeneralChatGroup, "[MapSharer] У вас нет исследованных областей для передачи.", EnumChatType.Notification);
                        return TextCommandResult.Success();
                    }

                    var packet = new MapDataPacket
                    {
                        PlayerName = player.PlayerName,
                        ExploredChunks = coords
                    };

                    int sent = 0;
                    foreach (var target in api.World.AllOnlinePlayers)
                    {
                        if (target == player) continue;
                        api.Network.GetChannel("mapsharer:sharemap").SendPacket(packet, (IServerPlayer)target);
                        sent++;
                    }

                    player.SendMessage(GlobalConstants.GeneralChatGroup, $"[MapSharer] Ваша карта ({coords.Count} областей) отправлена {sent} игрокам.", EnumChatType.Notification);
                    api.Logger.Notification($"[MapSharer] {player.PlayerName} поделился {coords.Count} чанками.");
                    return TextCommandResult.Success();
                });

            api.Logger.Notification("[MapSharer] Серверная часть загружена. Используйте /sharemap");
        }

        // ------ Клиентская часть ------
        public override void StartClientSide(ICoreClientAPI api)
        {
            base.StartClientSide(api);
            capi = api;

            var channel = api.Network.RegisterChannel("mapsharer:sharemap");
            channel.RegisterMessageType(typeof(MapDataPacket));
            channel.SetMessageHandler<MapDataPacket>(OnClientReceivedMapData);

            api.Logger.Notification("[MapSharer] Клиентская часть загружена.");
        }

        /// <summary>
        /// Вызывается на клиенте при получении пакета с данными карты от сервера.
        /// </summary>
        private void OnClientReceivedMapData(MapDataPacket packet)
        {
            if (capi == null || packet == null || packet.ExploredChunks.Count == 0)
                return;

            capi.ShowChatMessage($"[MapSharer] Получена карта от {packet.PlayerName}. Обновление...");
            capi.Logger.Notification($"[MapSharer] Начало обработки {packet.ExploredChunks.Count} чанков.");

            try
            {
                // Определяем путь к map.db через SavegameIdentifier (как в игре)
                string mapDbPath = GetLocalMapDbPath();
                if (!System.IO.File.Exists(mapDbPath))
                {
                    capi.ShowChatMessage($"[MapSharer] Файл карты не найден: {mapDbPath}");
                    capi.Logger.Error($"[MapSharer] map.db отсутствует по пути {mapDbPath}");
                    return;
                }
                capi.Logger.Notification($"[MapSharer] map.db найден: {mapDbPath}");

                // Работа с базой данных через MapDB
                using (var mapDb = new MapDB(capi.Logger))
                {
                    string? errorMsg = null;
                    mapDb.OpenOrCreate(mapDbPath, ref errorMsg, false, true, false);
                    if (errorMsg != null)
                    {
                        capi.ShowChatMessage($"[MapSharer] Ошибка открытия базы: {errorMsg}");
                        capi.Logger.Error($"[MapSharer] Ошибка открытия БД: {errorMsg}");
                        return;
                    }
                    capi.Logger.Notification("[MapSharer] База данных успешно открыта.");

                    var piecesToUpdate = new Dictionary<FastVec2i, MapPieceDB>();

                    foreach (var coord in packet.ExploredChunks)
                    {
                        FastVec2i chunkPos = new FastVec2i(coord.X, coord.Z);
                        MapPieceDB? piece = mapDb.GetMapPiece(chunkPos);
                        if (piece?.Pixels == null)
                        {
                            capi.Logger.Debug($"[MapSharer] Чанк ({coord.X},{coord.Z}) не найден в БД, пропускаем.");
                            continue;
                        }

                        bool changed = false;
                        // Заменяем неисследованные пиксели (0) на белый цвет (0xFFFFFFFF)
                        for (int i = 0; i < piece.Pixels.Length; i++)
                        {
                            if (piece.Pixels[i] == 0)
                            {
                                piece.Pixels[i] = unchecked((int)0xFFFFFFFF);
                                changed = true;
                            }
                        }

                        if (!changed) continue;

                        piecesToUpdate[chunkPos] = piece;
                        capi.Logger.Debug($"[MapSharer] Чанк ({coord.X},{coord.Z}) подготовлен к обновлению.");
                    }

                    if (piecesToUpdate.Count > 0)
                    {
                        mapDb.SetMapPieces(piecesToUpdate); // сохраняем за одну транзакцию
                        capi.ShowChatMessage($"[MapSharer] Сохранено {piecesToUpdate.Count} областей карты.");
                        capi.Logger.Notification($"[MapSharer] Сохранено {piecesToUpdate.Count} чанков.");
                    }
                    else
                    {
                        capi.ShowChatMessage($"[MapSharer] Новых областей для обновления не найдено.");
                    }
                }

                // Обновляем отображение карты через рефлексию
                RefreshMapRegions(packet.ExploredChunks);
                capi.ShowChatMessage($"[MapSharer] Обновление карты завершено!");
                capi.Logger.Notification($"[MapSharer] Карта успешно обновлена.");
            }
            catch (Exception ex)
            {
                capi.Logger.Error($"[MapSharer] Ошибка обновления карты: {ex}");
                capi.ShowChatMessage($"[MapSharer] Ошибка: {ex.Message}");
            }
        }

        /// <summary>
        /// Формирует путь к файлу map.db текущего мира, используя SavegameIdentifier.
        /// </summary>
        private string GetLocalMapDbPath()
        {
            string path = System.IO.Path.Combine(GamePaths.DataPath, "Maps");
            if (!System.IO.Directory.Exists(path))
                System.IO.Directory.CreateDirectory(path);
            return System.IO.Path.Combine(path, capi!.World.SavegameIdentifier + ".db");
        }

        /// <summary>
        /// Перезагружает регионы карты, соответствующие переданным чанкам, через рефлексию.
        /// </summary>
        private void RefreshMapRegions(List<ChunkCoord> chunks)
        {
            if (capi == null) return;

            var worldMapManagerField = capi.GetType().GetField("worldMapManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (worldMapManagerField == null)
            {
                capi.Logger.Warning("[MapSharer] Не удалось найти WorldMapManager. Карта может не обновиться.");
                capi.ShowChatMessage("[MapSharer] Не удалось обновить отображение карты, но данные сохранены.");
                return;
            }

            var worldMapManager = worldMapManagerField.GetValue(capi);
            if (worldMapManager == null) return;

            var refreshMethod = worldMapManager.GetType().GetMethod("RefreshMapRegion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (refreshMethod == null)
            {
                capi.Logger.Warning("[MapSharer] Метод RefreshMapRegion не найден. Карта может не обновиться.");
                capi.ShowChatMessage("[MapSharer] Не удалось обновить отображение карты, но данные сохранены.");
                return;
            }

            // Координаты регионов (один регион = 16x16 чанков)
            var regions = new HashSet<FastVec2i>();
            foreach (var chunk in chunks)
            {
                int regionX = chunk.X / 16;
                int regionZ = chunk.Z / 16;
                regions.Add(new FastVec2i(regionX, regionZ));
            }

            capi.Logger.Notification($"[MapSharer] Обновление {regions.Count} регионов карты.");
            foreach (var region in regions)
            {
                try
                {
                    refreshMethod.Invoke(worldMapManager, new object[] { region });
                    capi.Logger.Debug($"[MapSharer] Обновлён регион ({region.X}, {region.Y})");
                }
                catch (Exception ex)
                {
                    capi.Logger.Error($"[MapSharer] Ошибка обновления региона ({region.X},{region.Y}): {ex.Message}");
                }
            }
        }
    }
}