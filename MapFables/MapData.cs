using ProtoBuf;
using System.Collections.Generic;

namespace MapFables
{
    [ProtoContract]
    public class MapDataPacket
    {
        [ProtoMember(1)]
        public string FromPlayer = "";

        [ProtoMember(2)]
        public List<ChunkImageData> Chunks = new List<ChunkImageData>();
    }

    [ProtoContract]
    public class ChunkImageData
    {
        [ProtoMember(1)]
        public int X;

        [ProtoMember(2)]
        public int Z;

        // Пиксели чанка (32×32 = 1024 int) в формате RGBA
        [ProtoMember(3)]
        public int[] Pixels = System.Array.Empty<int>();
    }
}
