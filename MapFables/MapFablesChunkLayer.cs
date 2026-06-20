using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace MapFables
{
    /// <summary>
    /// Кастомный слой карты для отображения чанков, полученных от другого игрока.
    ///
    /// Используем MultiChunkMapComponent — тот же класс, что и стандартный ChunkMapLayer.
    /// Он группирует чанки по 3×3 и рендерит их через фреймбуфер в одну текстуру,
    /// что правильно обрабатывает мировые координаты и масштаб карты.
    ///
    /// Данные хранятся только в памяти — не сохраняются в MapDB.
    /// </summary>
    public class MapFablesChunkLayer : MapLayer
    {
        public override string Title => "MapFables Shared";
        public override string LayerGroupCode => "mapfableschunk";
        public override EnumMapAppSide DataSide => EnumMapAppSide.Client;

        // Словарь: координата MultiChunkMapComponent (в единицах групп 3×3) → компонент
        private readonly Dictionary<FastVec2i, MultiChunkMapComponent> sharedMapData = new();

        private ICoreClientAPI? capi;

        public MapFablesChunkLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
        {
            capi = api as ICoreClientAPI;
            api.Logger.Notification("[MapFables] ChunkLayer constructor called");
        }

        public override void OnLoaded()
        {
            base.OnLoaded();
            api.Logger.Notification("[MapFables] ChunkLayer OnLoaded");
        }

        /// <summary>
        /// Вызывается на главном потоке каждые ~20мс через WorldMapManager.OnClientTick().
        /// Здесь безопасно создавать OpenGL текстуры.
        /// Забираем пиксели из статической очереди и загружаем в MultiChunkMapComponent.
        /// </summary>
        public override void OnTick(float dt)
        {
            if (MapFablesModSystem.ReceivedPixels.IsEmpty) return;

            // Обрабатываем не более 50 чанков за тик, чтобы не фризить игру
            int toProcess = System.Math.Min(MapFablesModSystem.ReceivedPixels.Count, 50);
            var modified = new List<MultiChunkMapComponent>();

            while (toProcess-- > 0 && MapFablesModSystem.ReceivedPixels.TryDequeue(out var item))
            {
                // MultiChunkMapComponent группирует чанки по сетке ChunkLen×ChunkLen (3×3).
                // mcCoord — координата группы, baseCoord — координата первого чанка в группе.
                var mcCoord = new FastVec2i(
                    item.x / MultiChunkMapComponent.ChunkLen,
                    item.z / MultiChunkMapComponent.ChunkLen);
                var baseCoord = new FastVec2i(
                    mcCoord.X * MultiChunkMapComponent.ChunkLen,
                    mcCoord.Y * MultiChunkMapComponent.ChunkLen);

                if (!sharedMapData.TryGetValue(mcCoord, out var comp))
                {
                    // Создаём новый компонент для этой группы 3×3
                    // baseCoord передаётся в блочных координатах внутри конструктора:
                    // worldPos = baseCoord * chunkSize
                    comp = new MultiChunkMapComponent(capi!, baseCoord);
                    sharedMapData[mcCoord] = comp;
                }

                // dx/dz — смещение чанка внутри группы 3×3 (0, 1 или 2)
                int dx = item.x - baseCoord.X;
                int dz = item.z - baseCoord.Y;

                comp.setChunk(dx, dz, item.pixels);
                modified.Add(comp);
            }

            // Загружаем пиксели в GPU-текстуру через фреймбуфер
            foreach (var comp in modified)
            {
                comp.FinishSetChunks();
            }
        }

        /// <summary>
        /// Рендерит все полученные чанки на карте.
        /// Вызывается каждый кадр из GuiElementMap.RenderInteractiveElements().
        /// MultiChunkMapComponent сам вычисляет экранные координаты через
        /// map.TranslateWorldPosToViewPos() — поэтому масштаб и позиция всегда корректны.
        /// </summary>
        public override void Render(GuiElementMap map, float dt)
        {
            if (!Active || capi == null) return;

            foreach (var val in sharedMapData)
            {
                val.Value.Render(map, dt);
            }
        }

        public override void Dispose()
        {
            foreach (var val in sharedMapData)
            {
                val.Value?.ActuallyDispose();
            }
            sharedMapData.Clear();
            base.Dispose();
        }
    }
}
