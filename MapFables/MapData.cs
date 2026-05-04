using ProtoBuf;
using System.Collections.Generic;

namespace MapFables;

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class MapDataPacket
{
    public string PlayerName { get; set; } = "";
    public List<ChunkCoord> ExploredChunks { get; set; } = new();
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class ShareMapRequest
{
    // Пустой пакет-запрос
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class ChunkCoord
{
    public int X { get; set; }
    public int Z { get; set; }
    public ChunkCoord() { }
    public ChunkCoord(int x, int z) { X = x; Z = z; }
}