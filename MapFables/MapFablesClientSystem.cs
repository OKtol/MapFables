using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace MapFables;

internal class MapFablesClientSystem : ModSystem
{
    private ICoreClientAPI capi = null!;
    private IClientNetworkChannel clientChannel = null!;

    // Кэш чужих чанков (координата → пиксели)
    public static Dictionary<(int x, int z), int[]> SharedChunkImages { get; } = new();

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;
    public override double ExecuteOrder() => 0.11; // после WorldMapManager

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        clientChannel = api.Network.GetChannel(MapFablesModSystem.NetworkChannelName);
        clientChannel.SetMessageHandler<MapDataPacket>(OnReceivedMapData);
    }

    private void OnReceivedMapData(MapDataPacket packet)
    {
        if (packet?.Chunks == null || packet.Chunks.Count == 0) return;

        capi.ShowChatMessage($"[MapFables] Received map data from {packet.SenderName} ({packet.Chunks.Count} chunks).");

        bool added = false;
        lock (SharedChunkImages)
        {
            foreach (var chunk in packet.Chunks)
            {
                if (chunk.Pixels != null && chunk.Pixels.Length > 0)
                {
                    var key = (chunk.X, chunk.Z);
                    if (!SharedChunkImages.ContainsKey(key))
                    {
                        SharedChunkImages[key] = chunk.Pixels;
                        added = true;
                    }
                }
            }
        }

        if (added)
        {
            capi.ShowChatMessage("[MapFables] New chunks added to the map. They will appear when you view the area.");
        }
    }
}