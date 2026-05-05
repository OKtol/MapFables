using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace MapFables.Patches;

/// <summary>
/// Патч метода ChunkMapLayer.GenerateChunkImage.
///
/// Сигнатура в VS 1.20+:
///   void GenerateChunkImage(FastVec2i chunkPos, IMapChunk mc, bool colorAccurate)
///
/// Если для данного чанка в SharedChunkImages есть кэшированные пиксели —
/// записываем их в MapChunk.Pixels напрямую через рефлексию и пропускаем
/// оригинальную генерацию (return false).
///
/// Рефлексия нужна потому, что поле Pixels есть в конкретном классе MapChunk,
/// но не объявлено в интерфейсе IMapChunk.
/// </summary>
[HarmonyPatch(typeof(ChunkMapLayer))]
[HarmonyPatch(nameof(ChunkMapLayer.GenerateChunkImage))]
public static class ChunkMapLayerPatch
{
    // Кэшируем FieldInfo, чтобы не искать через рефлексию при каждом вызове
    private static FieldInfo? s_pixelsField;
    private static bool s_fieldResolved;

    /// <summary>
    /// Ищем поле Pixels в реальном типе mc один раз, результат кэшируем.
    /// </summary>
    private static FieldInfo? GetPixelsField(IMapChunk mc)
    {
        if (s_fieldResolved)
            return s_pixelsField;

        s_fieldResolved = true;
        s_pixelsField = mc.GetType().GetField(
            "Pixels",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (s_pixelsField == null)
        {
            MapFablesClientSystem.Api?.Logger.Warning(
                "[MapFables] Could not find 'Pixels' field on {0}. " +
                "Patch will be disabled.", mc.GetType().FullName);
        }

        return s_pixelsField;
    }

    [HarmonyPrefix]
    public static bool Prefix(FastVec2i chunkPos, IMapChunk mc)
    {
        // chunkPos.Y здесь — это Z (FastVec2i использует X/Y для 2D, Y = Z в мире)
        var key = (chunkPos.X, chunkPos.Y);

        if (!MapFablesClientSystem.SharedChunkImages.TryGetValue(key, out var pixels))
            return true; // кэша нет — пускаем оригинальный метод

        var pixelsField = GetPixelsField(mc);
        if (pixelsField == null)
            return true; // поле не найдено — не ломаем оригинал

        try
        {
            pixelsField.SetValue(mc, pixels);

            MapFablesClientSystem.Api?.Logger.VerboseDebug(
                "[MapFables] Injected cached pixels for chunk ({0},{1})",
                chunkPos.X, chunkPos.Y);

            return false; // оригинальный метод пропускаем
        }
        catch (Exception ex)
        {
            MapFablesClientSystem.Api?.Logger.Warning(
                "[MapFables] Failed to set Pixels on chunk ({0},{1}): {2}",
                chunkPos.X, chunkPos.Y, ex.Message);

            return true; // при ошибке — пускаем оригинал
        }
    }
}
