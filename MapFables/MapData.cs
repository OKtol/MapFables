using ProtoBuf;
using System.Collections.Generic;

namespace MapFables;

/// <summary>
/// Пакет с данными карты. Сериализуется через Protobuf и передаётся
/// через механизм движка SendMapDataToClient -> OnDataFromServer.
/// </summary>
[ProtoContract]
public class MapDataPacket
{
    [ProtoMember(1)]
    public string SenderName { get; set; } = "";

    [ProtoMember(2)]
    public List<ChunkImageData> Chunks { get; set; } = new();
}

[ProtoContract]
public class ChunkImageData
{
    [ProtoMember(1)]
    public int X { get; set; }

    [ProtoMember(2)]
    public int Z { get; set; }

    [ProtoMember(3)]
    public int[] Pixels { get; set; } = new int[0];
}
