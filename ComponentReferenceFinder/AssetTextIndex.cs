#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace RefFinder
{
    internal static class AssetTextIndex
    {
        private const int CHUNK = 1 << 16;

        internal static readonly string[] ScannableExtensions = { ".prefab", ".unity", ".asset" };

        private static string[] _fileCache;
        private static readonly Dictionary<string, List<string>> _guidHitCache = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        public static bool IsTextSerialized
        {
            get { return EditorSettings.serializationMode == SerializationMode.ForceText; }
        }

        public static void Invalidate()
        {
            _fileCache = null;
            _guidHitCache.Clear();
        }

        public static int CachedFileCount
        {
            get { return _fileCache != null ? _fileCache.Length : 0; }
        }

        public static string[] GetProjectFiles()
        {
            if (_fileCache != null) return _fileCache;

            var list = new List<string>(8192);
            string root = Path.GetFullPath("Assets");

            try
            {
                foreach (string f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(f);
                    if (string.IsNullOrEmpty(ext)) continue;

                    for (int i = 0; i < ScannableExtensions.Length; i++)
                    {
                        if (ext.Equals(ScannableExtensions[i], StringComparison.OrdinalIgnoreCase))
                        {
                            list.Add(f);
                            break;
                        }
                    }
                }
            }
            catch
            {
            }

            _fileCache = list.ToArray();
            return _fileCache;
        }

        public static string ToAssetPath(string absolutePath)
        {
            string full = absolutePath.Replace('\\', '/');
            int idx = full.LastIndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return full.Substring(idx + 1);
            return full.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        private static string CacheKey(IList<string> guids)
        {
            if (guids.Count == 1) return guids[0];

            var sb = new StringBuilder();
            for (int i = 0; i < guids.Count; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(guids[i]);
            }

            return sb.ToString();
        }

        public static bool TryGetCached(IList<string> guids, out List<string> hits)
        {
            return _guidHitCache.TryGetValue(CacheKey(guids), out hits);
        }

        public static void StoreCached(IList<string> guids, List<string> hits)
        {
            _guidHitCache[CacheKey(guids)] = hits;
        }

        public static Task<List<string>> FindFilesContainingAsync(string[] files, IList<string> guids, CancellationToken ct)
        {
            var needles = new byte[guids.Count][];
            for (int i = 0; i < guids.Count; i++)
                needles[i] = Encoding.ASCII.GetBytes("guid: " + guids[i]);

            return Task.Run(() =>
            {
                var bag = new ConcurrentBag<string>();

                var options = new ParallelOptions
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
                };

                try
                {
                    Parallel.ForEach(files, options, f =>
                    {
                        if (ct.IsCancellationRequested) return;
                        if (FileContainsAny(f, needles)) bag.Add(f);
                    });
                }
                catch (OperationCanceledException)
                {
                }

                var result = new List<string>(bag);
                result.Sort(StringComparer.OrdinalIgnoreCase);
                return result;
            }, ct);
        }

        private static bool FileContainsAny(string path, byte[][] needles)
        {
            int maxNeedle = 0;
            for (int i = 0; i < needles.Length; i++)
            {
                if (needles[i].Length > maxNeedle) maxNeedle = needles[i].Length;
            }

            if (maxNeedle == 0) return false;

            int overlap = maxNeedle - 1;

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, CHUNK, FileOptions.SequentialScan))
                {
                    var buffer = new byte[CHUNK + overlap];
                    int filled = 0;

                    while (true)
                    {
                        int read = fs.Read(buffer, filled, CHUNK);
                        if (read <= 0) break;

                        int total = filled + read;

                        for (int i = 0; i < needles.Length; i++)
                        {
                            if (IndexOfBytes(buffer, total, needles[i]) >= 0) return true;
                        }

                        if (total > overlap && overlap > 0)
                        {
                            Buffer.BlockCopy(buffer, total - overlap, buffer, 0, overlap);
                            filled = overlap;
                        }
                        else
                        {
                            filled = total;
                        }
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static int IndexOfBytes(byte[] haystack, int length, byte[] needle)
        {
            int limit = length - needle.Length;
            if (limit < 0) return -1;

            byte first = needle[0];

            for (int i = 0; i <= limit; i++)
            {
                if (haystack[i] != first) continue;

                int j = 1;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }

            return -1;
        }
    }

    internal sealed class AssetTextIndexInvalidator : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (importedAssets.Length > 0 || deletedAssets.Length > 0 || movedAssets.Length > 0)
            {
                AssetTextIndex.Invalidate();
                MonoScriptIndex.Invalidate();
            }
        }
    }
}
#endif
