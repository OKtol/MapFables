using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace MapFables;

/// <summary>
/// Клиентский слой карты, который принимает пиксели чанков от других игроков
/// и записывает их в локальную mapdb. Движок сам отрисует их через
/// стандартный путь: mapdb.GetMapPiece -> loadFromChunkPixels -> GPU.
///
/// Наследуется от ChunkMapLayer чтобы иметь прямой доступ к mapdb и chunksToGen.
/// Транспорт — встроенный механизм движка: SendMapDataToClient / OnDataFromServer.
/// </summary>
public class MapFablesChunkLayer : ChunkMapLayer
{
    // Кэшируем поля через рефлексию один раз при первом вызове OnDataFromServer.
    // Нужно потому что mapdb, chunksToGen и chunksToGenLock объявлены
    // как private в ChunkMapLayer — напрямую не доступны из наследника.
    private static FieldInfo? s_mapdbField;
    private static FieldInfo? s_chunksToGenField;
    private static FieldInfo? s_chunksToGenLockField;
    private static MethodInfo? s_enqueueMethod;
    private static bool s_fieldsResolved;

    public MapFablesChunkLayer(ICoreAPI api, IWorldMapManager mapSink)
        : base(api, mapSink)
    {
    }

    /// <summary>
    /// Вызывается движком когда сервер отправил данные через SendMapDataToClient.
    /// Десериализуем пакет, пишем пиксели в mapdb и добавляем чанки в очередь.
    /// </summary>
    public override void OnDataFromServer(byte[] data)
    {
        if (data == null || data.Length == 0) return;

        MapDataPacket packet;
        try
        {
            packet = SerializerUtil.Deserialize<MapDataPacket>(data);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[MapFables] Failed to deserialize map packet: {0}", ex.Message);
            return;
        }

        if (packet?.Chunks == null || packet.Chunks.Count == 0) return;

        if (!ResolveFields()) return;

        var mapdb = s_mapdbField!.GetValue(this) as MapDB;
        var chunksToGen = s_chunksToGenField!.GetValue(this);
        var chunksToGenLock = s_chunksToGenLockField!.GetValue(this);

        if (mapdb == null)
        {
            api.Logger.Warning("[MapFables] mapdb is null, cannot inject chunks.");
            return;
        }

        // Формируем словарь для пакетной записи в SQLite
        var piecesToSave = new Dictionary<FastVec2i, MapPieceDB>();
        foreach (var chunk in packet.Chunks)
        {
            if (chunk.Pixels == null || chunk.Pixels.Length == 0) continue;
            piecesToSave[new FastVec2i(chunk.X, chunk.Z)] = new MapPieceDB { Pixels = chunk.Pixels };
        }

        if (piecesToSave.Count == 0) return;

        // Записываем в локальную SQLite БД клиента одной транзакцией
        try
        {
            mapdb.SetMapPieces(piecesToSave);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[MapFables] Failed to write chunks to mapdb: {0}", ex.Message);
            return;
        }

        // Добавляем чанки в очередь на генерацию —
        // OnOffThreadTick подхватит их и вызовет loadFromChunkPixels
        lock (chunksToGenLock!)
        {
            foreach (var cord in piecesToSave.Keys)
                s_enqueueMethod!.Invoke(chunksToGen, new object[] { cord });
        }

        api.Logger.Notification(
            "[MapFables] Injected {0} chunks from {1} into mapdb.",
            piecesToSave.Count, packet.SenderName);

        (api as Vintagestory.API.Client.ICoreClientAPI)?
            .ShowChatMessage($"[MapFables] Получено {piecesToSave.Count} чанков карты от {packet.SenderName}. Открой карту чтобы увидеть их.");
    }

    private bool ResolveFields()
    {
        if (s_fieldsResolved) return s_mapdbField != null;
        s_fieldsResolved = true;

        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var t = typeof(ChunkMapLayer);

        s_mapdbField         = t.GetField("mapdb",            flags);
        s_chunksToGenField   = t.GetField("chunksToGen",      flags);
        s_chunksToGenLockField = t.GetField("chunksToGenLock", flags);

        if (s_chunksToGenField != null)
            s_enqueueMethod = s_chunksToGenField
                .FieldType
                .GetMethod("Enqueue");

        if (s_mapdbField == null || s_chunksToGenField == null ||
            s_chunksToGenLockField == null || s_enqueueMethod == null)
        {
            api.Logger.Warning(
                "[MapFables] Could not resolve ChunkMapLayer private fields. " +
                "mapdb={0} chunksToGen={1} lock={2} enqueue={3}",
                s_mapdbField != null, s_chunksToGenField != null,
                s_chunksToGenLockField != null, s_enqueueMethod != null);
            return false;
        }

        return true;
    }
}
