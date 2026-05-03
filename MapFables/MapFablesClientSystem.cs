using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace MapFables;

internal class MapFablesClientSystem : ModSystem
{
    ICoreClientAPI clientApi = null!;
    // private WorldMapManager _mapManager = null!;
    // IClientNetworkChannel clientChannel = null!;

    /// <summary>
    /// This is a client-side system, so we only want it to load on the client.
    /// </summary>
    public override bool ShouldLoad(EnumAppSide forSide)
    {
        return forSide == EnumAppSide.Client;
    }

    /// <summary>
    /// This mod system needs to be loaded after the <see cref="MapFablesModSystem"/> to ensure that the network channel is registered, 
    ///   so we have to give this a slightly higher execute order.
    /// </summary>
    public override double ExecuteOrder()
    {
        //Default is 0.1.
        return 0.11f;
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        clientApi = api;
        //_mapManager = clientApi.ModLoader.GetModSystem<WorldMapManager>();
        var clientChannel = api.Network.GetChannel(Mod.Info.ModID + ".networkchannel");

        clientChannel.SetMessageHandler<MapDataRequest>(OnClientReceivedMapData);
    }


    private void OnClientReceivedMapData(MapDataRequest packet)
    {
        if (clientApi == null || packet == null || packet.ExploredChunks.Count == 0)
            return;

        clientApi.ShowChatMessage($"[MapFables] Получена карта от {packet.PlayerName}. Обновление...");
        clientApi.Logger.Notification($"[MapFables] Начало обработки {packet.ExploredChunks.Count} чанков.");
    }
}
