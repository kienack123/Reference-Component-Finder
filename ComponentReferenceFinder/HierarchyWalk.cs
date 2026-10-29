#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RefFinder
{
    internal sealed class HierarchyWalk
    {
        private struct Source
        {
            public Scene scene;
            public RefScope scope;
        }

        private readonly List<Source> _sources = new List<Source>(4);

        private readonly Queue<Transform> _inward = new Queue<Transform>();
        private readonly Queue<Transform> _outward = new Queue<Transform>();
        private readonly Queue<Transform> _sweep = new Queue<Transform>();

        private Transform _climb;
        private Transform _seedRoot;
        private RefScope _seedScope = RefScope.Scene;
        private int _lane;

        private GameObject[] _roots;
        private int _rootIndex;
        private int _sourceIndex;
        private RefScope _sweepScope = RefScope.Scene;

        private int _yielded;
        private int _total = -1;

        public int Yielded { get { return _yielded; } }

        public bool NearTargetActive
        {
            get { return _climb != null || _inward.Count > 0 || _outward.Count > 0; }
        }

        public float Progress
        {
            get
            {
                if (_total < 0) return 0f;
                if (_total == 0) return 1f;
                return Mathf.Clamp01((float)_yielded / _total);
            }
        }

        public string ProgressText
        {
            get
            {
                if (_sources.Count == 0) return "nothing to walk";

                string text = _yielded.ToString("N0");
                if (_total > 0) text += "/" + _total.ToString("N0");
                text += " objects";

                return NearTargetActive ? text + ", spreading out from the target" : text;
            }
        }

        public void SetSeed(Transform seed, RefScope scope)
        {
            if (seed == null) return;

            _seedScope = scope;
            _climb = seed;
            _inward.Enqueue(seed);

            var root = seed;
            int guard = 0;
            while (root.parent != null && guard++ < 128) root = root.parent;
            _seedRoot = root;
        }

        public void AddSource(Scene scene, RefScope scope)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;

            for (int i = 0; i < _sources.Count; i++)
            {
                if (_sources[i].scene == scene) return;
            }

            _sources.Add(new Source { scene = scene, scope = scope });
        }

        public bool StepOnce(out GameObject go, out RefScope scope)
        {
            go = null;
            scope = _seedScope;

            if (_total < 0) _total = CountTotal();

            for (int i = 0; i < 3; i++)
            {
                int lane = _lane;
                _lane = _lane == 2 ? 0 : _lane + 1;

                if (lane == 0)
                {
                    if (TryDrain(_inward, out go))
                    {
                        scope = _seedScope;
                        return true;
                    }
                }
                else if (lane == 1)
                {
                    if (TryStepOutward(out go))
                    {
                        scope = _seedScope;
                        return true;
                    }
                }
                else if (TryStepSweep(out go, out scope))
                {
                    return true;
                }
            }

            return false;
        }

        private int CountTotal()
        {
            int total = 0;
            var buffer = new List<Transform>(2048);

            for (int i = 0; i < _sources.Count; i++)
            {
                GameObject[] roots;
                try
                {
                    roots = _sources[i].scene.GetRootGameObjects();
                }
                catch
                {
                    continue;
                }

                for (int r = 0; r < roots.Length; r++)
                {
                    if (roots[r] == null) continue;

                    buffer.Clear();
                    roots[r].GetComponentsInChildren(true, buffer);
                    total += buffer.Count;
                }
            }

            return total;
        }

        private bool TryDrain(Queue<Transform> queue, out GameObject go)
        {
            go = null;

            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                if (t == null) continue;

                EnqueueChildren(t, queue);
                go = t.gameObject;
                _yielded++;
                return true;
            }

            return false;
        }

        private bool TryStepOutward(out GameObject go)
        {
            go = null;

            if (TryDrain(_outward, out go)) return true;
            if (_climb == null) return false;

            Transform parent = null;
            try
            {
                parent = _climb.parent;
            }
            catch
            {
            }

            if (parent == null)
            {
                _climb = null;
                return false;
            }

            int count = 0;
            try
            {
                count = parent.childCount;
            }
            catch
            {
            }

            for (int i = 0; i < count; i++)
            {
                Transform child = null;
                try
                {
                    child = parent.GetChild(i);
                }
                catch
                {
                }

                if (child == null) continue;
                if (ReferenceEquals(child, _climb)) continue;

                _outward.Enqueue(child);
            }

            _climb = parent;

            go = parent.gameObject;
            _yielded++;
            return true;
        }

        private bool TryStepSweep(out GameObject go, out RefScope scope)
        {
            go = null;
            scope = _sweepScope;

            while (true)
            {
                if (TryDrain(_sweep, out go))
                {
                    scope = _sweepScope;
                    return true;
                }

                if (_roots != null && _rootIndex < _roots.Length)
                {
                    var root = _roots[_rootIndex++];
                    if (root != null && !ReferenceEquals(root.transform, _seedRoot))
                        _sweep.Enqueue(root.transform);

                    continue;
                }

                _roots = null;
                if (_sourceIndex >= _sources.Count) return false;

                var source = _sources[_sourceIndex++];
                _sweepScope = source.scope;
                _rootIndex = 0;

                try
                {
                    _roots = source.scene.GetRootGameObjects();
                }
                catch
                {
                    _roots = null;
                }
            }
        }

        private static void EnqueueChildren(Transform t, Queue<Transform> queue)
        {
            int count = 0;
            try
            {
                count = t.childCount;
            }
            catch
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                Transform child = null;
                try
                {
                    child = t.GetChild(i);
                }
                catch
                {
                }

                if (child != null) queue.Enqueue(child);
            }
        }
    }
}
#endif
