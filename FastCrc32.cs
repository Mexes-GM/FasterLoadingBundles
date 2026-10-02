using System.IO;

namespace BundleCacheBoost
{
    /// <summary>
    /// CRC-32 IEEE (reflected polynomial 0xEDB88320), same result as SPT.Custom.Utils.Crc32,
    /// but slicing-by-8 and streamed from disk instead of loading the whole file into memory.
    /// </summary>
    internal static class FastCrc32
    {
        private const int BufferSize = 1 << 20;
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var t = new uint[8 * 256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                t[i] = c;
            }

            for (var i = 0; i < 256; i++)
            {
                for (var s = 1; s < 8; s++)
                {
                    var prev = t[(s - 1) * 256 + i];
                    t[s * 256 + i] = (prev >> 8) ^ t[prev & 0xFF];
                }
            }

            return t;
        }

        public static uint HashFile(string path)
        {
            var buffer = new byte[BufferSize];
            var crc = 0xFFFFFFFFu;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
            {
                int read;
                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    crc = Update(crc, buffer, read);
                }
            }

            return ~crc;
        }

        private static uint Update(uint crc, byte[] data, int length)
        {
            var t = Table;
            var i = 0;

            for (; length - i >= 8; i += 8)
            {
                var one = (data[i] | (uint)data[i + 1] << 8 | (uint)data[i + 2] << 16 | (uint)data[i + 3] << 24) ^ crc;
                var two = data[i + 4] | (uint)data[i + 5] << 8 | (uint)data[i + 6] << 16 | (uint)data[i + 7] << 24;
                crc = t[7 * 256 + (one & 0xFF)]
                      ^ t[6 * 256 + ((one >> 8) & 0xFF)]
                      ^ t[5 * 256 + ((one >> 16) & 0xFF)]
                      ^ t[4 * 256 + (one >> 24)]
                      ^ t[3 * 256 + (two & 0xFF)]
                      ^ t[2 * 256 + ((two >> 8) & 0xFF)]
                      ^ t[1 * 256 + ((two >> 16) & 0xFF)]
                      ^ t[two >> 24];
            }

            for (; i < length; i++)
            {
                crc = t[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }
    }
}
