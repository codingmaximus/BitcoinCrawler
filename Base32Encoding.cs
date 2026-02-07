namespace BitcoinCrawlerStats
{
    // RFC 4648 Base32 encoding (lowercase, no padding) – same as before
    internal static class Base32Encoding
    {
        private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

        public static string ToString(byte[] input)
        {
            if (input == null || input.Length == 0)
                return string.Empty;

            int bitCount = input.Length * 8;
            int outputLength = (bitCount + 4) / 5;

            char[] output = new char[outputLength];
            int outputPos = 0;
            int buffer = 0;
            int bufferBits = 0;

            foreach (byte b in input)
            {
                buffer = (buffer << 8) | b;
                bufferBits += 8;

                while (bufferBits >= 5)
                {
                    bufferBits -= 5;
                    int index = (buffer >> bufferBits) & 0x1F;
                    output[outputPos++] = Alphabet[index];
                }
            }

            if (bufferBits > 0)
            {
                int index = (buffer << (5 - bufferBits)) & 0x1F;
                output[outputPos++] = Alphabet[index];
            }

            return new string(output, 0, outputPos);
        }
    }
}
