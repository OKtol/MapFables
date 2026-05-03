using System;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace MapFables;

public class MapSharerModSystem : ModSystem
{
    private ICoreServerAPI? serverApi;

    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);
        serverApi = api;
        
        // Регистрируем команду /sharemap
        api.ChatCommands.Create("sharemap")
            .WithDescription("Поделиться своей исследованной картой с другими игроками")
            .RequiresPrivilege(Privilege.chat)
            .HandleWith(args =>
            {
                // Получаем игрока, который ввёл команду
                var player = args.Caller.Player as IServerPlayer;

                if (player == null)
                {
                    api.Logger.Error("[MapSharer] Не удалось определить игрока, вызвавшего команду.");
                    return TextCommandResult.Error("Не удалось определить игрока");
                }

                // Запускаем основной процесс обмена картой
                ShareMap(api, player);

                return TextCommandResult.Success("Ваша карта передаётся другим игрокам...");
            });

        api.Logger.Chat("[MapSharer][Notification] Мод успешно загружен на сервере. Команда /sharemap доступна.");
    }

    /// <summary>
    /// Основная логика: перебирает все загруженные map‑чанки, проверяет, исследовал ли их источник, и отправляет их всем остальным онлайн‑игрокам.
    /// </summary>
    /// <param name="api"></param>
    /// <param name="sourcePlayer"></param>
    private static void ShareMap(ICoreServerAPI api, IServerPlayer sourcePlayer)
    {
        // Получаем доступ к IWorldManagerAPI (через свойство WorldManager)
        var worldManager = api.WorldManager;
        if (worldManager == null)
        {
            api.Logger.Chat("[MapSharer][Error] WorldManager недоступен. Операция прервана.");
            sourcePlayer.SendMessage(GlobalConstants.GeneralChatGroup, "[MapSharer] Ошибка: менеджер мира недоступен.", EnumChatType.Notification);
            return;
        }

        api.Logger.Chat($"[MapSharer][Notification] Игрок {sourcePlayer.PlayerName} начал передачу своей исследованной карты.");

        // 1. Получаем СЛОВАРЬ всех загруженных MAP-чанков (ключ = 2D-индекс чанка)
        //    AllLoadedMapchunks определён в IWorldManagerAPI.
        var allMapChunks = worldManager.AllLoadedMapchunks;
        if (allMapChunks == null || allMapChunks.Count == 0)
        {
            api.Logger.Chat("[MapSharer][Warning] Нет загруженных map-чанков на сервере. Возможно, игрок ещё не исследовал территорию.");
            sourcePlayer.SendMessage(GlobalConstants.GeneralChatGroup, "[MapSharer] Нет исследованных областей для передачи.", EnumChatType.Notification);
            return;
        }

        api.Logger.Chat($"[MapSharer][Notification] Всего загружено map-чанков на сервере: {allMapChunks.Count}");

        int sentChunksTotal = 0;       // Сколько чанков было отправлено хотя бы одному игроку
        int playersOnline = api.World.AllOnlinePlayers.Length;

        // Перебираем каждый загруженный map-чанк
        foreach (var kvp in allMapChunks)
        {
            long chunkIndex2D = kvp.Key;    // 2D-индекс чанка (кодирует X и Z)

            // Преобразуем индекс в координаты X, Z чанка
            Vec2i chunkPos = worldManager.MapChunkPosFromChunkIndex2D(chunkIndex2D);
            if (chunkPos == null)
            {
                api.Logger.Chat($"[MapSharer][Warning] Не удалось получить координаты для индекса {chunkIndex2D}. Пропускаем.");
                continue;
            }

            int chunkX = chunkPos.X;
            int chunkZ = chunkPos.Y;

            // 2. Проверяем: исследовал ли ИСТОЧНИК (sourcePlayer) этот map-чанк?
            //    HasChunk(chunkX, chunkY, chunkZ, player) – есть ли у игрока данный чанк.
            //    Для map-чанка Y всегда 0 (map-чанки не имеют вертикальной координаты).
            bool sourceHasChunk = worldManager.HasChunk(chunkX, 0, chunkZ, sourcePlayer);

            if (!sourceHasChunk)
            {
                // Этот чанк не открыт у источника – пропускаем
                api.Logger.Chat($"[MapSharer][Debug] Игрок {sourcePlayer.PlayerName} не исследовал чанк ({chunkX}, {chunkZ}).");
                continue;
            }

            api.Logger.Chat($"[MapSharer][Debug] Чанк ({chunkX}, {chunkZ}) исследован {sourcePlayer.PlayerName}. Начинаем рассылку.");

            // 3. Отправляем этот map-чанк всем ОСТАЛЬНЫМ онлайн-игрокам
            int sentToTargets = 0;
            foreach (var targetPlayer in api.World.AllOnlinePlayers)
            {
                if (targetPlayer == sourcePlayer) continue; // себе не отправляем

                try
                {
                    // Используем метод ResendMapChunk из IWorldManagerAPI.
                    // Он отправляет map-чанк указанному игроку (или всем, если передать null).
                    // В документации: void ResendMapChunk(int chunkX, int chunkZ, bool onlyIfInRange, IServerPlayer player = null)
                    // Параметр onlyIfInRange = false – отправляем всегда, даже если игрок далеко.
                    // Третий параметр – конкретный игрок.
                    worldManager.ResendMapChunk(chunkX, chunkZ, false);
                    sentToTargets++;
                    sentChunksTotal++;

                    api.Logger.Chat($"[MapSharer][Debug] Чанк ({chunkX}, {chunkZ}) отправлен игроку {targetPlayer.PlayerName}");
                }
                catch (Exception ex)
                {
                    api.Logger.Chat($"[MapSharer][Error] Ошибка отправки чанка ({chunkX}, {chunkZ}) игроку {targetPlayer.PlayerName}: {ex.Message}");
                }
            }

            if (sentToTargets > 0)
            {
                api.Logger.Chat($"[MapSharer][Notification] Чанк ({chunkX}, {chunkZ}) отправлен {sentToTargets} из {playersOnline - 1} другим игрокам.");
            }
        }

        // Финальное уведомление
        api.Logger.Chat($"[MapSharer][Notification] Передача карты от {sourcePlayer.PlayerName} завершена. Всего отправлено уникальных map-чанков: {sentChunksTotal}");
        sourcePlayer.SendMessage(GlobalConstants.GeneralChatGroup, $"[MapSharer] Ваша карта передана ({sentChunksTotal} областей).", EnumChatType.Notification);
    }
}