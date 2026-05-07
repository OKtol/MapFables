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

        /// <summary>Пиксели чанка (32×32) в формате int[] (игровой формат).</summary>
        [ProtoMember(3)]
        public int[] Pixels = System.Array.Empty<int>();
    }
}