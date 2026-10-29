#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;

namespace RefFinder
{
    internal sealed class FindRefsWindow : EditorWindow
    {
        private const string PREF_PREFIX = "ComponentRefFinder.";
        private const string PREF_SCENE_SCOPE = PREF_PREFIX + "sceneScope";
        private const string PREF_SCAN_PREFABS = PREF_PREFIX + "scanPrefabAssets";
        private const string PREF_SCAN_ASSETS = PREF_PREFIX + "scanAssetFiles";
        private const string PREF_SCAN_SCENES = PREF_PREFIX + "scanUnopenedScenes";
        private const string PREF_ONLY_MB = PREF_PREFIX + "onlyMonoBehaviours";
        private const string PREF_INTERNAL = PREF_PREFIX + "includeInternalLinks";
        private const string PREF_TURBO = PREF_PREFIX + "turbo";
        private const string PREF_BUNDLES = PREF_PREFIX + "resolveBundleNames";
        private const string PREF_RUNTIME = PREF_PREFIX + "scanRuntimeRefs";
        private const string PREF_RUNTIME_STATICS = PREF_PREFIX + "scanRuntimeStatics";
        private const string PREF_ONLY_CODE = PREF_PREFIX + "onlyWithCode";
        private const string PREF_GROUPED = PREF_PREFIX + "grouped";
        private const string PREF_LOG_HITS = PREF_PREFIX + "logHits";

        private static readonly string[] SCENE_SCOPE_OPTIONS = { "Current Scene", "All Loaded Scenes" };
        private static readonly List<FindRefsWindow> _openWindows = new List<FindRefsWindow>();

        [SerializeField] private bool _locked;

        private ScanTarget _target;
        private ScanJob _job;
        private FindRefsListView _view;
        private List<RefResult> _results = new List<RefResult>();
        private readonly ScanSettings _settings = new ScanSettings();

        private string _filterText = "";
        private string _notice;
        private bool _onlyWithCode;
        private bool _grouped;
        private bool _logHits;
        private bool _showHelp;

        public static void Open(ScanTarget target)
        {
            FindRefsWindow w = null;

            for (int i = 0; i < _openWindows.Count; i++)
            {
                if (_openWindows[i] != null && !_openWindows[i]._locked)
                {
                    w = _openWindows[i];
                    break;
                }
            }

            if (w == null)
            {
                w = CreateInstance<FindRefsWindow>();
                w.titleContent = new GUIContent("Find Refs");
                w.minSize = new Vector2(640f, 260f);
                w.Show();
            }

            w.SetTarget(target);
            w.Focus();
        }

