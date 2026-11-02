using System;
using System.IO;

namespace VoiceSubEditor
{
    /// <summary>MSH 中的一条语音(16字节条目)</summary>
    public class VoiceEntry
    {
        public uint Size;        // MSB 内 ADPCM 字节数
        public uint Config;      // bit0=循环
        public uint Start;       // MSB 内偏移
        public uint SampleRate;
        public bool Loop => (Config & 1) != 0;
    }

    /// <summary>
    /// slgvoice.msh/.msb 语音库: 解析 MSH 头, 按语音ID(=MSH条目索引, tbl 的ボイスID)从 MSB
    /// 直接读取 PSX-ADPCM 并在内存解码为 PCM, 全程无临时文件。
    /// 解码逻辑对照 vgmstream psx_decoder.c (decode_psx)。
    /// </summary>
    public class VoiceBank
    {
        public string MshPath, MsbPath;
        VoiceEntry[] entries;

        public int Count { get { return entries != null ? entries.Length : 0; } }

        public static VoiceBank Load(string mshPath)
        {
            if (!File.Exists(mshPath)) return null;
            byte[] h = File.ReadAllBytes(mshPath);   // MSH 很小, 直接读
            if (h.Length < 12) return null;
            uint fileSize = ReadU32(h, 0);
            if (fileSize != (uint)h.Length) return null;
            int n = (int)ReadU32(h, 8);
            if (n < 0 || 0x0c + 0x10 * (long)n > h.Length) return null;

            var bank = new VoiceBank();
            bank.MshPath = mshPath;
            bank.MsbPath = Path.ChangeExtension(mshPath, ".msb");
            if (!File.Exists(bank.MsbPath)) return null;
            bank.entries = new VoiceEntry[n];
            for (int i = 0; i < n; i++)
            {
                int off = 0x0c + 0x10 * i;
                bank.entries[i] = new VoiceEntry
                {
                    Size = ReadU32(h, off),
                    Config = ReadU32(h, off + 4),
                    Start = ReadU32(h, off + 8),
                    SampleRate = ReadU32(h, off + 12),
                };
            }
            return bank;
        }

        static uint ReadU32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }

        public VoiceEntry GetEntry(int voiceId)
        {
            if (entries == null || voiceId < 0 || voiceId >= entries.Length) return null;
            return entries[voiceId];
        }

        /// <summary>解码为 16bit PCM(单声道)。返回 null 表示语音不存在。</summary>
        public bool TryDecode(int voiceId, out short[] pcm, out int sampleRate, out int loopStart, out int loopEnd)
        {
            pcm = null; sampleRate = 0; loopStart = -1; loopEnd = -1;
            VoiceEntry e = GetEntry(voiceId);
            if (e == null || e.Size == 0 || e.SampleRate == 0) return false;

            try
            {
                long msbLength = new FileInfo(MsbPath).Length;
                if (e.Start > msbLength || e.Size > msbLength - e.Start) return false;
                if (e.Size > int.MaxValue) return false;
            }
            catch (IOException) { return false; }

            byte[] adpcm = new byte[e.Size];
            using (var fs = new FileStream(MsbPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(e.Start, SeekOrigin.Begin);
                int read = 0;
                while (read < (int)e.Size)
                {
                    int k = fs.Read(adpcm, read, (int)e.Size - read);
                    if (k <= 0) break;
                    read += k;
                }
                if (read < (int)e.Size) Array.Resize(ref adpcm, read);
            }
            sampleRate = (int)e.SampleRate;
            DecodePsxAdpcm(adpcm, out pcm, out loopStart, out loopEnd);
            return true;
        }

        // PS-ADPCM 系数表 (vgmstream ps_adpcm_coefs_i)
        static readonly int[,] Coefs = {
            {   0,   0 }, {  60,   0 }, { 115, -52 }, {  98, -55 }, { 122, -60 },
        };

        /// <summary>
        /// PSX-ADPCM → PCM16。帧=16字节(1字节滤波/移位 + 1字节标志 + 14字节28个nibble)。
        /// nibble 顺序: 偶数采样取低4位(先低后高) —— 与 vgmstream decode_psx 一致。
        /// </summary>
        public static void DecodePsxAdpcm(byte[] data, out short[] pcm, out int loopStartSample, out int loopEndSample)
        {
            int frames = data.Length / 16;
            pcm = new short[frames * 28];
            int h1 = 0, h2 = 0, pos = 0;
            int loopStartFrame = -1, loopEndFrame = -1;

            for (int f = 0; f < frames; f++)
            {
                int off = f * 16;
                int filter = (data[off] >> 4) & 0xF;
                int shift = data[off] & 0xF;
                if (filter > 4) filter = 0;      // 5~7 未定义
                if (shift > 12) shift = 9;       // nocash PSX 文档
                int c1 = Coefs[filter, 0], c2 = Coefs[filter, 1];
                int flag = data[off + 1];
                if (loopStartFrame < 0 && (flag & 0x02) != 0) loopStartFrame = f;
                if (loopEndFrame < 0 && (flag & 0x01) != 0) loopEndFrame = f;

                for (int i = 0; i < 28; i++)
                {
                    int b = data[off + 2 + (i >> 1)];
                    int nib = (i % 2 == 0) ? (b & 0xF) : ((b >> 4) & 0xF);
                    if (nib >= 8) nib -= 16;
                    int s;
                    if (flag >= 7)
                    {
                        s = 0;   // flag 0x07: 静音帧
                    }
                    else
                    {
                        // sample = nib<<(20-shift) + pred*256; >> 8
                        s = (nib << (20 - shift)) + ((((c1 * h1 + c2 * h2 + 32) >> 6)) << 8);
                        s >>= 8;
                    }
                    int cl = s < -32768 ? -32768 : (s > 32767 ? 32767 : s);
                    pcm[pos++] = (short)cl;
                    h2 = h1; h1 = s;   // 历史保存未截幅值(与 vgmstream 一致)
                }
            }
            loopStartSample = loopStartFrame >= 0 ? loopStartFrame * 28 : -1;
            loopEndSample = loopEndFrame >= 0 ? (loopEndFrame + 1) * 28 : -1;
        }
    }

