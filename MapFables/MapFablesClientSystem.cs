using System.Collections.Concurrent;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace MapFables;

internal class MapFablesClientSystem : ModSystem
{
    private ICoreClientAPI capi = null!;
    private IClientNetworkChannel clientChannel = null!;

    /// <summary>
    /// Кэш пикселей чанков, полученных по сети.
    /// Ключ — (chunkX, chunkZ) в чанковых координатах.
    /// Используется патчем ChunkMapLayerPatch.
    /// ConcurrentDictionary — потокобезопасен без явного lock,
    /// так как патч читает из офф-тред тика карты.
    /// </summary>
    public static ConcurrentDictionary<(int x, int z), int[]> SharedChunkImages { get; }
        = new ConcurrentDictionary<(int x, int z), int[]>();

    /// <summary>
    /// Публичный доступ к API для использования в патче (без рефлексии).
    /// </summary>
    public static ICoreClientAPI? Api { get; private set; }

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    // ExecuteOrder 0.11 — загружаемся чуть позже стандартных систем,
    // чтобы канал уже был зарегистрирован в MapFablesModSystem.Start()
    public override double ExecuteOrder() => 0.11;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        Api = api;

        clientChannel = api.Network.GetChannel(MapFablesModSystem.NetworkChannelName);
        clientChannel.SetMessageHandler<MapDataPacket>(OnReceivedMapData);

        capi.Logger.Notification("[MapFables] Client system started.");
    }

    private void OnReceivedMapData(MapDataPacket packet)
    {
        if (packet?.Chunks == null || packet.Chunks.Count == 0)
        {
            capi.Logger.Warning("[MapFables] Received empty or null packet.");
            return;
        }

        int added = 0;
        foreach (var chunk in packet.Chunks)
        {
            if (chunk.Pixels == null || chunk.Pixels.Length == 0)
                continue;

            var key = (chunk.X, chunk.Z);
            // AddOrUpdate: не теряем данные при повторной отправке
            SharedChunkImages[key] = chunk.Pixels;
            added++;
        }

        capi.Logger.Notification(
            $"[MapFables] Received {added} chunks from {packet.SenderName}. " +
            $"Total cached: {SharedChunkImages.Count}");

        capi.ShowChatMessage(
            $"[MapFables] Получено {added} чанков карты от {packet.SenderName}.");
    }

    public override void Dispose()
    {
        Api = null;
        SharedChunkImages.Clear();
        base.Dispose();
    }
}