        private void OnEnable()
        {
            if (!_openWindows.Contains(this)) _openWindows.Add(this);

            titleContent = new GUIContent("Find Refs");
            LoadPrefs();

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private void OnDisable()
        {
            _openWindows.Remove(this);
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            StopScan();
        }

        private void LoadPrefs()
        {
            _settings.sceneScope = (SceneScope)EditorPrefs.GetInt(PREF_SCENE_SCOPE, (int)SceneScope.AllLoadedScenes);
            _settings.scanPrefabAssets = EditorPrefs.GetBool(PREF_SCAN_PREFABS, true);
            _settings.scanAssetFiles = EditorPrefs.GetBool(PREF_SCAN_ASSETS, true);
            _settings.scanUnopenedScenes = EditorPrefs.GetBool(PREF_SCAN_SCENES, false);
            _settings.onlyMonoBehaviours = EditorPrefs.GetBool(PREF_ONLY_MB, true);
            _settings.includeInternalLinks = EditorPrefs.GetBool(PREF_INTERNAL, false);
            _settings.turbo = EditorPrefs.GetBool(PREF_TURBO, false);
            _settings.resolveBundleNames = EditorPrefs.GetBool(PREF_BUNDLES, false);
            _settings.scanRuntimeRefs = EditorPrefs.GetBool(PREF_RUNTIME, true);
            _settings.scanRuntimeStatics = EditorPrefs.GetBool(PREF_RUNTIME_STATICS, true);

            _onlyWithCode = EditorPrefs.GetBool(PREF_ONLY_CODE, false);
            _grouped = EditorPrefs.GetBool(PREF_GROUPED, false);
            _logHits = EditorPrefs.GetBool(PREF_LOG_HITS, false);
        }

        private void SetTarget(ScanTarget target)
        {
            StopScan();

            _target = target;
            _results = new List<RefResult>(256);
            _notice = null;

            if (_view != null)
            {
                _view.SetResults(_results);
                _view.MarkDirty();
            }

            StartScan();
        }

        private void EnsureView()
        {
            if (_view != null) return;

            _view = new FindRefsListView();
            _view.Activated += OnActivated;
            _view.OpenCodeRequested += OpenCode;
            _view.CopyRequested += r => EditorGUIUtility.systemCopyBuffer = FormatRow(r);
            _view.ContextRequested += OnContextRequested;
            _view.RepaintRequested += Repaint;
            _view.onlyWithCode = _onlyWithCode;
            _view.grouped = _grouped;
            _view.SetResults(_results);
            _view.SetFilterText(_filterText);
            _view.RebuildIfDirty(true);
        }

        private void StartScan()
        {
            StopScan();

            if (_target == null || !_target.IsAlive) return;

            _job = ReferenceScan.CreateScanJob(_target, _results, _settings.Clone());
            EditorApplication.update += Tick;
        }

        private void RestartScan()
        {
            if (_target == null) return;

            _results.Clear();
            _target.ClearCaches();

            if (_view != null)
            {
                _view.SetResults(_results);
                _view.MarkDirty();
            }

            StartScan();
        }

        private void StopScan()
        {
            if (_job != null)
            {
                _job.Cancel();
                _job = null;
            }

            EditorApplication.update -= Tick;
        }

        private void Tick()
        {
            if (_target == null || !_target.IsAlive)
            {
                StopScan();
                Repaint();
                return;
            }

            if (_job == null)
            {
                EditorApplication.update -= Tick;
                return;
            }

            _job.Step();

            if (_view != null) _view.MarkDirty();

            if (_job.IsDone)
            {
                if (_logHits) Debug.Log("[FindRefs] " + _target.label + " -> " + _results.Count + " result(s)");
                EditorApplication.update -= Tick;
            }

            Repaint();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode ||
                state == PlayModeStateChange.EnteredPlayMode ||
                state == PlayModeStateChange.ExitingPlayMode)
            {
                AutoClearTarget("PlayMode change: " + state);
            }
        }

        private void AutoClearTarget(string reason)
        {
            if (_target == null) return;

            StopScan();

            bool persistent = _target.primary != null && _target.primary && EditorUtility.IsPersistent(_target.primary);
            bool sceneBound = _target.mode == MatchMode.Instance;

            if (!persistent && sceneBound)
            {
                _target = null;
                _results.Clear();
                if (_view != null) _view.MarkDirty();
                _notice = "Target cleared (" + reason + "). Select a component and run Find Refs again.";
            }
            else
            {
                _notice = "Scan stopped (" + reason + "). Target is an asset and was kept.";
            }

            Repaint();
        }

        private void OnGUI()
        {
            EnsureView();

            DrawHeader();

            if (!string.IsNullOrEmpty(_notice))
                EditorGUILayout.HelpBox(_notice, MessageType.Info);

            if (_job != null && !string.IsNullOrEmpty(_job.Note))
                EditorGUILayout.HelpBox(_job.Note, MessageType.Warning);

            if (_target == null)
            {
                EditorGUILayout.HelpBox(
                    "No target. Right-click a Component header > Find Refs, a GameObject in the Hierarchy, or an asset in the Project window.",
                    MessageType.Info);
                return;
            }

            DrawProgress();
            DrawPlayModeHint();
            DrawToolbar();

            if (_showHelp)
                EditorGUILayout.HelpBox(RefFilter.Syntax, MessageType.None);

            _view.RebuildIfDirty(_job == null || _job.IsDone);

            var rect = GUILayoutUtility.GetRect(0f, 100000f, 0f, 100000f);
            _view.Draw(rect);
        }

