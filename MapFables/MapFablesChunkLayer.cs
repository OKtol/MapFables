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

    private ConcurrentDictionary<(int x, int z), byte[]> pendingPixels = new();
    private ConcurrentDictionary<(int x, int z), LoadedTexture> chunkTextures = new();
    private ConcurrentDictionary<(int x, int z), float> chunkTTL = new();
    private const float TTL_MAX = float.MaxValue;

    private ICoreClientAPI? capi;
    private bool firstFrame = true;
    private Vec2f tmpViewPos = new();
    private Vec3d tmpWorldPos = new();

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

        // ОТЛАДКА: красная заливка, чтобы убедиться, что слой жив
        capi.Render.RenderRectangle(
            (float)map.Bounds.renderX,
            (float)map.Bounds.renderY,
            (float)map.Bounds.OuterWidth,
            (float)map.Bounds.OuterHeight,
            50f,
            ColorUtil.ColorFromRgba(150, 255, 0, 0)
        );

        // Перенос пикселей из общей очереди
        while (MapFablesModSystem.ReceivedPixels.TryDequeue(out var item))
        {
            pendingPixels.TryAdd((item.x, item.z), item.pixels);
        }

        // Создание текстур
        if (!pendingPixels.IsEmpty)
        {
            int createdThisFrame = 0;
            var keys = pendingPixels.Keys.ToArray();
            foreach (var key in keys)
            {
                if (pendingPixels.TryRemove(key, out byte[]? pixels) && pixels != null)
                {
                    int[] rgbaPixels = new int[pixels.Length];
                    Buffer.BlockCopy(pixels, 0, rgbaPixels, 0, pixels.Length);

                    LoadedTexture tex = new LoadedTexture(capi, 0, GlobalConstants.ChunkSize, GlobalConstants.ChunkSize);
                    capi.Render.LoadOrUpdateTextureFromRgba(rgbaPixels, false, 0, ref tex);
                    chunkTextures[key] = tex;
                    chunkTTL[key] = TTL_MAX;
                    createdThisFrame++;
                }
            }
            if (createdThisFrame > 0)
                api.Logger.Notification("[MapFables] Created {0} textures this frame. Total textures: {1}.", createdThisFrame, chunkTextures.Count);
        }

        int renderedThisFrame = 0;
        float zoom = map.ZoomLevel;

        foreach (var kv in chunkTextures)
        {
            var key = kv.Key;
            chunkTTL[key] = TTL_MAX;

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

            if (firstFrame)
            {
                api.Logger.Notification("[MapFables] First chunk ({0},{1}) viewPos=({2:F1},{3:F1}), screen=({4:F1},{5:F1}), zoom={6}, mapBounds=({7},{8})-({9},{10})",
                    key.x, key.z, tmpViewPos.X, tmpViewPos.Y,
                    map.Bounds.renderX + tmpViewPos.X, map.Bounds.renderY + tmpViewPos.Y,
                    zoom,
                    map.Bounds.renderX, map.Bounds.renderY,
                    map.Bounds.renderX + map.Bounds.OuterWidth, map.Bounds.renderY + map.Bounds.OuterHeight);
                firstFrame = false;
            }
        }

        if (renderedThisFrame > 0)
            api.Logger.VerboseDebug("[MapFables] Rendered {0} chunks.", renderedThisFrame);
    }

    public override void Dispose()
    {
        api.Logger.Notification("[MapFables] Disposing layer. Textures: {0}, Pending: {1}.", chunkTextures.Count, pendingPixels.Count);
        foreach (var tex in chunkTextures.Values) tex.Dispose();
        chunkTextures.Clear();
        pendingPixels.Clear();
        base.Dispose();
    }
}