    /// <summary>PCM → WAV 字节流(System.Media.SoundPlayer 播放, 无临时文件)</summary>
    public static class Wav
    {
        public static byte[] Build(short[] pcm, int sampleRate)
        {
            int dataLen = pcm.Length * 2;
            var ms = new MemoryStream(44 + dataLen);
            var w = new BinaryWriter(ms);
            w.Write("RIFF"u8);
            w.Write(36 + dataLen);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1);          // PCM
            w.Write((short)1);          // 单声道
            w.Write(sampleRate);
            w.Write(sampleRate * 2);    // byte rate
            w.Write((short)2);          // block align
            w.Write((short)16);         // bits
            w.Write("data"u8);
            w.Write(dataLen);
            byte[] buf = new byte[dataLen];
            Buffer.BlockCopy(pcm, 0, buf, 0, dataLen);
            w.Write(buf);
            w.Flush();
            return ms.ToArray();
        }

        /// <summary>带循环标记的预览: 线性 + 循环区再放一遍</summary>
        public static byte[] BuildLoopPreview(short[] pcm, int sampleRate, int loopStart, int loopEnd)
        {
            if (loopStart < 0 || loopEnd <= loopStart || loopEnd > pcm.Length)
                return Build(pcm, sampleRate);
            int intro = loopEnd;
            int loopLen = loopEnd - loopStart;
            var all = new short[intro + loopLen];
            Array.Copy(pcm, 0, all, 0, intro);
            Array.Copy(pcm, loopStart, all, intro, loopLen);
            return Build(all, sampleRate);
        }
    }

    /// <summary>SoundPlayer 封装: 内存流播放/停止</summary>
    public class VoicePlayer : IDisposable
    {
        System.Media.SoundPlayer player;
        MemoryStream stream;
        string tempPath;

        public void Play(byte[] wav)
        {
            Stop();
            tempPath = Path.Combine(Path.GetTempPath(), "ga2_voice_" + Guid.NewGuid().ToString("N") + ".wav");
            File.WriteAllBytes(tempPath, wav);
            player = new System.Media.SoundPlayer(tempPath);
            player.Play();   // 异步
        }

        public void Stop()
        {
            if (player != null)
            {
                try { player.Stop(); } catch { }
                player.Dispose();
                player = null;
            }
            if (stream != null) { stream.Dispose(); stream = null; }
            if (tempPath != null)
            {
                try { File.Delete(tempPath); } catch { }
                tempPath = null;
            }
        }

        public void Dispose() { Stop(); }
    }
}