        private void DrawHeader()
        {
            bool scanning = _job != null && !_job.IsDone;

            string status;
            if (_job == null) status = "Idle";
            else if (_job.IsDone) status = _job.IsCancelled ? "Cancelled" : "Done";
            else status = "Scanning (" + _job.CurrentPhase + ") — " + _job.ProgressText;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                string targetLabel = _target != null ? _target.label : "(none)";
                string counts = _job == null
                    ? ""
                    : "   stage:" + _job.PrefabStageHits + "  scene:" + _job.SceneHits +
                      "  assets:" + _job.PrefabAssetHits + "  sceneFiles:" + _job.SceneAssetHits +
                      "  runtime:" + _job.RuntimeHits;

                GUILayout.Label(
                    "Target: " + targetLabel + "\nStatus: " + status + counts,
                    EditorStyles.wordWrappedLabel);

                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(!scanning))
                {
                    if (GUILayout.Button("Cancel", GUILayout.Width(70)) && _job != null) _job.Cancel();
                }

                bool locked = GUILayout.Toggle(_locked, _locked ? "Locked" : "Lock", EditorStyles.miniButton, GUILayout.Width(60));
                if (locked != _locked) _locked = locked;
            }
        }

        private void DrawProgress()
        {
            if (_job == null || _job.IsDone) return;

            var rect = GUILayoutUtility.GetRect(0f, 18f, GUILayout.ExpandWidth(true));
            rect.xMin += 4f;
            rect.xMax -= 4f;

            float progress = _job.Progress;
            string label = _job.CurrentPhase + "   ·   " + _job.ProgressText + "   ·   " + _results.Count + " found";

            if (progress < 0f)
            {
                float sweep = Mathf.PingPong((float)EditorApplication.timeSinceStartup * 0.6f, 1f);
                EditorGUI.ProgressBar(rect, sweep, label + "   (background)");
            }
            else
            {
                EditorGUI.ProgressBar(rect, progress, label + "   " + Mathf.RoundToInt(progress * 100f) + "%");
            }
        }

        private void DrawPlayModeHint()
        {
            if (!Application.isPlaying) return;
            if (_settings.scanRuntimeRefs) return;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(
                    "Play Mode: 'Runtime C#' scan is OFF, so references held by non-serialized fields, " +
                    "Dictionaries, statics or runtime AddListener closures are NOT searched.",
                    EditorStyles.wordWrappedMiniLabel);

                if (GUILayout.Button("Enable", GUILayout.Width(64), GUILayout.Height(28)))
                {
                    _settings.scanRuntimeRefs = true;
                    EditorPrefs.SetBool(PREF_RUNTIME, true);
                    RestartScan();
                }
            }
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                int shown = _view != null ? _view.VisibleCount : 0;
                GUILayout.Label("Results: " + shown + " / " + _results.Count, GUILayout.Width(120));

                GUILayout.Label(
                    new GUIContent("Scope", "In Play Mode the DontDestroyOnLoad scene is always scanned, in both modes."),
                    GUILayout.Width(38));

                bool changed = false;

                int scopeIndex = EditorGUILayout.Popup((int)_settings.sceneScope, SCENE_SCOPE_OPTIONS, EditorStyles.toolbarPopup, GUILayout.Width(120));
                if (scopeIndex != (int)_settings.sceneScope)
                {
                    _settings.sceneScope = (SceneScope)scopeIndex;
                    EditorPrefs.SetInt(PREF_SCENE_SCOPE, scopeIndex);
                    changed = true;
                }

                changed |= ToolbarToggle(ref _settings.scanPrefabAssets, "PrefabAssets", PREF_SCAN_PREFABS, 90,
                    "Scan .prefab assets that reference the target");
                changed |= ToolbarToggle(ref _settings.onlyMonoBehaviours, "Only MonoBehaviours", PREF_ONLY_MB, 130,
                    "Only inspect MonoBehaviour / ScriptableObject owners");

                GUILayout.Space(6);

                var searchStyle = GUI.skin.FindStyle("ToolbarSearchTextField") ?? EditorStyles.toolbarTextField;
                string next = GUILayout.TextField(_filterText, searchStyle, GUILayout.MinWidth(90));

                var cancelStyle = GUI.skin.FindStyle("ToolbarSearchCancelButton");
                if (cancelStyle != null && GUILayout.Button("", cancelStyle)) next = "";

                if (next != _filterText)
                {
                    _filterText = next;
                    _view.SetFilterText(_filterText);
                    _view.RebuildIfDirty(true);
                }

                _showHelp = GUILayout.Toggle(_showHelp, new GUIContent("?", "Filter syntax"), EditorStyles.toolbarButton, GUILayout.Width(20));

                bool onlyCode = GUILayout.Toggle(_onlyWithCode, new GUIContent("Only with Code", "Hide rows with no resolved .cs line"),
                    EditorStyles.toolbarButton, GUILayout.Width(100));
                if (onlyCode != _onlyWithCode)
                {
                    _onlyWithCode = onlyCode;
                    EditorPrefs.SetBool(PREF_ONLY_CODE, onlyCode);
                    _view.onlyWithCode = onlyCode;
                    _view.MarkDirty();
                    _view.RebuildIfDirty(true);
                }

                bool logHits = GUILayout.Toggle(_logHits, "Log Hits", EditorStyles.toolbarButton, GUILayout.Width(64));
                if (logHits != _logHits)
                {
                    _logHits = logHits;
                    EditorPrefs.SetBool(PREF_LOG_HITS, logHits);
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button(new GUIContent("Options ▾", "Advanced scan options and export"), EditorStyles.toolbarPopup, GUILayout.Width(72)))
                    ShowOptionsMenu();

                if (GUILayout.Button("Rescan", EditorStyles.toolbarButton, GUILayout.Width(56)))
                    RestartScan();

                if (GUILayout.Button("Copy Paths", EditorStyles.toolbarButton, GUILayout.Width(76)))
                    EditorGUIUtility.systemCopyBuffer = BuildTextReport();

                if (changed) RestartScan();
            }
        }

        private bool ToolbarToggle(ref bool value, string label, string prefKey, float width, string tooltip)
        {
            bool next = GUILayout.Toggle(value, new GUIContent(label, tooltip), EditorStyles.toolbarButton, GUILayout.Width(width));
            if (next == value) return false;

            value = next;
            EditorPrefs.SetBool(prefKey, next);
            return true;
        }

        private void ShowOptionsMenu()
        {
            var menu = new GenericMenu();

            AddScanOption(menu, "Scan/ScriptableObject assets (.asset)", _settings.scanAssetFiles, PREF_SCAN_ASSETS,
                v => _settings.scanAssetFiles = v);
            AddScanOption(menu, "Scan/Unopened scenes (.unity)", _settings.scanUnopenedScenes, PREF_SCAN_SCENES,
                v => _settings.scanUnopenedScenes = v);
            AddScanOption(menu, "Scan/Runtime C# refs (Play Mode)", _settings.scanRuntimeRefs, PREF_RUNTIME,
                v => _settings.scanRuntimeRefs = v);

            if (_settings.scanRuntimeRefs)
            {
                AddScanOption(menu, "Scan/Runtime C# refs — include statics", _settings.scanRuntimeStatics, PREF_RUNTIME_STATICS,
                    v => _settings.scanRuntimeStatics = v);
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Scan/Runtime C# refs — include statics"));
            }

            AddScanOption(menu, "Scan/Internal links (m_GameObject, m_Father...)", _settings.includeInternalLinks, PREF_INTERNAL,
                v => _settings.includeInternalLinks = v);
            AddScanOption(menu, "Scan/Turbo (bigger time slice)", _settings.turbo, PREF_TURBO,
                v => _settings.turbo = v);
            AddScanOption(menu, "Scan/Resolve AssetBundle names", _settings.resolveBundleNames, PREF_BUNDLES,
                v => _settings.resolveBundleNames = v);

            menu.AddSeparator("");

            menu.AddItem(new GUIContent("Group by scope / asset"), _grouped, () =>
            {
                _grouped = !_grouped;
                EditorPrefs.SetBool(PREF_GROUPED, _grouped);
                _view.grouped = _grouped;
                _view.MarkDirty();
                _view.RebuildIfDirty(true);
                Repaint();
            });

            menu.AddSeparator("");

            menu.AddItem(new GUIContent("Select All in Project/Hierarchy"), false, SelectAll);
            menu.AddItem(new GUIContent("Export/CSV..."), false, () => Export(false));
            menu.AddItem(new GUIContent("Export/JSON..."), false, () => Export(true));

            menu.ShowAsContext();
        }

        private void AddScanOption(GenericMenu menu, string label, bool value, string prefKey, Action<bool> setter)
        {
            menu.AddItem(new GUIContent(label), value, () =>
            {
                setter(!value);
                EditorPrefs.SetBool(prefKey, !value);
                RestartScan();
                Repaint();
            });
        }

        private void SelectAll()
        {
            var rows = _view.Filtered;
            var objects = new List<UnityEngine.Object>(rows.Count);

            for (int i = 0; i < rows.Count; i++)
            {
                var p = rows[i].ping;
                if (p != null && p) objects.Add(p);
            }

            Selection.objects = objects.ToArray();
            if (objects.Count > 0) EditorGUIUtility.PingObject(objects[0]);
        }

        private void OnActivated(RefResult r)
        {
            PingResult(r);
        }

        private void OnContextRequested(RefResult r)
        {
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent("Ping / Select"), false, () => PingResult(r));

            if (r.scope == RefScope.PrefabAsset && !string.IsNullOrEmpty(r.assetPath))
                menu.AddItem(new GUIContent("Open Prefab and Select Owner"), false, () => OpenPrefabAndSelect(r));
            else
                menu.AddDisabledItem(new GUIContent("Open Prefab and Select Owner"));

            if (r.scope == RefScope.SceneAsset && !string.IsNullOrEmpty(r.assetPath))
                menu.AddItem(new GUIContent("Open Scene"), false, () => OpenScene(r));
            else
                menu.AddDisabledItem(new GUIContent("Open Scene"));

            if (r.HasCode)
                menu.AddItem(new GUIContent("Open Code"), false, () => OpenCode(r));
            else
                menu.AddDisabledItem(new GUIContent("Open Code"));

            menu.AddSeparator("");

            menu.AddItem(new GUIContent("Copy Row"), false, () => EditorGUIUtility.systemCopyBuffer = FormatRow(r));
            menu.AddItem(new GUIContent("Copy Property Path"), false, () => EditorGUIUtility.systemCopyBuffer = r.propertyPath);

            if (!string.IsNullOrEmpty(r.assetPath))
                menu.AddItem(new GUIContent("Copy Asset Path"), false, () => EditorGUIUtility.systemCopyBuffer = r.assetPath);

            menu.ShowAsContext();
        }

        private void PingResult(RefResult r)
        {
            if (r.ping == null || !r.ping) return;

            Selection.activeObject = r.ping;
            EditorGUIUtility.PingObject(r.ping);
        }

        private static void OpenPrefabAndSelect(RefResult r)
        {
            var stage = PrefabStageUtility.OpenPrefab(r.assetPath);
            if (stage == null) return;

            var root = stage.prefabContentsRoot;
            if (root == null) return;

            var t = FindByPath(root.transform, r.ownerPath);
            if (t == null) return;

            Selection.activeGameObject = t.gameObject;
            EditorGUIUtility.PingObject(t.gameObject);
        }

        private static Transform FindByPath(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;

            int slash = path.IndexOf('/');
            if (slash < 0) return root;

            string rest = path.Substring(slash + 1);
            if (string.IsNullOrEmpty(rest)) return root;

            return root.Find(rest);
        }

        private static void OpenScene(RefResult r)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(r.assetPath, OpenSceneMode.Single);
        }

        private void OpenCode(RefResult r)
        {
            InternalEditorUtility.OpenFileAtLineExternal(r.scriptPath, r.line, 0);
            if (_logHits) Debug.Log("[FindRefs] open " + r.scriptPath + ":" + r.line);
        }

        private void Export(bool json)
        {
            string defaultName = json ? "find-refs.json" : "find-refs.csv";
            string ext = json ? "json" : "csv";

            string path = EditorUtility.SaveFilePanel("Export Find Refs", "", defaultName, ext);
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                File.WriteAllText(path, json ? BuildJson() : BuildCsv(), Encoding.UTF8);
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception e)
            {
                Debug.LogError("[FindRefs] export failed: " + e.Message);
            }
        }

        private string BuildCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("scope,confidence,ownerType,ownerName,ownerPath,property,kind,detail,assetPath,script,line,bundle");

            var rows = _view.Filtered;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                sb.Append(Csv(r.scope.ToString())).Append(',');
                sb.Append(Csv(r.confidence.ToString())).Append(',');
                sb.Append(Csv(r.ownerType)).Append(',');
                sb.Append(Csv(r.ownerName)).Append(',');
                sb.Append(Csv(r.ownerPath)).Append(',');
                sb.Append(Csv(r.propertyPath)).Append(',');
                sb.Append(Csv(r.refKind)).Append(',');
                sb.Append(Csv(r.detail)).Append(',');
                sb.Append(Csv(r.assetPath)).Append(',');
                sb.Append(Csv(r.scriptPath)).Append(',');
                sb.Append(r.line).Append(',');
                sb.Append(Csv(r.bundleName));
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.IndexOf(',') < 0 && value.IndexOf('"') < 0 && value.IndexOf('\n') < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private string BuildJson()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[");

            var rows = _view.Filtered;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                sb.Append("  {");
                sb.Append("\"scope\":").Append(Json(r.scope.ToString())).Append(',');
                sb.Append("\"confidence\":").Append(Json(r.confidence.ToString())).Append(',');
                sb.Append("\"ownerType\":").Append(Json(r.ownerType)).Append(',');
                sb.Append("\"ownerName\":").Append(Json(r.ownerName)).Append(',');
                sb.Append("\"ownerPath\":").Append(Json(r.ownerPath)).Append(',');
                sb.Append("\"property\":").Append(Json(r.propertyPath)).Append(',');
                sb.Append("\"kind\":").Append(Json(r.refKind)).Append(',');
                sb.Append("\"detail\":").Append(Json(r.detail)).Append(',');
                sb.Append("\"assetPath\":").Append(Json(r.assetPath)).Append(',');
                sb.Append("\"script\":").Append(Json(r.scriptPath)).Append(',');
                sb.Append("\"line\":").Append(r.line).Append(',');
                sb.Append("\"bundle\":").Append(Json(r.bundleName));
                sb.Append('}');
                if (i < rows.Count - 1) sb.Append(',');
                sb.AppendLine();
            }

            sb.AppendLine("]");
            return sb.ToString();
        }

        private static string Json(string value)
        {
            if (value == null) return "null";

            var sb = new StringBuilder("\"");
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }

        private string BuildTextReport()
        {
            var sb = new StringBuilder();
            var rows = _view.Filtered;

            for (int i = 0; i < rows.Count; i++)
                sb.AppendLine(FormatRow(rows[i]));

            return sb.ToString();
        }

        private static string FormatRow(RefResult r)
        {
            var sb = new StringBuilder();
            sb.Append(r.scope).Append("  |  ").Append(r.ownerType).Append(" on '").Append(r.ownerName).Append('\'');

            if (!string.IsNullOrEmpty(r.assetPath)) sb.Append("  |  ").Append(r.assetPath);
            sb.Append("  |  ").Append(r.propertyPath).Append(" (").Append(r.refKind).Append(')');
            if (!string.IsNullOrEmpty(r.detail)) sb.Append(" -> ").Append(r.detail);
            if (r.HasCode) sb.Append("  |  ").Append(r.scriptPath).Append(':').Append(r.line);

            return sb.ToString();
        }
    }
}
#endif
