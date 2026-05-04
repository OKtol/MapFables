using ProtoBuf;
using System.Collections.Generic;

namespace MapFables;

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class MapDataPacket
{
    public string SenderName { get; set; } = "";
    public List<ChunkImageData> Chunks { get; set; } = new();
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class ChunkImageData
{
    public int X { get; set; }
    public int Z { get; set; }
    public int[] Pixels { get; set; } = new int[0];
}