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

        /// <summary>
        /// RGBA-пиксели чанка в байтах.
        /// Длина всегда равна ChunkSize * ChunkSize * 4 (4 байта на пиксель).
        /// Это byte[]-представление int[] из MapPieceDB.Pixels игры.
        /// </summary>
        [ProtoMember(3)]
        public byte[] Pixels = System.Array.Empty<byte>();

        [ProtoMember(4)]
        public int Width;

        [ProtoMember(5)]
        public int Height;
    }
}