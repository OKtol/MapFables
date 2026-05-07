using System.Collections.Concurrent;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace MapFables;

public class MapFablesChunkLayer : MapLayer
{
    public override string Title => "MapFables Shared";
    public override string LayerGroupCode => "mapfableschunk";
    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;

    private readonly ConcurrentDictionary<(int x, int z), LoadedTexture> chunkTextures = new();
    private readonly ConcurrentDictionary<(int x, int z), float> chunkTTL = new();
    private const float TTL_MAX = 300f;

    private ICoreClientAPI? capi;
    private Vec2f tmpViewPos = new();
    private Vec3d tmpWorldPos = new();

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

    public override void Render(GuiElementMap map, float dt)
    {
        // Отладка: вызывается ли Render и сколько данных в очереди
        api.Logger.VerboseDebug("[MapFables] Render called, queue size: {0}, textures: {1}",
            MapFablesModSystem.ReceivedPixels.Count, chunkTextures.Count);

        if (!Active || capi == null) return;

        // --- Загрузка текстур из очереди ---
        int newCount = 0;
        int debugCreated = 0; // счётчик для отладки первых 5 чанков
        while (MapFablesModSystem.ReceivedPixels.TryDequeue(out var item))
        {
            var key = (item.x, item.z);

            // Удаляем старую текстуру, если есть
            if (chunkTextures.TryRemove(key, out var oldTex))
                oldTex.Dispose();

            // Создаём новую текстуру
            var tex = new LoadedTexture(capi, 0, GlobalConstants.ChunkSize, GlobalConstants.ChunkSize);
            capi.Render.LoadOrUpdateTextureFromRgba(item.pixels, false, 0, ref tex);
            chunkTextures[key] = tex;
            chunkTTL[key] = TTL_MAX;

            // Отладка: выводим координаты первых 5 чанков
            if (debugCreated < 5)
            {
                capi.Logger.Notification("[MapFables] Creating texture for chunk ({0},{1})", item.x, item.z);
                debugCreated++;
            }

            newCount++;
        }

        if (newCount > 0)
            api.Logger.Notification("[MapFables] Textures created: {0}, total: {1}", newCount, chunkTextures.Count);

        // --- Рендеринг всех активных текстур ---
        float zoom = map.ZoomLevel;
        bool debugPosPrinted = false;
        foreach (var kv in chunkTextures)
        {
            if (kv.Value.Disposed) continue;

            var key = kv.Key;
            tmpWorldPos.Set(
                key.x * GlobalConstants.ChunkSize,
                0,
                key.z * GlobalConstants.ChunkSize);
            map.TranslateWorldPosToViewPos(tmpWorldPos, ref tmpViewPos);

            // Отладка: выводим мировые и экранные координаты первого чанка
            if (!debugPosPrinted)
            {
                capi.Logger.Notification("[MapFables] Render: world ({0},{1}) -> view ({2},{3}) bounds renderX {4} renderY {5} zoom {6}",
                    tmpWorldPos.X, tmpWorldPos.Z, tmpViewPos.X, tmpViewPos.Y,
                    map.Bounds.renderX, map.Bounds.renderY, zoom);
                debugPosPrinted = true;
            }

            capi.Render.Render2DTexture(
                kv.Value.TextureId,
                (int)(map.Bounds.renderX + tmpViewPos.X),
                (int)(map.Bounds.renderY + tmpViewPos.Y),
                (int)(GlobalConstants.ChunkSize * zoom),
                (int)(GlobalConstants.ChunkSize * zoom),
                50f  // Z-depth: поверх terrain (ChunkMapLayer рендерится на 50 тоже)
            );
        }
    }

    public override void OnTick(float dt)
    {
        var toRemove = new List<(int x, int z)>();
        foreach (var kv in chunkTTL)
        {
            float ttl = kv.Value - dt;
            if (ttl <= 0) toRemove.Add(kv.Key);
            else chunkTTL[kv.Key] = ttl;
        }
        foreach (var key in toRemove)
        {
            if (chunkTextures.TryRemove(key, out var tex))
                tex.Dispose();
            chunkTTL.TryRemove(key, out _);
        }
    }

    public override void Dispose()
    {
        foreach (var tex in chunkTextures.Values)
            if (!tex.Disposed) tex.Dispose();
        chunkTextures.Clear();
        chunkTTL.Clear();
        base.Dispose();
    }
}