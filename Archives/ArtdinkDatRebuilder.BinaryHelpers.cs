using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyAngel2Localization.Utils;
using GalaxyAngel2Localization.Workspace;
using Utils;
using ArtdinkCodec = Utils.Artdink;

namespace GalaxyAngel2Localization.Archives.Artdink
{
    internal static partial class ArtdinkDatRebuilder
    {
        static byte[] BuildStringPool(IEnumerable<string> strings, Dictionary<string, int> offsets)
        {
            var list = new List<byte>();
            foreach (var s in strings)
            {
                if (offsets.ContainsKey(s))
                    continue;

                int off = list.Count;
                offsets[s] = off;
                var bytes = ShiftJis.GetBytes(s);
                list.AddRange(bytes);
                list.Add(0);
            }
            return list.ToArray();
        }

        static long Align16(long value) => (value + 0xF) & ~0xF;

        static long AlignData2048(long value) => (value + 0x7FF) & ~0x7FF;

        static void WriteUInt32(Stream s, uint v)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, v);
            s.Write(buf);
        }

        static void WriteBytes(Stream s, byte[] data) => s.Write(data, 0, data.Length);
    }
}