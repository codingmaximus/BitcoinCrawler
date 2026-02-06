using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitcoinCrawlerStats
{
    public static class BinaryReaderExtensions
    {
        public static ulong ReadVarint(this BinaryReader reader)
        {
            var b = reader.ReadByte();
            if (b < 0xFD) return b;
            if (b == 0xFD) return reader.ReadUInt16();
            if (b == 0xFE) return reader.ReadUInt32();
            return reader.ReadUInt64();
        }

        public static string ReadVarString(this BinaryReader reader)
        {
            var len = reader.ReadVarint();
            var bytes = reader.ReadBytes((int)len);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
