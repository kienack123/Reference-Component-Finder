#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RefFinder
{
    internal static class ReferenceScan
    {
        public static ScanJob CreateScanJob(ScanTarget target, List<RefResult> results, ScanSettings settings)
        {
            return new ScanJob(target, results, settings);
        }
    }

    internal sealed class ScanJob
    {
        private const double NORMAL_SHARE = 0.25;
        private const double NORMAL_MIN_BUDGET_SECONDS = 0.004;
        private const double NORMAL_MAX_BUDGET_SECONDS = 0.016;

        private const double TURBO_SHARE = 0.50;
        private const double TURBO_MIN_BUDGET_SECONDS = 0.020;
        private const double TURBO_MAX_BUDGET_SECONDS = 0.060;

        private const int MAX_RESULTS = 20000;

        private enum Phase
        {
            Hierarchy = 0,
            Runtime = 1,
            TextIndex = 2,
            AssetFiles = 3,
            SceneFiles = 4,
            Done = 5,
        }

        private readonly ScanTarget _target;
        private readonly ScanSettings _settings;
        private readonly List<RefResult> _results;
        private readonly ScriptCodeLocator _code = new ScriptCodeLocator();

        private readonly List<Component> _compBuffer = new List<Component>(64);
        private readonly List<SerializedRefWalker.Hit> _hits = new List<SerializedRefWalker.Hit>(64);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private Phase _phase = Phase.Hierarchy;
        private bool _prepared;
        private bool _done;
        private bool _cancelled;
        private bool _waiting;
        private double _lastStepStart;

        private HierarchyWalk _walk;

        private bool _indexStarted;
        private Task<List<string>> _indexTask;
        private List<string> _assetCandidates;
        private List<string> _sceneCandidates;
        private int _assetIndex;

        private bool _yamlStarted;
        private Task<List<YamlAssetScanner.FileScanResult>> _yamlTask;

        private RuntimeHeapScanner _runtime;

        public int PrefabStageHits { get; private set; }
        public int SceneHits { get; private set; }
        public int PrefabAssetHits { get; private set; }
        public int SceneAssetHits { get; private set; }
        public int RuntimeHits { get; private set; }
        public string Note { get; private set; }

        public bool IsDone { get { return _done; } }
        public bool IsCancelled { get { return _cancelled; } }
        public string CurrentPhase { get { return _phase.ToString(); } }

        public float Progress
        {
            get
            {
                switch (_phase)
                {
                    case Phase.Hierarchy:
                        return _walk != null ? _walk.Progress : 0f;

                    case Phase.Runtime:
                        return _runtime != null ? _runtime.Progress : 0f;

                    case Phase.AssetFiles:
                        if (_assetCandidates == null || _assetCandidates.Count == 0) return 1f;
                        return Mathf.Clamp01((float)_assetIndex / _assetCandidates.Count);

                    case Phase.TextIndex:
                    case Phase.SceneFiles:
                        return -1f;

                    default:
                        return 1f;
                }
            }
        }

        public string ProgressText
        {
            get
            {
                switch (_phase)
                {
                    case Phase.Hierarchy:
                        return _walk != null ? _walk.ProgressText : "";

                    case Phase.Runtime:
                        return _runtime == null ? "skipped" : _runtime.ProgressText;

                    case Phase.TextIndex:
                        return _indexTask != null && !_indexTask.IsCompleted
                            ? "indexing " + AssetTextIndex.CachedFileCount + " files (background)"
                            : "index ready";

                    case Phase.AssetFiles:
                        return _assetCandidates == null
                            ? "skipped"
                            : _assetIndex + "/" + _assetCandidates.Count + " candidate assets";

                    case Phase.SceneFiles:
                        return _sceneCandidates == null
                            ? "skipped"
                            : "parsing " + _sceneCandidates.Count + " scene files";

                    default:
                        return "";
                }
            }
        }

        public ScanJob(ScanTarget target, List<RefResult> results, ScanSettings settings)
        {
            _target = target;
            _results = results;
            _settings = settings;
        }

        public void Cancel()
        {
            _cancelled = true;
            try
            {
                _cts.Cancel();
            }
            catch
            {
            }
        }

        public void Step()
        {
            if (_done) return;

            if (_cancelled || !_target.IsAlive)
            {
                Finish();
                return;
            }

            PrepareIfNeeded();

            double start = EditorApplication.timeSinceStartup;
            double budget = ResolveBudget(start);

            while (!_cancelled && !_done)
            {
                _waiting = false;
                Advance();

                if (_waiting) return;
                if (_phase == Phase.Done) break;
                if (_results.Count >= MAX_RESULTS)
                {
                    Note = "Stopped at " + MAX_RESULTS + " results.";
                    break;
                }
                if (EditorApplication.timeSinceStartup - start >= budget) return;
            }

            Finish();
        }

        private double ResolveBudget(double start)
        {
            double interval = _lastStepStart > 0.0 ? start - _lastStepStart : 0.0;
            _lastStepStart = start;

            double share = _settings.turbo ? TURBO_SHARE : NORMAL_SHARE;
            double min = _settings.turbo ? TURBO_MIN_BUDGET_SECONDS : NORMAL_MIN_BUDGET_SECONDS;
            double max = _settings.turbo ? TURBO_MAX_BUDGET_SECONDS : NORMAL_MAX_BUDGET_SECONDS;

            double budget = interval * share;
            if (budget < min) budget = min;
            if (budget > max) budget = max;

            return budget;
        }

        private void Advance()
        {
            switch (_phase)
            {
                case Phase.Hierarchy:
                    if (!StepWalk()) _phase = Phase.Runtime;
                    break;

                case Phase.Runtime:
                    if (!StepRuntime()) _phase = Phase.TextIndex;
                    break;

                case Phase.TextIndex:
                    if (!StepTextIndex()) _phase = Phase.AssetFiles;
                    break;

                case Phase.AssetFiles:
                    if (!StepAssetFiles()) _phase = Phase.SceneFiles;
                    break;

                case Phase.SceneFiles:
                    if (!StepSceneFiles()) _phase = Phase.Done;
                    break;

                default:
                    _phase = Phase.Done;
                    break;
            }
        }

        private void Finish()
        {
            _done = true;
            _phase = Phase.Done;

            try
            {
                _cts.Dispose();
            }
            catch
            {
            }
        }

        private void PrepareIfNeeded()
        {
            if (_prepared) return;
            _prepared = true;

            _walk = new HierarchyWalk();

            try
            {
                Prepare();
            }
            catch (Exception e)
            {
                Note = "Prepare failed: " + e.Message;
                Debug.LogException(e);
            }
        }

        private void Prepare()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            Scene stageScene = stage != null ? stage.scene : default(Scene);

            var scenes = new List<Scene>(SceneManager.sceneCount + 1);

            if (_settings.sceneScope == SceneScope.CurrentScene)
            {
                var active = SceneManager.GetActiveScene();
                if (active.IsValid() && active.isLoaded) scenes.Add(active);
            }
            else
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    if (s.IsValid() && s.isLoaded) scenes.Add(s);
                }
            }

            AddDontDestroyOnLoadScene(scenes);

            var seed = ResolveSeedTransform();
            if (seed != null)
            {
                Scene seedScene = seed.gameObject.scene;
                RefScope seedScope = stage != null && seedScene == stageScene ? RefScope.PrefabStage : RefScope.Scene;

                _walk.SetSeed(seed, seedScope);
                _walk.AddSource(seedScene, seedScope);
            }

            if (stage != null) _walk.AddSource(stageScene, RefScope.PrefabStage);

            for (int i = 0; i < scenes.Count; i++) _walk.AddSource(scenes[i], RefScope.Scene);
        }

        private Transform ResolveSeedTransform()
        {
            if (_target.mode != MatchMode.Instance) return null;

            var o = _target.primary;
            if (o == null || !o) return null;
            if (EditorUtility.IsPersistent(o)) return null;

            try
            {
                var component = o as Component;
                if (component != null) return component.transform;

                var go = o as GameObject;
                if (go != null) return go.transform;
            }
            catch (MissingReferenceException)
            {
            }

            return null;
        }

        private static void AddDontDestroyOnLoadScene(List<Scene> scenes)
        {
            if (!Application.isPlaying) return;

            Scene ddol;
            if (!TryGetDontDestroyOnLoadScene(out ddol)) return;

            for (int i = 0; i < scenes.Count; i++)
            {
                if (scenes[i] == ddol) return;
            }

            scenes.Add(ddol);
        }

        private bool StepWalk()
        {
            if (_walk == null) return false;

            GameObject go;
            RefScope scope;
            if (!_walk.StepOnce(out go, out scope)) return false;

            if (go != null) ScanGameObject(go, scope, null, null, null);
            return true;
        }

        private bool StepRuntime()
        {
            if (!_settings.scanRuntimeRefs) return false;
            if (_target.mode != MatchMode.Instance) return false;

            if (_runtime == null)
            {
                if (!Application.isPlaying) return false;

                _runtime = new RuntimeHeapScanner(_target, _results, _settings.scanRuntimeStatics, _code);
            }

            bool more = _runtime.StepOnce();
            RuntimeHits = _runtime.Hits;

            if (more) return true;

            if (!string.IsNullOrEmpty(_runtime.Note)) Note = _runtime.Note;
            return false;
        }

        private bool ShouldScanAssets()
        {
            if (!_settings.scanPrefabAssets && !_settings.scanAssetFiles && !_settings.scanUnopenedScenes) return false;
            if (_target.mode == MatchMode.Missing) return true;

            if (!_target.CanScanAssets)
            {
                Note = "Target is scene-only (no prefab source), so no asset can reference it.";
                return false;
            }

            if (!AssetTextIndex.IsTextSerialized)
            {
                Note = "Asset scan needs Asset Serialization = Force Text (Project Settings > Editor).";
                return false;
            }

            return true;
        }

        private bool StepTextIndex()
        {
            if (!_indexStarted)
            {
                _indexStarted = true;

                if (!ShouldScanAssets()) return false;

                if (_target.mode == MatchMode.Missing)
                {
                    BuildCandidateLists(FilterEnabledFiles(AssetTextIndex.GetProjectFiles()));
                    return false;
                }

                List<string> cached;
                if (AssetTextIndex.TryGetCached(_target.SourceGuids, out cached))
                {
                    BuildCandidateLists(cached);
                    return false;
                }

                var files = FilterEnabledFiles(AssetTextIndex.GetProjectFiles());
                _indexTask = AssetTextIndex.FindFilesContainingAsync(files, _target.SourceGuids, _cts.Token);
                _waiting = true;
                return true;
            }

            if (_indexTask == null) return false;

            if (!_indexTask.IsCompleted)
            {
                _waiting = true;
                return true;
            }

            if (_indexTask.Status == TaskStatus.RanToCompletion)
            {
                var hits = _indexTask.Result;
                AssetTextIndex.StoreCached(_target.SourceGuids, hits);
                BuildCandidateLists(hits);
            }

            _indexTask = null;
            return false;
        }

        private string[] FilterEnabledFiles(string[] all)
        {
            var list = new List<string>(all.Length);

            for (int i = 0; i < all.Length; i++)
            {
                string ext = Path.GetExtension(all[i]);

                if (ext.Equals(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    if (_settings.scanPrefabAssets) list.Add(all[i]);
                }
                else if (ext.Equals(".asset", StringComparison.OrdinalIgnoreCase))
                {
                    if (_settings.scanAssetFiles) list.Add(all[i]);
                }
                else if (ext.Equals(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    if (_settings.scanUnopenedScenes) list.Add(all[i]);
                }
            }

            return list.ToArray();
        }

        private void BuildCandidateLists(IList<string> absolutePaths)
        {
            _assetCandidates = new List<string>(absolutePaths.Count);
            _sceneCandidates = new List<string>();

            var loadedScenePaths = GetLoadedScenePaths();

            for (int i = 0; i < absolutePaths.Count; i++)
            {
                string assetPath = AssetTextIndex.ToAssetPath(absolutePaths[i]);
                if (string.IsNullOrEmpty(assetPath)) continue;

                if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    if (_settings.scanUnopenedScenes && !loadedScenePaths.Contains(assetPath))
                        _sceneCandidates.Add(absolutePaths[i]);
                }
                else
                {
                    _assetCandidates.Add(assetPath);
                }
            }
        }

        private static HashSet<string> GetLoadedScenePaths()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.IsValid() && !string.IsNullOrEmpty(s.path)) set.Add(s.path);
            }

            return set;
        }

        private bool StepAssetFiles()
        {
            if (_assetCandidates == null) return false;

            while (_assetIndex < _assetCandidates.Count)
            {
                string path = _assetCandidates[_assetIndex++];
                ScanAssetFile(path);
                return true;
            }

            return false;
        }

        private void ScanAssetFile(string assetPath)
        {
            UnityEngine.Object[] objs;
            try
            {
                objs = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            }
            catch
            {
                return;
            }

            if (objs == null || objs.Length == 0) return;

            UnityEngine.Object main = null;
            try
            {
                main = AssetDatabase.LoadMainAssetAtPath(assetPath);
            }
            catch
            {
            }

            string bundle = _settings.resolveBundleNames ? GetBundleName(assetPath) : null;
            bool isPrefab = assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
            RefScope scope = isPrefab ? RefScope.PrefabAsset : RefScope.AssetFile;

            for (int i = 0; i < objs.Length; i++)
            {
                var o = objs[i];
                if (o == null || !o) continue;
                if (o is GameObject) continue;
                if (o is MonoScript) continue;

                if (_settings.onlyMonoBehaviours && !(o is MonoBehaviour) && !(o is ScriptableObject)) continue;

                UnityEngine.Object ping = main != null && main ? main : o;
                CollectFrom(o, scope, ping, assetPath, bundle);
            }
        }

        private bool StepSceneFiles()
        {
            if (_sceneCandidates == null || _sceneCandidates.Count == 0) return false;

            if (!_yamlStarted)
            {
                _yamlStarted = true;

                var guidSet = new HashSet<string>(_target.SourceGuids, StringComparer.Ordinal);
                var idSet = new HashSet<long>(_target.SourceFileIds);
                var files = _sceneCandidates.ToArray();
                var token = _cts.Token;

                _yamlTask = Task.Run(() =>
                {
                    var bag = new ConcurrentBag<YamlAssetScanner.FileScanResult>();

                    var options = new ParallelOptions
                    {
                        CancellationToken = token,
                        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
                    };

                    try
                    {
                        Parallel.ForEach(files, options, f =>
                        {
                            if (token.IsCancellationRequested) return;
                            var r = YamlAssetScanner.ScanFile(f, guidSet, idSet, token);
                            if (r.guidSeen || r.hits.Count > 0) bag.Add(r);
                        });
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    return new List<YamlAssetScanner.FileScanResult>(bag);
                }, token);

                _waiting = true;
                return true;
            }

            if (_yamlTask == null) return false;

            if (!_yamlTask.IsCompleted)
            {
                _waiting = true;
                return true;
            }

            if (_yamlTask.Status == TaskStatus.RanToCompletion)
                ConsumeYamlResults(_yamlTask.Result);

            _yamlTask = null;
            return false;
        }

        private void ConsumeYamlResults(List<YamlAssetScanner.FileScanResult> results)
        {
            for (int i = 0; i < results.Count; i++)
            {
                var fr = results[i];

                string assetPath = AssetTextIndex.ToAssetPath(fr.filePath);
                if (string.IsNullOrEmpty(assetPath)) continue;

                UnityEngine.Object ping = AssetDatabase.LoadAssetAtPath<SceneAsset>(assetPath);
                string bundle = _settings.resolveBundleNames ? GetBundleName(assetPath) : null;

                if (fr.hits.Count == 0)
                {
                    if (!fr.guidSeen) continue;

                    _results.Add(new RefResult
                    {
                        scope = RefScope.SceneAsset,
                        confidence = RefConfidence.Candidate,
                        ping = ping,
                        assetPath = assetPath,
                        bundleName = bundle ?? "",
                        ownerType = "(scene)",
                        ownerName = Path.GetFileNameWithoutExtension(assetPath),
                        ownerPath = "",
                        propertyPath = "",
                        propertyLabel = "(open scene to confirm)",
                        refKind = RefKind.Asset,
                        detail = "scene uses the target prefab; exact field needs the scene open",
                    });

                    SceneAssetHits++;
                    continue;
                }

                for (int h = 0; h < fr.hits.Count; h++)
                {
                    var hit = fr.hits[h];

                    string scriptPath = null;
                    string ownerType = YamlAssetScanner.ClassIdToName(hit.ownerClassId);

                    if (!string.IsNullOrEmpty(hit.ownerScriptGuid))
                    {
                        string p = AssetDatabase.GUIDToAssetPath(hit.ownerScriptGuid);
                        if (!string.IsNullOrEmpty(p) && p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                        {
                            scriptPath = p;
                            ownerType = Path.GetFileNameWithoutExtension(p);
                        }
                    }

                    int line = -1;
                    if (!string.IsNullOrEmpty(scriptPath))
                    {
                        string fieldName = hit.propertyKey;
                        int space = fieldName.IndexOf(' ');
                        if (space > 0) fieldName = fieldName.Substring(0, space);

                        line = _code.LocateInFile(scriptPath, SerializedRefWalker.RootFieldName(fieldName));
                    }

                    _results.Add(new RefResult
                    {
                        scope = RefScope.SceneAsset,
                        confidence = RefConfidence.Confirmed,
                        ping = ping,
                        assetPath = assetPath,
                        bundleName = bundle ?? "",
                        ownerType = ownerType,
                        ownerName = hit.ownerGameObjectName,
                        ownerPath = "",
                        propertyPath = hit.propertyKey,
                        propertyLabel = hit.propertyKey,
                        refKind = RefKind.Asset,
                        detail = Path.GetFileName(assetPath) + ":" + hit.line,
                        scriptPath = scriptPath,
                        line = line,
                        yamlLine = hit.line,
                    });

                    SceneAssetHits++;
                }
            }
        }

        private void ScanGameObject(GameObject go, RefScope scope, UnityEngine.Object ping, string assetPath, string bundle)
        {
            if (go == null) return;

            _compBuffer.Clear();
            go.GetComponents(_compBuffer);

            UnityEngine.Object pingObj = ping != null && ping ? ping : go;

            for (int i = 0; i < _compBuffer.Count; i++)
            {
                var comp = _compBuffer[i];

                if (comp == null)
                {
                    if (_target.mode == MatchMode.Missing)
                    {
                        _results.Add(new RefResult
                        {
                            scope = scope,
                            ping = pingObj,
                            assetPath = assetPath ?? "",
                            bundleName = bundle ?? "",
                            ownerType = "(missing script)",
                            ownerName = go.name,
                            ownerPath = TransformPath(go.transform),
                            propertyPath = "m_Script",
                            propertyLabel = "m_Script",
                            refKind = RefKind.MissingScript,
                            detail = "script asset missing or failed to compile",
                        });

                        CountHit(scope);
                    }

                    continue;
                }

                if (_settings.onlyMonoBehaviours && !(comp is MonoBehaviour)) continue;
                if (!_settings.includeInternalLinks && comp is Transform) continue;

                CollectFrom(comp, scope, pingObj, assetPath, bundle);
            }
        }

        private void CollectFrom(UnityEngine.Object owner, RefScope scope, UnityEngine.Object ping, string assetPath, string bundle)
        {
            if (_results.Count >= MAX_RESULTS) return;

            SerializedObject so;
            try
            {
                so = new SerializedObject(owner);
            }
            catch
            {
                return;
            }

            try
            {
                bool missingMode = _target.mode == MatchMode.Missing;

                _hits.Clear();
                SerializedRefWalker.Collect(so, _settings.includeInternalLinks, missingMode, _hits);

                for (int i = 0; i < _hits.Count; i++)
                {
                    var h = _hits[i];
                    string kind;

                    if (h.missing)
                    {
                        if (!missingMode) continue;
                        kind = RefKind.MissingRef;
                    }
                    else if (missingMode)
                    {
                        continue;
                    }
                    else if (!_target.TryMatch(h.value, out kind))
                    {
                        continue;
                    }

                    _results.Add(MakeResult(so, owner, scope, ping, assetPath, bundle, h, kind));
                    CountHit(scope);
                }
            }
            finally
            {
                so.Dispose();
            }
        }

        private void CountHit(RefScope scope)
        {
            switch (scope)
            {
                case RefScope.PrefabStage:
                    PrefabStageHits++;
                    break;
                case RefScope.Scene:
                    SceneHits++;
                    break;
                case RefScope.PrefabAsset:
                case RefScope.AssetFile:
                    PrefabAssetHits++;
                    break;
                case RefScope.SceneAsset:
                    SceneAssetHits++;
                    break;
            }
        }

        private RefResult MakeResult(
            SerializedObject so,
            UnityEngine.Object owner,
            RefScope scope,
            UnityEngine.Object ping,
            string assetPath,
            string bundle,
            SerializedRefWalker.Hit hit,
            string kind)
        {
            var r = new RefResult
            {
                scope = scope,
                confidence = RefConfidence.Confirmed,
                ping = ping,
                assetPath = assetPath ?? "",
                bundleName = bundle ?? "",
                propertyPath = hit.propertyPath,
                refKind = kind,
                ownerType = owner.GetType().Name,
            };

            var comp = owner as Component;
            if (comp != null)
            {
                try
                {
                    var go = comp.gameObject;
                    r.ownerName = go ? go.name : "(no GameObject)";
                    r.ownerPath = TransformPath(comp.transform);
                }
                catch (MissingReferenceException)
                {
                    r.ownerName = "(destroyed)";
                }
            }
            else
            {
                r.ownerName = owner.name;
            }

            string eventName, methodName;
            if (SerializedRefWalker.TryDescribeUnityEvent(so, hit.propertyPath, out eventName, out methodName))
            {
                if (kind != RefKind.MissingRef) r.refKind = RefKind.UnityEvent;
                r.propertyLabel = eventName + "()";
                r.detail = string.IsNullOrEmpty(methodName) ? "(no method)" : methodName + "()";
            }
            else
            {
                r.propertyLabel = hit.propertyPath;
                r.detail = hit.value != null && hit.value ? hit.value.name : "";
            }

            string scriptPath;
            int line;
            _code.Locate(owner, hit.propertyPath, out scriptPath, out line);
            r.scriptPath = scriptPath;
            r.line = line;

            return r;
        }

        private static string TransformPath(Transform t)
        {
            if (t == null) return "";

            var sb = new StringBuilder(t.name);
            var p = t.parent;

            int guard = 0;
            while (p != null && guard++ < 64)
            {
                sb.Insert(0, "/");
                sb.Insert(0, p.name);
                p = p.parent;
            }

            return sb.ToString();
        }

        private static string GetBundleName(string assetPath)
        {
            try
            {
                var importer = AssetImporter.GetAtPath(assetPath);
                return importer != null ? importer.assetBundleName : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetDontDestroyOnLoadScene(out Scene scene)
        {
            scene = default(Scene);
            if (!Application.isPlaying) return false;

            GameObject probe = null;

            try
            {
                probe = new GameObject("~ddol_probe~");
                probe.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(probe);
                scene = probe.scene;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (probe != null)
                {
                    try
                    {
                        UnityEngine.Object.DestroyImmediate(probe);
                    }
                    catch
                    {
                    }
                }
            }

            return scene.IsValid();
        }
    }
}
#endif
