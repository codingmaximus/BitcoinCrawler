using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitcoinCrawlerStats
{
    public static class StringUtils
    {
        public static String SafeSubstring(String text, int start)
        {
            return SafeSubstring(text, start, text.Length);
        }

        public static String SafeSubstring(String text, int start, int length)
        {
            if (String.IsNullOrEmpty(text))
                return String.Empty;

            if (start < 0)
                start = 0;
            if (start > text.Length)
                start = text.Length - 1;
            if (length < 0)
                length = 0;
            if (length > text.Length)
                length = text.Length;

            return text.Substring(start, Math.Min(length, text.Length - start));
        }
    }
}
