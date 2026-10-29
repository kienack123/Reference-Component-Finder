#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace RefFinder
{
    internal static class YamlAssetScanner
    {
        private const string DOC_MARK = "--- !u!";
        private const int CLASS_GAMEOBJECT = 1;
        private const int CLASS_MONOBEHAVIOUR = 114;
        private const int CLASS_PREFAB_INSTANCE = 1001;

        private static readonly HashSet<string> StructuralKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_Script",
            "m_CorrespondingSourceObject",
            "m_SourcePrefab",
            "m_PrefabInstance",
            "m_PrefabAsset",
            "m_ParentPrefab",
            "target",
        };

        internal sealed class RawHit
        {
            public string filePath;
            public string propertyKey;
            public int line;
            public long ownerFileId;
            public int ownerClassId;
            public string ownerScriptGuid;
            public long ownerGameObjectId;
            public string ownerGameObjectName;
        }

        internal sealed class FileScanResult
        {
            public string filePath;
            public List<RawHit> hits = new List<RawHit>();
            public bool guidSeen;
        }

        public static FileScanResult ScanFile(string absolutePath, HashSet<string> targetGuids, HashSet<long> targetFileIds, CancellationToken ct)
        {
            var result = new FileScanResult { filePath = absolutePath };
            var goNames = new Dictionary<long, string>(512);

            long curFileId = 0;
            int curClassId = 0;
            string curScriptGuid = null;
            long curGoId = 0;
            string lastFieldKey = null;
            string lastPropertyPath = null;
            int lineNo = 0;

            try
            {
                using (var reader = new StreamReader(absolutePath, System.Text.Encoding.UTF8, true, 1 << 16))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lineNo++;
                        if ((lineNo & 1023) == 0 && ct.IsCancellationRequested) break;

                        if (line.StartsWith(DOC_MARK, StringComparison.Ordinal))
                        {
                            ParseDocHeader(line, out curClassId, out curFileId);
                            curScriptGuid = null;
                            curGoId = 0;
                            lastFieldKey = null;
                            lastPropertyPath = null;
                            continue;
                        }

                        string key = ParseKey(line);
                        if (key != null) lastFieldKey = key;

                        if (curClassId == CLASS_GAMEOBJECT && key == "m_Name")
                        {
                            goNames[curFileId] = ParseScalar(line);
                            continue;
                        }

                        if (key == "propertyPath")
                        {
                            lastPropertyPath = ParseScalar(line);
                        }

                        long refFileId;
                        string refGuid;
                        if (!TryParseRef(line, out refFileId, out refGuid)) continue;

                        if (key == "m_Script" && refGuid != null)
                        {
                            curScriptGuid = refGuid;
                            continue;
                        }

                        if (key == "m_GameObject")
                        {
                            curGoId = refFileId;
                            continue;
                        }

                        if (refGuid == null || !targetGuids.Contains(refGuid)) continue;

                        result.guidSeen = true;

                        if (key != null && StructuralKeys.Contains(key)) continue;
                        if (!targetFileIds.Contains(refFileId)) continue;

                        string propKey = key;
                        if (string.IsNullOrEmpty(propKey)) propKey = lastFieldKey;
                        if (propKey == "objectReference" && !string.IsNullOrEmpty(lastPropertyPath))
                            propKey = lastPropertyPath + " (override)";

                        result.hits.Add(new RawHit
                        {
                            filePath = absolutePath,
                            propertyKey = propKey ?? "(unknown)",
                            line = lineNo,
                            ownerFileId = curFileId,
                            ownerClassId = curClassId,
                            ownerScriptGuid = curScriptGuid,
                            ownerGameObjectId = curGoId,
                        });
                    }
                }
            }
            catch
            {
            }

            for (int i = 0; i < result.hits.Count; i++)
            {
                var h = result.hits[i];
                string name;
                if (h.ownerGameObjectId != 0 && goNames.TryGetValue(h.ownerGameObjectId, out name))
                    h.ownerGameObjectName = name;
                else if (h.ownerClassId == CLASS_PREFAB_INSTANCE)
                    h.ownerGameObjectName = "(prefab instance)";
                else
                    h.ownerGameObjectName = "(unknown)";
            }

            return result;
        }

        public static string ClassIdToName(int classId)
        {
            switch (classId)
            {
                case CLASS_GAMEOBJECT: return "GameObject";
                case CLASS_MONOBEHAVIOUR: return "MonoBehaviour";
                case CLASS_PREFAB_INSTANCE: return "PrefabInstance";
                case 4: return "Transform";
                case 224: return "RectTransform";
                default: return "Class" + classId.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static void ParseDocHeader(string line, out int classId, out long fileId)
        {
            classId = 0;
            fileId = 0;

            int start = DOC_MARK.Length;
            int amp = line.IndexOf('&', start);
            if (amp < 0) return;

            string classPart = line.Substring(start, amp - start).Trim();
            int.TryParse(classPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out classId);

            int end = amp + 1;
            while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '-')) end++;

            string idPart = line.Substring(amp + 1, end - amp - 1);
            long.TryParse(idPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out fileId);
        }

        private static string ParseKey(string line)
        {
            int i = 0;
            while (i < line.Length && line[i] == ' ') i++;
            if (i >= line.Length) return null;

            if (line[i] == '-')
            {
                i++;
                while (i < line.Length && line[i] == ' ') i++;
            }

            int start = i;
            while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
            if (i == start) return null;
            if (i >= line.Length || line[i] != ':') return null;

            return line.Substring(start, i - start);
        }

        private static string ParseScalar(string line)
        {
            int colon = line.IndexOf(':');
            if (colon < 0 || colon + 1 >= line.Length) return "";
            return line.Substring(colon + 1).Trim();
        }

        private static bool TryParseRef(string line, out long fileId, out string guid)
        {
            fileId = 0;
            guid = null;

            int f = line.IndexOf("{fileID:", StringComparison.Ordinal);
            if (f < 0) return false;

            int p = f + "{fileID:".Length;
            while (p < line.Length && line[p] == ' ') p++;

            int start = p;
            while (p < line.Length && (char.IsDigit(line[p]) || line[p] == '-')) p++;
            if (p == start) return false;

            if (!long.TryParse(line.Substring(start, p - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out fileId))
                return false;

            int g = line.IndexOf("guid:", p, StringComparison.Ordinal);
            if (g >= 0)
            {
                int q = g + "guid:".Length;
                while (q < line.Length && line[q] == ' ') q++;

                int gs = q;
                while (q < line.Length && Uri.IsHexDigit(line[q])) q++;

                if (q > gs) guid = line.Substring(gs, q - gs);
            }

            return true;
        }
    }
}
#endif
