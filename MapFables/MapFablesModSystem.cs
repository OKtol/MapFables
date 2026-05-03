using Vintagestory.API.Common;

namespace MapFables;

public class MapFablesModSystem : ModSystem
{
    public override void Start(ICoreAPI api)
    {
        base.Start(api);

        api.Network.RegisterChannel(Mod.Info.ModID + ".networkchannel")
            .RegisterMessageType(typeof(MapDataRequest))
            .RegisterMessageType(typeof(MapDataResponce));
    }
}