using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace MapFables.Patches;

[HarmonyPatch(typeof(ChunkMapLayer))]
public static class ChunkMapLayerPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(ChunkMapLayer.GenerateChunkImage))]
    public static bool Prefix(ChunkMapLayer __instance, FastVec2i chunkPos, ref int[] __result)
    {
        var key = (chunkPos.X, chunkPos.Y);
        if (MapFablesClientSystem.SharedChunkImages.TryGetValue(key, out var pixels))
        {
            __result = pixels;
            return false; // Пропустить оригинальный метод (он требует загруженных данных)
        }
        return true; // Вызвать оригинал (вернёт null, если чанк не открыт)
    }
}