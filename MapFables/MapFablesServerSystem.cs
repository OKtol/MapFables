using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace MapFables;

internal class MapFablesServerSystem : ModSystem
{
    private IServerNetworkChannel serverChannel = null!;
    private ICoreServerAPI sapi = null!;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);
        sapi = api;

        serverChannel = api.Network.GetChannel(MapFablesModSystem.NetworkChannelName);
        serverChannel.SetMessageHandler<MapDataPacket>(OnMapDataReceived);

        // Регистрируем серверную команду
        api.ChatCommands.Create("sharemap")
            .WithDescription("Share your discovered chunks with all online players")
            .RequiresPlayer()
            .RequiresPrivilege(Privilege.chat)
            .HandleWith(OnShareMapCommand);

        api.Logger.Notification("[MapFables] Server-side loaded with /sharemap command.");
    }

    // Когда игрок вводит /sharemap, просим у него данные
    private TextCommandResult OnShareMapCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("Player not found");

        // Отправляем клиенту запрос на предоставление его чанков
        serverChannel.SendPacket(new ShareMapRequest(), player);
        player.SendMessage(GlobalConstants.GeneralChatGroup, "[MapFables] Gathering your discovered chunks...", EnumChatType.Notification);
        return TextCommandResult.Success();
    }

    // Получили пакет с чанками от игрока – рассылаем всем остальным
    private void OnMapDataReceived(IServerPlayer fromPlayer, MapDataPacket packet)
    {
        if (packet?.ExploredChunks == null || packet.ExploredChunks.Count == 0)
            return;

        // Отправляем пакет всем онлайн-игрокам, кроме отправителя
        foreach (IServerPlayer target in sapi.World.AllOnlinePlayers)
        {
            if (target == fromPlayer) continue;
            serverChannel.SendPacket(packet, target);
        }

        // Лог на сервере
        sapi.Logger.Notification($"[MapFables] {fromPlayer.PlayerName} shared {packet.ExploredChunks.Count} chunks.");
    }
}