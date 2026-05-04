using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace MapFables.Patches;

[HarmonyPatch(typeof(ChunkMapLayer))]
public static class ChunkMapLayerPatch
{
    private static HashSet<long> alreadyDiscovered = new();

    private static long ChunkToIndex(int x, int z) => (long)x << 32 | (uint)z;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(ChunkMapLayer.OnViewChangedClient))]
    public static void OnViewChangedClient(ChunkMapLayer __instance, List<FastVec2i> nowVisible, List<FastVec2i> nowHidden)
    {
        var clientSystem = MapFablesClientSystem.Instance;
        if (clientSystem == null) return;

        foreach (var chunk in nowVisible)
        {
            long idx = ChunkToIndex(chunk.X, chunk.Y);
            if (alreadyDiscovered.Add(idx))
            {
                clientSystem.AddDiscoveredChunk(chunk.X, chunk.Y);
            }
        }
    }
}