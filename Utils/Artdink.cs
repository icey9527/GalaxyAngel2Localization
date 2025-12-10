using System;
using System.Collections.Generic;
using System.IO;

namespace Utils
{
    internal static class Artdink
    {
        const int RingSize = 0x1000, RingMask = 0xFFF, RingInit = 0xFEE;
        const int MaxMatch = 18, MinMatch = 3;
        const byte XorKey = 0x72;

        public static bool Decompress(byte[] data, out byte[] output)
        {
            output = Array.Empty<byte>();
            if (data == null || data.Length < 8) return false;
            using var ms = new MemoryStream(data, false);
            return Decompress(ms, data.Length, out output);
        }

        public static bool Decompress(Stream input, int compressedSize, out byte[] output)
        {
            output = Array.Empty<byte>();
            if (input == null || !input.CanRead || compressedSize < 8) return false;

            var header = new byte[8];
            if (input.Read(header, 0, 8) < 8) return false;

            int mode = ParseHex(header[3]);
            if (mode < 0 || mode > 1 || !ValidMagic(header)) return false;

            uint rawSize = BitConverter.ToUInt32(header, 4);
            if (rawSize == 0 || rawSize > int.MaxValue) return false;

            int size = (int)rawSize, idx = 0, remain = compressedSize - 8;
            var buf = new byte[size];

            int Read() => remain-- > 0 ? (input.ReadByte() ^ XorKey) & 0xFF : -1;
            void Write(byte v) { if (idx < size) buf[idx++] = v; }

            if (mode == 0)
                while (remain > 0 && idx < size) { int b = Read(); if (b < 0) break; Write((byte)b); }
            else
                LzssDec(Read, Write, remain);

            output = new byte[idx];
            Buffer.BlockCopy(buf, 0, output, 0, idx);
            return true;
        }

        public static byte[] Compress(byte[] data, int mode = 1, bool arz = true)
        {
            if (data == null || data.Length == 0) return Array.Empty<byte>();

            var payload = mode == 1 ? LzssEnc(data) : data;
            if (mode == 1 && payload.Length >= data.Length) { mode = 0; payload = data; }

            var result = new byte[8 + payload.Length];
            result[0] = (byte)(arz ? 'A' : ' ');
            result[1] = (byte)(arz ? 'R' : '3');
            result[2] = (byte)(arz ? 'Z' : ';');
            result[3] = (byte)(mode < 10 ? '0' + mode : 'A' + mode - 10);
            result[4] = (byte)data.Length;
            result[5] = (byte)(data.Length >> 8);
            result[6] = (byte)(data.Length >> 16);
            result[7] = (byte)(data.Length >> 24);

            for (int i = 0; i < payload.Length; i++)
                result[8 + i] = (byte)(payload[i] ^ XorKey);

            return result;
        }

        static void LzssDec(Func<int> read, Action<byte> write, int remain)
        {
            var win = new byte[RingSize];
            int pos = RingInit, flags = 0;

            while (remain > 0)
            {
                if (((flags >>= 1) & 0x100) == 0)
                {
                    int f = read(); if (f < 0) break;
                    remain--; flags = 0xFF00 | f;
                }

                if ((flags & 1) != 0)
                {
                    int b = read(); if (b < 0) break;
                    remain--;
                    write(win[pos] = (byte)b);
                    pos = (pos + 1) & RingMask;
                }
                else
                {
                    int b1 = read(), b2 = read();
                    if (b1 < 0 || b2 < 0) break;
                    remain -= 2;
                    int off = b1 | ((b2 & 0xF0) << 4), len = (b2 & 0x0F) + 3;
                    for (int k = 0; k < len; k++, off++, pos = (pos + 1) & RingMask)
                        write(win[pos] = win[off & RingMask]);
                }
            }
        }

        static byte[] LzssEnc(byte[] src)
        {
            int n = src.Length;
            if (n == 0) return Array.Empty<byte>();

            var buf = new byte[n + (n >> 3) + 16];
            var head = new int[0x10000];
            var chain = new int[RingSize];
            Array.Fill(head, -1);

            int sp = 0, op = 0;

            while (sp < n)
            {
                int fp = op++;
                byte flags = 0;

                for (int bit = 0; bit < 8 && sp < n; bit++)
                {
                    int best = 0, off = 0;

                    if (sp + 1 < n)
                    {
                        int h = src[sp] << 8 | src[sp + 1];
                        for (int p = head[h], c = 128; p >= 0 && sp - p <= RingSize && c-- > 0; p = chain[p & RingMask])
                        {
                            int len = 0;
                            while (len < MaxMatch && sp + len < n && src[p + len] == src[sp + len]) len++;
                            if (len > best) { best = len; off = (RingInit + p) & RingMask; if (len == MaxMatch) break; }
                        }
                    }

                    if (best >= MinMatch)
                    {
                        if (op + 2 > buf.Length) Array.Resize(ref buf, buf.Length * 2);
                        buf[op++] = (byte)off;
                        buf[op++] = (byte)((off >> 4 & 0xF0) | (best - 3));
                        for (int i = 0; i < best && sp + i + 1 < n; i++)
                        {
                            int hh = src[sp + i] << 8 | src[sp + i + 1];
                            chain[(sp + i) & RingMask] = head[hh];
                            head[hh] = sp + i;
                        }
                        sp += best;
                    }
                    else
                    {
                        if (op + 1 > buf.Length) Array.Resize(ref buf, buf.Length * 2);
                        flags |= (byte)(1 << bit);
                        if (sp + 1 < n)
                        {
                            int h = src[sp] << 8 | src[sp + 1];
                            chain[sp & RingMask] = head[h];
                            head[h] = sp;
                        }
                        buf[op++] = src[sp++];
                    }
                }
                buf[fp] = flags;
            }

            Array.Resize(ref buf, op);
            return buf;
        }

        static bool ValidMagic(byte[] h) =>
            (h[0] == 'A' && h[1] == 'R' && h[2] == 'Z') ||
            (h[0] == ' ' && h[1] == '3' && h[2] == ';');

        static int ParseHex(byte v) =>
            v >= '0' && v <= '9' ? v - '0' :
            v >= 'A' && v <= 'F' ? v - 'A' + 10 :
            v >= 'a' && v <= 'f' ? v - 'a' + 10 : -1;
    }
}