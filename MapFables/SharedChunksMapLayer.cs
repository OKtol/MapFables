using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace MapFables;

public class SharedChunksMapLayer : MapLayer
{
    private readonly ICoreClientAPI capi;
    private HashSet<long> sharedChunks = new();
    private LoadedTexture? overlayTexture;
    private readonly object textureLock = new();
    private const int chunksize = GlobalConstants.ChunkSize;

    private float lastZoom;
    private double lastBoundsX1, lastBoundsZ1;

    public override string Title => "Shared Chunks";
    public override string LayerGroupCode => "shared";
    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
    public override bool RequireChunkLoaded => false;

    public SharedChunksMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
    {
        capi = (api as ICoreClientAPI) ?? throw new ArgumentNullException(nameof(api), "Must be client API");
        Active = true;
        ZIndex = 0;
    }

    public void AddSharedChunks(List<ChunkCoord> chunks)
    {
        lock (textureLock)
        {
            bool changed = false;
            foreach (var c in chunks)
            {
                long idx = ((long)c.X << 32) | (uint)c.Z;
                if (sharedChunks.Add(idx))
                    changed = true;
            }
            if (changed)
                InvalidateTexture();
        }
    }

    private void InvalidateTexture()
    {
        overlayTexture?.Dispose();
        overlayTexture = null;
    }

    private void RegenerateTexture(int width, int height, GuiElementMap mapElem)
    {
        lock (textureLock)
        {
            if (overlayTexture != null && overlayTexture.Width == width && overlayTexture.Height == height)
                return;

            var rgba = new int[width * height];
            for (int i = 0; i < rgba.Length; i++)
                rgba[i] = 0;

            var bounds = mapElem.CurrentBlockViewBounds;
            int startX = (int)Math.Floor(bounds.X1 / chunksize);
            int endX = (int)Math.Ceiling(bounds.X2 / chunksize);
            int startZ = (int)Math.Floor(bounds.Z1 / chunksize);
            int endZ = (int)Math.Ceiling(bounds.Z2 / chunksize);

            const int color = 0x3a86ff;
            const int alpha = 102;

            for (int x = startX; x <= endX; x++)
            {
                for (int z = startZ; z <= endZ; z++)
                {
                    long idx = ((long)x << 32) | (uint)z;
                    if (!sharedChunks.Contains(idx)) continue;

                    double worldX = x * chunksize;
                    double worldZ = z * chunksize;
                    Vec2f viewPos = new();
                    mapElem.TranslateWorldPosToViewPos(new Vec3d(worldX, 0, worldZ), ref viewPos);

                    int px = (int)viewPos.X;
                    int py = (int)viewPos.Y;
                    int w = (int)(chunksize * mapElem.ZoomLevel);
                    int h = (int)(chunksize * mapElem.ZoomLevel);

                    for (int dy = 0; dy < h; dy++)
                    {
                        int yTex = py + dy;
                        if (yTex < 0 || yTex >= height) continue;
                        for (int dx = 0; dx < w; dx++)
                        {
                            int xTex = px + dx;
                            if (xTex < 0 || xTex >= width) continue;
                            int idxPixel = yTex * width + xTex;
                            int existing = rgba[idxPixel];
                            int r = (existing >> 16) & 0xFF;
                            int g = (existing >> 8) & 0xFF;
                            int b = existing & 0xFF;
                            r = (r * (255 - alpha) + ((color >> 16) & 0xFF) * alpha) / 255;
                            g = (g * (255 - alpha) + ((color >> 8) & 0xFF) * alpha) / 255;
                            b = (b * (255 - alpha) + (color & 0xFF) * alpha) / 255;
                            rgba[idxPixel] = (255 << 24) | (r << 16) | (g << 8) | b;
                        }
                    }
                }
            }

            if (overlayTexture == null)
            {
                overlayTexture = new LoadedTexture(capi);
            }
            capi.Render.LoadOrUpdateTextureFromRgba(rgba, false, 0, ref overlayTexture);
        }
    }

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!Active || capi == null || sharedChunks.Count == 0) return;

        int width = (int)mapElem.Bounds.InnerWidth;
        int height = (int)mapElem.Bounds.InnerHeight;
        var bounds = mapElem.CurrentBlockViewBounds;

        bool needsRegen = overlayTexture == null ||
                          overlayTexture.Width != width ||
                          overlayTexture.Height != height ||
                          lastZoom != mapElem.ZoomLevel ||
                          Math.Abs(lastBoundsX1 - bounds.X1) > chunksize * mapElem.ZoomLevel ||
                          Math.Abs(lastBoundsZ1 - bounds.Z1) > chunksize * mapElem.ZoomLevel;

        if (needsRegen)
        {
            RegenerateTexture(width, height, mapElem);
            lastZoom = mapElem.ZoomLevel;
            lastBoundsX1 = bounds.X1;
            lastBoundsZ1 = bounds.Z1;
        }

        if (overlayTexture != null && !overlayTexture.Disposed)
        {
            capi.Render.Render2DTexture(overlayTexture.TextureId,
                (int)mapElem.Bounds.renderX, (int)mapElem.Bounds.renderY,
                width, height, 51);
        }
    }

    public override void Dispose()
    {
        overlayTexture?.Dispose();
        base.Dispose();
    }
}