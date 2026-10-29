#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace RefFinder
{
    internal sealed class ScanTarget
    {
        private const int SOURCE_CHAIN_LIMIT = 8;
        private const int RESOLVE_CACHE_LIMIT = 20000;

        public MatchMode mode = MatchMode.Instance;
        public UnityEngine.Object primary;
        public string label = "(none)";

        private readonly List<UnityEngine.Object> _match = new List<UnityEngine.Object>(8);
        private readonly List<string> _matchKinds = new List<string>(8);
        private readonly List<string> _sourceGuids = new List<string>(2);
        private readonly List<string> _sourceAssetPaths = new List<string>(2);
        private readonly List<long> _sourceFileIds = new List<long>(8);
        private readonly Dictionary<UnityEngine.Object, string> _resolveCache = new Dictionary<UnityEngine.Object, string>(1024);

        public IList<string> SourceGuids { get { return _sourceGuids; } }
        public IList<string> SourceAssetPaths { get { return _sourceAssetPaths; } }
        public IList<long> SourceFileIds { get { return _sourceFileIds; } }

        public bool CanScanAssets { get { return mode == MatchMode.Instance && _sourceGuids.Count > 0; } }

        public bool IsAlive
        {
            get
            {
                if (mode == MatchMode.Missing) return true;
                return primary != null && primary;
            }
        }

        public static ScanTarget FromComponent(Component c)
        {
            var t = new ScanTarget();
            t.mode = MatchMode.Instance;
            t.primary = c;
            t.label = Describe(c);

            t.Add(c, RefKind.Component);
            try
            {
                t.Add(c.gameObject, RefKind.GameObject);
                t.Add(c.transform, RefKind.Transform);
            }
            catch (MissingReferenceException)
            {
            }

            t.ResolveSources();
            return t;
        }

        public static ScanTarget FromGameObject(GameObject go)
        {
            var t = new ScanTarget();
            t.mode = MatchMode.Instance;
            t.primary = go;
            t.label = "GameObject '" + go.name + "'";

            t.Add(go, RefKind.GameObject);
            t.Add(go.transform, RefKind.Transform);

            t.ResolveSources();
            return t;
        }

        public static ScanTarget FromAsset(UnityEngine.Object asset)
        {
            var t = new ScanTarget();
            t.mode = MatchMode.Instance;
            t.primary = asset;
            t.label = asset.GetType().Name + " asset '" + asset.name + "'";

            var go = asset as GameObject;
            if (go != null)
            {
                t.Add(go, RefKind.GameObject);
                t.Add(go.transform, RefKind.Transform);
            }
            else
            {
                t.Add(asset, RefKind.Asset);
            }

            t.ResolveSources();
            return t;
        }

        public static ScanTarget ForMissing()
        {
            var t = new ScanTarget();
            t.mode = MatchMode.Missing;
            t.label = "missing references";
            return t;
        }

        public static string Describe(Component c)
        {
            if (c == null || !c) return "(missing)";

            try
            {
                var go = c.gameObject;
                return c.GetType().Name + " on '" + (go ? go.name : "(no GameObject)") + "'";
            }
            catch (MissingReferenceException)
            {
                return "(destroyed)";
            }
        }

        private void Add(UnityEngine.Object o, string kind)
        {
            if (o == null || !o) return;

            for (int i = 0; i < _match.Count; i++)
            {
                if (_match[i] == o) return;
            }

            _match.Add(o);
            _matchKinds.Add(kind);
        }

        private void ResolveSources()
        {
            int liveCount = _match.Count;

            for (int i = 0; i < liveCount; i++)
            {
                var o = _match[i];
                string kind = _matchKinds[i];
                if (o == null || !o) continue;

                if (EditorUtility.IsPersistent(o))
                {
                    RegisterSource(o);
                    continue;
                }

                var cur = o;
                for (int step = 0; step < SOURCE_CHAIN_LIMIT; step++)
                {
                    UnityEngine.Object src = null;
                    try
                    {
                        src = PrefabUtility.GetCorrespondingObjectFromSource(cur);
                    }
                    catch
                    {
                    }

                    if (src == null || !src || src == cur) break;

                    Add(src, kind);
                    if (EditorUtility.IsPersistent(src)) RegisterSource(src);

                    cur = src;
                }
            }

            _sourceFileIds.Clear();
            for (int i = 0; i < _match.Count; i++)
            {
                var o = _match[i];
                if (o == null || !o) continue;
                if (!EditorUtility.IsPersistent(o)) continue;

                try
                {
                    var gid = GlobalObjectId.GetGlobalObjectIdSlow(o);
                    long fileId = unchecked((long)gid.targetObjectId);
                    if (fileId != 0 && !_sourceFileIds.Contains(fileId))
                        _sourceFileIds.Add(fileId);
                }
                catch
                {
                }
            }
        }

        private void RegisterSource(UnityEngine.Object persistent)
        {
            string path = AssetDatabase.GetAssetPath(persistent);
            if (string.IsNullOrEmpty(path)) return;
            if (_sourceAssetPaths.Contains(path)) return;

            string guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) return;

            _sourceAssetPaths.Add(path);
            if (!_sourceGuids.Contains(guid)) _sourceGuids.Add(guid);
        }

        public bool TryMatch(UnityEngine.Object v, out string kind)
        {
            kind = null;
            if (v == null || !v) return false;

            if (_resolveCache.TryGetValue(v, out kind))
                return kind != null;

            kind = Resolve(v);

            if (_resolveCache.Count > RESOLVE_CACHE_LIMIT) _resolveCache.Clear();
            _resolveCache[v] = kind;

            return kind != null;
        }

        private string Resolve(UnityEngine.Object v)
        {
            for (int i = 0; i < _match.Count; i++)
            {
                if (_match[i] == v) return _matchKinds[i];
            }

            var cur = v;
            for (int step = 0; step < SOURCE_CHAIN_LIMIT; step++)
            {
                UnityEngine.Object src = null;
                try
                {
                    src = PrefabUtility.GetCorrespondingObjectFromSource(cur);
                }
                catch
                {
                }

                if (src == null || !src || src == cur) break;

                for (int i = 0; i < _match.Count; i++)
                {
                    if (_match[i] == src) return _matchKinds[i];
                }

                cur = src;
            }

            return null;
        }

        public void ClearCaches()
        {
            _resolveCache.Clear();
        }
    }
}
#endif
