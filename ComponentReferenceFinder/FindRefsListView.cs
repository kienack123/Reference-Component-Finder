#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace RefFinder
{
    internal sealed class FindRefsListView
    {
        private const float CARD_HEIGHT = 88f;
        private const float GROUP_HEIGHT = 22f;
        private const float LINE = 18f;
        private const float SCROLLBAR_WIDTH = 16f;
        private const double REBUILD_THROTTLE = 0.25;

        private struct Row
        {
            public string group;
            public RefResult result;
        }

        private List<RefResult> _source = new List<RefResult>();
        private readonly List<RefResult> _filtered = new List<RefResult>();
        private readonly List<Row> _rows = new List<Row>();
        private readonly RefFilter _filter = new RefFilter();

        private float[] _offsets = new float[1];
        private Vector2 _scroll;
        private bool _dirty = true;
        private double _lastRebuild;
        private RefResult _selected;

        public bool onlyWithCode;
        public bool grouped;

        public event Action<RefResult> Activated;
        public event Action<RefResult> OpenCodeRequested;
        public event Action<RefResult> CopyRequested;
        public event Action<RefResult> ContextRequested;
        public event Action RepaintRequested;

        public int VisibleCount { get { return _filtered.Count; } }
        public IList<RefResult> Filtered { get { return _filtered; } }

        public void SetResults(List<RefResult> results)
        {
            _source = results ?? new List<RefResult>();
            _selected = null;
            _dirty = true;
        }

        public void SetFilterText(string text)
        {
            _filter.SetText(text);
            _dirty = true;
        }

        public void MarkDirty()
        {
            _dirty = true;
        }

        public void RebuildIfDirty(bool force)
        {
            if (!_dirty) return;
            if (!force && EditorApplication.timeSinceStartup - _lastRebuild < REBUILD_THROTTLE) return;

            _dirty = false;
            _lastRebuild = EditorApplication.timeSinceStartup;
            Rebuild();
        }

        private void Rebuild()
        {
            _filtered.Clear();

            for (int i = 0; i < _source.Count; i++)
            {
                var r = _source[i];
                if (r == null) continue;
                if (onlyWithCode && !r.HasCode) continue;
                if (!_filter.Pass(r)) continue;

                _filtered.Add(r);
            }

            _rows.Clear();
            string lastGroup = null;

            for (int i = 0; i < _filtered.Count; i++)
            {
                var r = _filtered[i];

                if (grouped)
                {
                    string key = GroupKey(r);
                    if (!string.Equals(key, lastGroup, StringComparison.Ordinal))
                    {
                        _rows.Add(new Row { group = key });
                        lastGroup = key;
                    }
                }

                _rows.Add(new Row { result = r });
            }

            if (_offsets.Length < _rows.Count + 1) _offsets = new float[_rows.Count + 1];

            float y = 0f;
            for (int i = 0; i < _rows.Count; i++)
            {
                _offsets[i] = y;
                y += _rows[i].result == null ? GROUP_HEIGHT : CARD_HEIGHT;
            }

            _offsets[_rows.Count] = y;
        }

        private static string GroupKey(RefResult r)
        {
            if (!string.IsNullOrEmpty(r.assetPath)) return r.scope + "   —   " + r.assetPath;
            return r.scope.ToString();
        }

        private int FindFirstVisible(float top)
        {
            int lo = 0;
            int hi = _rows.Count - 1;
            int result = 0;

            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_offsets[mid] <= top)
                {
                    result = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return result;
        }

        public void Draw(Rect rect)
        {
            float contentHeight = _rows.Count > 0 ? _offsets[_rows.Count] : 0f;
            var viewRect = new Rect(0f, 0f, rect.width - SCROLLBAR_WIDTH, contentHeight);

            _scroll = GUI.BeginScrollView(rect, _scroll, viewRect);

            if (_rows.Count == 0)
            {
                GUI.Label(new Rect(0f, 6f, rect.width, LINE), "(no results)", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                int first = FindFirstVisible(_scroll.y);

                for (int i = first; i < _rows.Count; i++)
                {
                    if (_offsets[i] > _scroll.y + rect.height) break;

                    float height = _offsets[i + 1] - _offsets[i];
                    var rowRect = new Rect(2f, _offsets[i], viewRect.width - 4f, height);

                    if (_rows[i].result == null) DrawGroup(rowRect, _rows[i].group);
                    else DrawCard(rowRect, _rows[i].result);
                }
            }

            GUI.EndScrollView();
        }

        private static void DrawGroup(Rect rect, string label)
        {
            var labelRect = new Rect(rect.x + 2f, rect.y + 3f, rect.width - 4f, LINE);
            GUI.Label(labelRect, label, EditorStyles.boldLabel);
        }

        private void DrawCard(Rect rect, RefResult r)
        {
            var box = new Rect(rect.x, rect.y + 2f, rect.width, rect.height - 4f);
            GUI.Box(box, GUIContent.none, EditorStyles.helpBox);

            if (ReferenceEquals(_selected, r))
                EditorGUI.DrawRect(new Rect(box.x, box.y, 3f, box.height), SelectionColor());

            HandleCardEvents(box, r);

            var line = new Rect(box.x + 6f, box.y + 4f, box.width - 12f, LINE);
            GUI.Label(line, HeaderText(r), EditorStyles.boldLabel);

            line.y += LINE;
            GUI.Label(line, PropertyText(r));

            line.y += LINE;
            GUI.Label(line, CodeText(r));

            line.y += LINE + 2f;

            var button = new Rect(line.x, line.y, 90f, LINE);
            if (GUI.Button(button, "Ping/Select") && Activated != null) Activated(r);

            button.x += 94f;
            using (new EditorGUI.DisabledScope(!r.HasCode))
            {
                if (GUI.Button(button, "Open Code") && OpenCodeRequested != null) OpenCodeRequested(r);
            }

            button.x += 94f;
            if (GUI.Button(button, "Copy") && CopyRequested != null) CopyRequested(r);
        }

        private static string HeaderText(RefResult r)
        {
            string text = r.scope.ToString();
            if (r.confidence == RefConfidence.Candidate) text += " (?)";

            text += "  |  " + r.ownerType + " on '" + r.ownerName + "'";

            if (!string.IsNullOrEmpty(r.assetPath)) text += "   —   " + r.assetPath;

            return text;
        }

        private static string PropertyText(RefResult r)
        {
            string text = "propertyPath: " + r.propertyLabel + "   (ref " + r.refKind + ")";
            if (!string.IsNullOrEmpty(r.detail)) text += "  ->  " + r.detail;
            return text;
        }

        private static string CodeText(RefResult r)
        {
            string text = r.HasCode ? "code: " + r.scriptPath + ":" + r.line : "code: (not found)";
            if (!string.IsNullOrEmpty(r.bundleName)) text += "   |   bundle: " + r.bundleName;
            return text;
        }

        private void HandleCardEvents(Rect rect, RefResult r)
        {
            var e = Event.current;
            if (e.type != EventType.MouseDown) return;
            if (!rect.Contains(e.mousePosition)) return;

            _selected = r;

            if (e.button == 1)
            {
                if (ContextRequested != null) ContextRequested(r);
                e.Use();
                return;
            }

            if (e.button == 0 && e.clickCount == 2 && Activated != null)
            {
                Activated(r);
                e.Use();
            }

            if (RepaintRequested != null) RepaintRequested();
        }

        private static Color SelectionColor()
        {
            return EditorGUIUtility.isProSkin ? new Color(0.35f, 0.65f, 1f) : new Color(0.16f, 0.40f, 0.85f);
        }
    }
}
#endif
