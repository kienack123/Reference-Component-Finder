#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RefFinder
{
    internal sealed class RefFilter
    {
        private struct Token
        {
            public string key;
            public string value;
            public bool negate;
            public Regex regex;
        }

        private readonly List<Token> _tokens = new List<Token>(4);
        private string _text = "";

        public const string Syntax =
            "Filter: bare text matches any column.\n" +
            "scope: type: owner: prop: kind: path: code: bundle: detail: restrict to one column.\n" +
            "re:<pattern> is a regex. Prefix a term with '-' to exclude it.\n" +
            "Example:  scope:Prefab kind:UnityEvent -path:Legacy";

        public string Text { get { return _text; } }
        public bool IsEmpty { get { return _tokens.Count == 0; } }

        public void SetText(string text)
        {
            if (text == null) text = "";
            if (text == _text) return;

            _text = text;
            Parse();
        }

        private void Parse()
        {
            _tokens.Clear();

            var parts = Split(_text);

            for (int i = 0; i < parts.Count; i++)
            {
                string p = parts[i];
                if (string.IsNullOrEmpty(p)) continue;

                var t = new Token();

                if (p[0] == '-' && p.Length > 1)
                {
                    t.negate = true;
                    p = p.Substring(1);
                }

                int colon = p.IndexOf(':');
                if (colon > 0 && colon < p.Length - 1)
                {
                    t.key = p.Substring(0, colon).ToLowerInvariant();
                    t.value = p.Substring(colon + 1);
                }
                else
                {
                    t.key = "";
                    t.value = p;
                }

                if (t.key == "re")
                {
                    try
                    {
                        t.regex = new Regex(t.value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    }
                    catch
                    {
                        continue;
                    }
                }

                if (string.IsNullOrEmpty(t.value)) continue;
                _tokens.Add(t);
            }
        }

        private static List<string> Split(string text)
        {
            var list = new List<string>(4);
            int i = 0;

            while (i < text.Length)
            {
                while (i < text.Length && text[i] == ' ') i++;
                if (i >= text.Length) break;

                int start = i;
                bool quoted = false;

                while (i < text.Length)
                {
                    char c = text[i];
                    if (c == '"') quoted = !quoted;
                    else if (c == ' ' && !quoted) break;
                    i++;
                }

                list.Add(text.Substring(start, i - start).Replace("\"", ""));
            }

            return list;
        }

        public bool Pass(RefResult r)
        {
            for (int i = 0; i < _tokens.Count; i++)
            {
                bool hit = Match(r, _tokens[i]);
                if (_tokens[i].negate) hit = !hit;
                if (!hit) return false;
            }

            return true;
        }

        private static bool Match(RefResult r, Token t)
        {
            if (t.regex != null)
                return t.regex.IsMatch(Flatten(r));

            switch (t.key)
            {
                case "scope":
                    return Contains(r.scope.ToString(), t.value);
                case "type":
                    return Contains(r.ownerType, t.value);
                case "owner":
                    return Contains(r.ownerName, t.value) || Contains(r.ownerPath, t.value);
                case "prop":
                    return Contains(r.propertyPath, t.value) || Contains(r.propertyLabel, t.value);
                case "kind":
                    return Contains(r.refKind, t.value);
                case "path":
                    return Contains(r.assetPath, t.value) || Contains(r.ownerPath, t.value);
                case "code":
                    return Contains(r.scriptPath, t.value);
                case "bundle":
                    return Contains(r.bundleName, t.value);
                case "detail":
                    return Contains(r.detail, t.value);
                default:
                    return Contains(r.scope.ToString(), t.value)
                        || Contains(r.ownerType, t.value)
                        || Contains(r.ownerName, t.value)
                        || Contains(r.ownerPath, t.value)
                        || Contains(r.propertyLabel, t.value)
                        || Contains(r.propertyPath, t.value)
                        || Contains(r.refKind, t.value)
                        || Contains(r.detail, t.value)
                        || Contains(r.assetPath, t.value)
                        || Contains(r.bundleName, t.value)
                        || Contains(r.scriptPath, t.value);
            }
        }

        private static string Flatten(RefResult r)
        {
            return string.Concat(
                r.scope.ToString(), " ", r.ownerType, " ", r.ownerName, " ", r.ownerPath, " ",
                r.propertyLabel, " ", r.refKind, " ", r.detail, " ", r.assetPath, " ",
                r.bundleName, " ", r.scriptPath);
        }

        private static bool Contains(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack)) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
#endif
