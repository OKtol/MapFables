using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace MapFables;

internal class MapFablesServerSystem : ModSystem
{
    IServerNetworkChannel serverChannel = null!;
    ICoreServerAPI sapi = null!;

    /// <summary>
    /// This is a server-side system, so we do not want it to load on the client.
    /// </summary>
    public override bool ShouldLoad(EnumAppSide forSide)
    {
        return forSide != EnumAppSide.Client;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);

        serverChannel = api.Network.GetChannel(Mod.Info.ModID + ".networkchannel")
            .SetMessageHandler<MapDataResponce>(OnMapDataRequestReceived);
        sapi = api;

        api.ChatCommands.Create("sharemap")
            .WithDescription("Поделиться своей исследованной картой с другими игроками")
            .RequiresPlayer()
            .RequiresPrivilege(Privilege.chat)
            .HandleWith(OnShareMapCmd);

        api.Logger.Notification("[MapFables] Серверная часть загружена. Используйте /sharemap");
    }

    private void OnMapDataRequestReceived(IServerPlayer fromPlayer, MapDataResponce packet)
    {
        if (packet?.ExploredChunks == null || packet.ExploredChunks.Count == 0) return;
        var responce = new MapDataRequest
        {
            PlayerName = packet.PlayerName,
            ExploredChunks = packet.ExploredChunks
        };
        foreach (var target in sapi.World.AllOnlinePlayers)
        {
            if (target == fromPlayer) continue;
            serverChannel.SendPacket(responce, (IServerPlayer)target);
        }
    }

    private TextCommandResult OnShareMapCmd(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;

        var worldManager = sapi.WorldManager;
        if (worldManager == null)
        {
            player!.SendMessage(GlobalConstants.GeneralChatGroup, "[MapFables] WorldManager недоступен", EnumChatType.Notification);
            return TextCommandResult.Error("Ошибка");
        }

        // Все загруженные map-чанки
        var allChunks = worldManager.AllLoadedMapchunks;
        if (allChunks == null || allChunks.Count == 0)
        {
            player!.SendMessage(GlobalConstants.GeneralChatGroup, "[MapFables] Нет загруженных чанков. Исследуйте мир.", EnumChatType.Notification);
            return TextCommandResult.Success();
        }

        // Собираем координаты чанков, исследованных игроком
        var coords = new List<ChunkCoord>();
        foreach (var kvp in allChunks)
        {
            long idx = kvp.Key;
            Vec2i pos = worldManager.MapChunkPosFromChunkIndex2D(idx);
            if (pos != null && worldManager.HasChunk(pos.X, 0, pos.Y, player))
            {
                coords.Add(new ChunkCoord(pos.X, pos.Y));
            }
        }

        if (coords.Count == 0)
        {
            player!.SendMessage(GlobalConstants.GeneralChatGroup, "[MapFables] У вас нет исследованных областей для передачи.", EnumChatType.Notification);
            return TextCommandResult.Success();
        }

        var packet = new MapDataRequest
        {
            PlayerName = player!.PlayerName,
            ExploredChunks = coords
        };

        int sent = 0;
        foreach (var target in sapi.World.AllOnlinePlayers)
        {
            if (target == player) continue;
            serverChannel.SendPacket(packet, (IServerPlayer)target);
            sent++;
        }

        player.SendMessage(GlobalConstants.GeneralChatGroup, $"[MapFables] Ваша карта ({coords.Count} областей) отправлена {sent} игрокам.", EnumChatType.Notification);
        sapi.Logger.Notification($"[MapFables] {player.PlayerName} поделился {coords.Count} чанками.");
        return TextCommandResult.Success();
    }
}
