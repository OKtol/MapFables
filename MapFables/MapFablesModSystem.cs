using HarmonyLib;
using Vintagestory.API.Common;

namespace MapFables;

public class MapFablesModSystem : ModSystem
{
    public const string NetworkChannelName = "mapfables.networkchannel";

    private Harmony? harmony;

    public override void Start(ICoreAPI api)
    {
        base.Start(api);

        api.Network
            .RegisterChannel(NetworkChannelName)
            .RegisterMessageType<MapDataPacket>();

        // Патчим только на клиенте — патч работает с клиентским рендером карты
        if (api.Side == EnumAppSide.Client)
        {
            harmony = new Harmony(Mod.Info.ModID);
            harmony.PatchAll();
            api.Logger.Notification("[MapFables] Harmony patches applied.");
        }
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll(Mod.Info.ModID);
        base.Dispose();
    }
}
