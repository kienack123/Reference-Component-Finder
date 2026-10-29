#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace RefFinder
{
    internal static class MonoScriptIndex
    {
        private static Dictionary<Type, string> _map;

        public static void Invalidate()
        {
            _map = null;
        }

        public static string GetScriptPath(Type type)
        {
            if (type == null) return null;
            if (_map == null) Build();

            string path;
            return _map.TryGetValue(type, out path) ? path : null;
        }

        private static void Build()
        {
            _map = new Dictionary<Type, string>(4096);

            var guids = AssetDatabase.FindAssets("t:MonoScript");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;

                MonoScript ms = null;
                try
                {
                    ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                }
                catch
                {
                }

                if (ms == null) continue;

                Type cls = null;
                try
                {
                    cls = ms.GetClass();
                }
                catch
                {
                }

                if (cls == null) continue;
                if (!_map.ContainsKey(cls)) _map[cls] = path;
            }
        }

        public static bool IsProjectType(Type t)
        {
            if (t == null) return false;

            string an = t.Assembly.GetName().Name;
            if (an.StartsWith("UnityEngine", StringComparison.Ordinal)) return false;
            if (an.StartsWith("UnityEditor", StringComparison.Ordinal)) return false;
            if (an.StartsWith("System", StringComparison.Ordinal)) return false;
            if (an == "mscorlib" || an == "netstandard") return false;

            return true;
        }
    }

    internal sealed class ScriptCodeLocator
    {
        private const int HIERARCHY_LIMIT = 32;

        private readonly Dictionary<string, string[]> _fileLines = new Dictionary<string, string[]>(128);
        private readonly Dictionary<string, int> _lineCache = new Dictionary<string, int>(512);

        public void ClearRunCaches()
        {
            _fileLines.Clear();
            _lineCache.Clear();
        }

        public void Locate(UnityEngine.Object owner, string propertyPath, out string scriptPath, out int line)
        {
            scriptPath = null;
            line = -1;

            if (owner == null || !owner) return;

            string ownPath = GetOwnScriptPath(owner);
            var ownerType = owner.GetType();

            var leaf = ResolveLeafField(ownerType, propertyPath);
            if (leaf == null)
            {
                scriptPath = ownPath;
                return;
            }

            var declaringType = leaf.DeclaringType;

            if (!string.IsNullOrEmpty(ownPath))
            {
                int own = FindFieldLine(ownPath, declaringType.Name, leaf.Name);
                if (own > 0)
                {
                    scriptPath = ownPath;
                    line = own;
                    return;
                }
            }

            string typePath = MonoScriptIndex.GetScriptPath(declaringType);
            if (!string.IsNullOrEmpty(typePath))
            {
                int found = FindFieldLine(typePath, declaringType.Name, leaf.Name);
                if (found > 0)
                {
                    scriptPath = typePath;
                    line = found;
                    return;
                }

                scriptPath = typePath;
                return;
            }

            scriptPath = ownPath;
        }

        public void LocateField(UnityEngine.Object owner, FieldInfo field, out string scriptPath, out int line)
        {
            scriptPath = null;
            line = -1;

            if (field == null) return;

            var declaringType = field.DeclaringType;
            if (declaringType == null) return;

            string ownPath = owner != null && owner ? GetOwnScriptPath(owner) : null;

            if (!string.IsNullOrEmpty(ownPath))
            {
                int own = FindFieldLine(ownPath, declaringType.Name, field.Name);
                if (own > 0)
                {
                    scriptPath = ownPath;
                    line = own;
                    return;
                }
            }

            string typePath = MonoScriptIndex.GetScriptPath(declaringType);
            if (!string.IsNullOrEmpty(typePath))
            {
                int found = FindFieldLine(typePath, declaringType.Name, field.Name);
                scriptPath = typePath;
                if (found > 0) line = found;
                return;
            }

            scriptPath = ownPath;
        }

        public int LocateInFile(string csPath, string field)
        {
            if (string.IsNullOrEmpty(csPath) || string.IsNullOrEmpty(field)) return -1;
            return FindFieldLine(csPath, null, field);
        }

        private static FieldInfo ResolveLeafField(Type ownerType, string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath)) return null;

            var parts = propertyPath.Split('.');
            var cursor = ownerType;
            FieldInfo best = null;

            for (int i = 0; i < parts.Length; i++)
            {
                string segment = parts[i];
                if (segment.Length == 0) continue;
                if (segment == "Array") continue;

                if (segment.StartsWith("data[", StringComparison.Ordinal))
                {
                    cursor = GetElementType(cursor);
                    if (cursor == null) return best;
                    continue;
                }

                int bracket = segment.IndexOf('[');
                if (bracket > 0) segment = segment.Substring(0, bracket);

                var field = FindFieldInHierarchy(cursor, segment);
                if (field == null) return best;

                if (MonoScriptIndex.IsProjectType(field.DeclaringType)) best = field;

                cursor = field.FieldType;
                if (cursor == null) return best;
            }

            return best;
        }

        private static Type GetElementType(Type type)
        {
            if (type == null) return null;
            if (type.IsArray) return type.GetElementType();

            if (type.IsGenericType)
            {
                var args = type.GetGenericArguments();
                if (args.Length >= 1) return args[0];
            }

            return null;
        }

        private static FieldInfo FindFieldInHierarchy(Type type, string name)
        {
            var cursor = type;

            for (int guard = 0; cursor != null && guard < HIERARCHY_LIMIT; guard++)
            {
                FieldInfo field = null;
                try
                {
                    field = cursor.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch
                {
                }

                if (field != null) return field;
                cursor = cursor.BaseType;
            }

            return null;
        }

        private static string GetOwnScriptPath(UnityEngine.Object owner)
        {
            MonoScript ms = null;

            var mb = owner as MonoBehaviour;
            if (mb != null)
            {
                ms = MonoScript.FromMonoBehaviour(mb);
            }
            else
            {
                var so = owner as ScriptableObject;
                if (so != null) ms = MonoScript.FromScriptableObject(so);
            }

            if (ms == null) return null;

            string path = AssetDatabase.GetAssetPath(ms);
            if (string.IsNullOrEmpty(path)) return null;
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return null;

            return path;
        }

        private int FindFieldLine(string csPath, string typeName, string fieldName)
        {
            string key = csPath + "|" + (typeName ?? "*") + "|" + fieldName;

            int cached;
            if (_lineCache.TryGetValue(key, out cached)) return cached;

            int line = FindFieldLineUncached(csPath, typeName, fieldName);
            _lineCache[key] = line;

            return line;
        }

        private int FindFieldLineUncached(string csPath, string typeName, string fieldName)
        {
            var lines = GetLines(csPath);
            if (lines.Length == 0) return -1;

            if (!string.IsNullOrEmpty(typeName))
            {
                int start, end;
                if (TryFindTypeBody(lines, typeName, out start, out end))
                {
                    int scoped = FindDeclarationInRange(lines, fieldName, start, end);
                    if (scoped > 0) return scoped;
                }

                return -1;
            }

            return FindDeclarationInRange(lines, fieldName, 0, lines.Length - 1);
        }

        private static bool TryFindTypeBody(string[] lines, string typeName, out int start, out int end)
        {
            start = -1;
            end = -1;

            int declaration = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i];
                if (string.IsNullOrEmpty(s)) continue;

                string trimmed = s.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                if (trimmed.StartsWith("*", StringComparison.Ordinal)) continue;

                if (!ContainsWord(s, typeName)) continue;
                if (!ContainsWord(s, "class") && !ContainsWord(s, "struct")) continue;

                declaration = i;
                break;
            }

            if (declaration < 0) return false;

            int depth = 0;
            bool opened = false;

            for (int i = declaration; i < lines.Length; i++)
            {
                string s = lines[i];
                for (int c = 0; c < s.Length; c++)
                {
                    if (s[c] == '{')
                    {
                        depth++;
                        opened = true;
                    }
                    else if (s[c] == '}')
                    {
                        depth--;
                        if (opened && depth <= 0)
                        {
                            start = declaration;
                            end = i;
                            return true;
                        }
                    }
                }
            }

            if (opened)
            {
                start = declaration;
                end = lines.Length - 1;
                return true;
            }

            return false;
        }

        private static int FindDeclarationInRange(string[] lines, string fieldName, int start, int end)
        {
            int fallback = -1;

            if (start < 0) start = 0;
            if (end >= lines.Length) end = lines.Length - 1;

            for (int i = start; i <= end; i++)
            {
                string s = lines[i];
                if (string.IsNullOrEmpty(s)) continue;

                string trimmed = s.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                if (trimmed.StartsWith("*", StringComparison.Ordinal)) continue;

                int idx = 0;
                while (true)
                {
                    idx = s.IndexOf(fieldName, idx, StringComparison.Ordinal);
                    if (idx < 0) break;

                    int after = idx + fieldName.Length;

                    bool boundaryBefore = idx == 0 || !IsIdentChar(s[idx - 1]);
                    bool boundaryAfter = after >= s.Length || !IsIdentChar(s[after]);

                    if (boundaryBefore && boundaryAfter)
                    {
                        if (fallback < 0) fallback = i + 1;
                        if (LooksLikeDeclaration(s, idx, after)) return i + 1;
                    }

                    idx = after;
                }
            }

            return fallback;
        }

        private static bool LooksLikeDeclaration(string s, int wordStart, int wordEnd)
        {
            int a = wordEnd;
            while (a < s.Length && s[a] == ' ') a++;
            if (a >= s.Length) return false;

            char next = s[a];
            if (next != ';' && next != '=' && next != ',') return false;

            int b = wordStart - 1;
            while (b >= 0 && s[b] == ' ') b--;
            if (b < 0) return false;

            char prev = s[b];
            return IsIdentChar(prev) || prev == '>' || prev == ']' || prev == '?';
        }

        private static bool ContainsWord(string line, string word)
        {
            int idx = 0;

            while (true)
            {
                idx = line.IndexOf(word, idx, StringComparison.Ordinal);
                if (idx < 0) return false;

                int after = idx + word.Length;

                bool okBefore = idx == 0 || !IsIdentChar(line[idx - 1]);
                bool okAfter = after >= line.Length || !IsIdentChar(line[after]);

                if (okBefore && okAfter) return true;

                idx = after;
            }
        }

        private static bool IsIdentChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private string[] GetLines(string csPath)
        {
            string[] lines;
            if (_fileLines.TryGetValue(csPath, out lines)) return lines;

            try
            {
                lines = File.Exists(csPath) ? File.ReadAllLines(csPath) : File.ReadAllLines(Path.GetFullPath(csPath));
            }
            catch
            {
                lines = Array.Empty<string>();
            }

            _fileLines[csPath] = lines;
            return lines;
        }
    }
}
#endif
