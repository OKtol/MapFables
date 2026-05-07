using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

    private ConcurrentDictionary<(int x, int z), LoadedTexture> chunkTextures = new();
    private ConcurrentDictionary<(int x, int z), float> chunkTTL = new();
    private const float TTL_MAX = float.MaxValue; // текстуры живут вечно при открытой карте

    private ICoreClientAPI? capi;
    private Vec2f tmpViewPos = new();
    private Vec3d tmpWorldPos = new();
    private int renderedCountLog = 0;

    public MapFablesChunkLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
    {
        capi = api as ICoreClientAPI;
    }

    public override void OnDataFromServer(byte[] data) { }

    public override void OnMapOpenedClient()
    {
        api.Logger.Notification("[MapFables] Map opened. Resetting TTL for all {0} loaded chunks.", chunkTextures.Count);
        foreach (var key in chunkTextures.Keys)
            chunkTTL[key] = TTL_MAX;
    }

    public override void Render(GuiElementMap map, float dt)
    {
        if (!Active || capi == null) return;

        // Переносим пиксели из очереди и сразу создаём/обновляем текстуры
        int newChunks = 0, updatedChunks = 0;
        while (MapFablesModSystem.ReceivedPixels.TryDequeue(out var item))
        {
            byte[] pixels = item.pixels;
            int[] rgbaPixels = new int[pixels.Length];
            Buffer.BlockCopy(pixels, 0, rgbaPixels, 0, pixels.Length);

            LoadedTexture tex;
            if (chunkTextures.TryGetValue((item.x, item.z), out tex))
            {
                // Обновляем существующую текстуру
                capi.Render.LoadOrUpdateTextureFromRgba(rgbaPixels, false, tex.TextureId, ref tex);
                updatedChunks++;
            }
            else
            {
                // Создаём новую
                tex = new LoadedTexture(capi, 0, GlobalConstants.ChunkSize, GlobalConstants.ChunkSize);
                capi.Render.LoadOrUpdateTextureFromRgba(rgbaPixels, false, 0, ref tex);
                chunkTextures[(item.x, item.z)] = tex;
                newChunks++;
            }
            chunkTTL[(item.x, item.z)] = TTL_MAX;
        }

        if (newChunks > 0 || updatedChunks > 0)
            api.Logger.Notification("[MapFables] Processed queue: created {0}, updated {1} textures. Total loaded: {2}.",
                newChunks, updatedChunks, chunkTextures.Count);

        // Текущий рендер и обслуживание TTL
        List<(int x, int z)> toRemove = new();
        int renderedThisFrame = 0;
        float zoom = map.ZoomLevel;

        foreach (var kv in chunkTextures)
        {
            var key = kv.Key;
            if (chunkTTL.TryGetValue(key, out float ttl))
            {
                ttl -= dt;
                if (ttl <= 0)
                {
                    kv.Value.Dispose();
                    toRemove.Add(key);
                    continue;
                }
                chunkTTL[key] = ttl;
            }

            tmpWorldPos.Set(key.x * GlobalConstants.ChunkSize, 0, key.z * GlobalConstants.ChunkSize);
            map.TranslateWorldPosToViewPos(tmpWorldPos, ref tmpViewPos);

            capi.Render.Render2DTexture(
                kv.Value.TextureId,
                (int)(map.Bounds.renderX + tmpViewPos.X),
                (int)(map.Bounds.renderY + tmpViewPos.Y),
                (int)(GlobalConstants.ChunkSize * zoom),
                (int)(GlobalConstants.ChunkSize * zoom),
                50f
            );
            renderedThisFrame++;
        }

        foreach (var key in toRemove)
        {
            chunkTextures.TryRemove(key, out _);
            chunkTTL.TryRemove(key, out _);
        }

        // Периодическое логирование рендера
        if (renderedThisFrame != renderedCountLog)
        {
            api.Logger.VerboseDebug("[MapFables] Rendered {0} chunks. Total loaded: {1}.", renderedThisFrame, chunkTextures.Count);
            renderedCountLog = renderedThisFrame;
        }
    }

    public override void Dispose()
    {
        api.Logger.Notification("[MapFables] Disposing layer. Textures: {0}.", chunkTextures.Count);
        foreach (var tex in chunkTextures.Values) tex.Dispose();
        chunkTextures.Clear();
        chunkTTL.Clear();
        base.Dispose();
    }
}