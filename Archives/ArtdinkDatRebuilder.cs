using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics; // 新增
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
        static readonly Encoding ShiftJis = Encoding.GetEncoding(932);
        static readonly byte[] PidxMagic = Encoding.ASCII.GetBytes("PIDX");
        static readonly byte[] FstsMagic = Encoding.ASCII.GetBytes("FSTS");

        public sealed class RebuildSummary
        {
            public int DatCount { get; internal set; }
            public int TotalPaths { get; internal set; }
            public int ModifiedCount { get; internal set; }
            public int OriginalCount { get; internal set; }
            public string PackedRoot { get; internal set; } = string.Empty;
            public string Log { get; internal set; } = string.Empty;

            // 新增：总耗时
            public TimeSpan Elapsed { get; internal set; }
        }

        // 原来的 async 版本：现在转调到带 logCallback 的重载
        public static Task<RebuildSummary> RebuildAllDatsAsync(
            string workspaceRoot,
            CancellationToken cancellationToken = default) =>
            RebuildAllDatsAsync(workspaceRoot, null, cancellationToken);

        // 新增：带实时日志回调的 async 版本
        public static Task<RebuildSummary> RebuildAllDatsAsync(
            string workspaceRoot,
            Action<string>? logCallback,
            CancellationToken cancellationToken = default) =>
            Task.Run(() => RebuildAllDats(workspaceRoot, logCallback), cancellationToken);

        // 原来的同步版本：保持签名不变，对外行为不变
        public static RebuildSummary RebuildAllDats(string workspaceRoot) =>
            RebuildAllDats(workspaceRoot, null);

        // 新增：带实时日志回调的同步版本，核心逻辑在这里
        public static RebuildSummary RebuildAllDats(
            string workspaceRoot,
            Action<string>? logCallback)
        {
            var swTotal = Stopwatch.StartNew(); // 新增：总耗时计时

            var listPath = Path.Combine(workspaceRoot, "list.json");
            if (!File.Exists(listPath))
                throw new FileNotFoundException("找不到 list.json", listPath);

            var originalRoot = Path.Combine(workspaceRoot, "original");
            if (!Directory.Exists(originalRoot))
                throw new DirectoryNotFoundException("找不到 original 目录: " + originalRoot);

            var modifiedRoot = Path.Combine(workspaceRoot, "modified");

            var packedRoot = Path.Combine(workspaceRoot, "packed");
            Directory.CreateDirectory(packedRoot);

            var json = File.ReadAllText(listPath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var indexDict = JsonSerializer.Deserialize<Dictionary<string, DatIndex>>(json, options)
                           ?? new Dictionary<string, DatIndex>();

            var allPaths = CollectAllPaths(indexDict);
            var globalSources = BuildGlobalPathSources(allPaths, originalRoot, modifiedRoot, logCallback);

            var logLines = new ConcurrentQueue<string>();

            // 新增：统一日志函数，既入队又调用回调
            void Log(string msg)
            {
                logLines.Enqueue(msg);
                logCallback?.Invoke(msg);
            }

            int datCount = 0;
            int totalPaths = 0;
            int modCount = 0;
            int origCount = 0;

            var po = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount
            };

            Parallel.ForEach(indexDict, po, kvp =>
            {
                string datName = kvp.Key;
                var datIndex = kvp.Value;

                try
                {
                    string outPath = Path.Combine(packedRoot, datName.ToLowerInvariant());
                    Log($"[DAT] 开始重建: {datName}");
                    var res = RebuildSingleDat(datName, datIndex, outPath, globalSources);
                    Log($"[DAT] 完成: {datName}  总条目 {res.TotalPaths}, modified {res.ModifiedCount}, original {res.OriginalCount}");

                    Interlocked.Increment(ref datCount);
                    Interlocked.Add(ref totalPaths, res.TotalPaths);
                    Interlocked.Add(ref modCount, res.ModifiedCount);
                    Interlocked.Add(ref origCount, res.OriginalCount);
                }
                catch (Exception ex)
                {
                    Log($"[DAT] 失败: {datName} : {ex.Message}");
                }
            });

            swTotal.Stop(); // 新增：停止计时

            return new RebuildSummary
            {
                DatCount = datCount,
                TotalPaths = totalPaths,
                ModifiedCount = modCount,
                OriginalCount = origCount,
                PackedRoot = packedRoot,
                Log = string.Join(Environment.NewLine, logLines),
                Elapsed = swTotal.Elapsed // 新增：回填耗时
            };
        }

        sealed class DatRebuildResult
        {
            public int TotalPaths { get; init; }
            public int ModifiedCount { get; init; }
            public int OriginalCount { get; init; }
        }
    }
}