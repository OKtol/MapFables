using HarmonyLib;
using Vintagestory.API.Common;

namespace MapFables;

public class MapFablesModSystem : ModSystem
{
    public const string NetworkChannelName = "mapfables.networkchannel";

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        api.Network.RegisterChannel(NetworkChannelName)
            .RegisterMessageType<MapDataPacket>();

        var harmony = new Harmony(Mod.Info.ModID);
        harmony.PatchAll();
    }
}