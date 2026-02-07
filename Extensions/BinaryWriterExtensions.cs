using System.Text;

namespace BitcoinCrawlerStats
{
    // Helper extensions
    public static class BinaryWriterExtensions
    {
        public static void WriteVarInt(this BinaryWriter writer, ulong value)
        {
            if (value < 0xFD)
            {
                writer.Write((byte)value);
            }
            else if (value <= 0xFFFF)
            {
                writer.Write((byte)0xFD);
                writer.Write((ushort)value);
            }
            else if (value <= 0xFFFFFFFF)
            {
                writer.Write((byte)0xFE);
                writer.Write((uint)value);
            }
            else
            {
                writer.Write((byte)0xFF);
                writer.Write(value);
            }
        }

        public static void WriteVarString(this BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.WriteVarInt((ulong)bytes.Length);
            writer.Write(bytes);
        }
    }
}
