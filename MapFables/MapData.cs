using ProtoBuf;
using System.Collections.Generic;

namespace MapFables
{
    [ProtoContract]
    public class MapDataPacket
    {
        [ProtoMember(1)]
        public string FromPlayer { get; set; } = "";

        [ProtoMember(2)]
        public List<ChunkImageData> Chunks { get; set; } = [];
    }

    [ProtoContract]
    public class ChunkImageData
    {
        [ProtoMember(1)]
        public int X { get; set; }

        [ProtoMember(2)]
        public int Z { get; set; }

        /// <summary>Пиксели чанка (32×32) в формате int[] (игровой формат).</summary>
        [ProtoMember(3)]
        public int[] Pixels { get; set; } = [];
    }
}