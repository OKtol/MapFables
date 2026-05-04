using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace MapFables;

internal class MapFablesClientSystem : ModSystem
{
    private ICoreClientAPI capi = null!;
    private IClientNetworkChannel clientChannel = null!;
    private SharedChunksMapLayer? sharedLayer;

    // Локальное хранилище открытых чанков (текущая сессия)
    private HashSet<long> locallyExploredChunks = new();

    public static MapFablesClientSystem? Instance { get; private set; }
    private static long ChunkToIndex(int x, int z) => (long)x << 32 | (uint)z;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;
    public override double ExecuteOrder() => 0.11;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        Instance = this;

        clientChannel = api.Network.GetChannel(MapFablesModSystem.NetworkChannelName);
        clientChannel.SetMessageHandler<MapDataPacket>(OnReceivedMapData);
        clientChannel.SetMessageHandler<ShareMapRequest>(OnShareMapRequest);  // обработчик запроса

        // Добавляем слой отображения чужих чанков
        api.Event.LevelFinalize += () =>
        {
            var mapManager = api.ModLoader.GetModSystem<WorldMapManager>();
            if (mapManager != null)
            {
                sharedLayer = new SharedChunksMapLayer(api, mapManager);
                mapManager.MapLayers.Add(sharedLayer);
                api.Logger.Notification("[MapFables] Shared chunks layer added.");
            }
        };
    }

    // Вызывается из патча при обнаружении нового чанка
    public void AddDiscoveredChunk(int x, int z)
    {
        long idx = ChunkToIndex(x, z);
        if (locallyExploredChunks.Add(idx))
        {
            capi.Logger.Notification($"[MapFables] Discovered new chunk ({x}, {z})");
        }
    }

    // Ответ на запрос от сервера – отправляем свои чанки
    private void OnShareMapRequest(ShareMapRequest _)
    {
        if (locallyExploredChunks.Count == 0)
        {
            capi.ShowChatMessage("[MapFables] You haven't discovered any chunks yet.");
            return;
        }

        var coords = locallyExploredChunks.Select(idx =>
        {
            int x = (int)(idx >> 32);
            int z = (int)(idx & 0xFFFFFFFF);
            return new ChunkCoord(x, z);
        }).ToList();

        var packet = new MapDataPacket
        {
            PlayerName = capi.World.Player.PlayerName,
            ExploredChunks = coords
        };

        clientChannel.SendPacket(packet);
        capi.ShowChatMessage($"[MapFables] Sent your map ({locallyExploredChunks.Count} chunks) to the server for sharing.");
    }

    private void OnReceivedMapData(MapDataPacket packet)
    {
        if (packet?.ExploredChunks == null || packet.ExploredChunks.Count == 0)
            return;

        capi.ShowChatMessage($"[MapFables] Received map from {packet.PlayerName} ({packet.ExploredChunks.Count} chunks).");

        if (sharedLayer != null)
            sharedLayer.AddSharedChunks(packet.ExploredChunks);
        else
            capi.Logger.Warning("[MapFables] Shared layer not ready, data will be ignored.");
    }

    public override void Dispose()
    {
        Instance = null;
        base.Dispose();
    }
}