#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RefFinder
{
    internal sealed class RuntimeHeapScanner
    {
        private const int MAX_DEPTH = 8;
        private const int MAX_VISITED = 400000;
        private const int MAX_COLLECTION_ITEMS = 20000;

        private sealed class StaticRoot
        {
            public Type type;
        }

        private struct Node
        {
            public object container;
            public UnityEngine.Object owner;
            public string path;
            public int depth;
            public FieldInfo field;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            bool IEqualityComparer<object>.Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            int IEqualityComparer<object>.GetHashCode(object obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }

        private static readonly string OwnNamespace = typeof(RuntimeHeapScanner).Namespace;

        private static readonly Dictionary<Type, FieldInfo[]> _fieldCache = new Dictionary<Type, FieldInfo[]>(512);
        private static readonly Dictionary<Type, FieldInfo[]> _staticFieldCache = new Dictionary<Type, FieldInfo[]>(512);
        private static readonly Dictionary<Type, bool> _reachCache = new Dictionary<Type, bool>(2048);
        private static readonly HashSet<Type> _reachInProgress = new HashSet<Type>();

        private readonly ScanTarget _target;
        private readonly List<RefResult> _results;
        private readonly bool _includeStatics;
        private readonly ScriptCodeLocator _code;

        private readonly Queue<Node> _queue = new Queue<Node>();
        private readonly HashSet<object> _visited = new HashSet<object>(ReferenceComparer.Instance);

        private List<Node> _roots;
        private int _rootIndex;
        private bool _rootsBuilt;

        public string Note { get; private set; }
        public int Hits { get; private set; }

        public string ProgressText
        {
            get
            {
                if (!_rootsBuilt) return "collecting roots";
                return _rootIndex.ToString("N0") + "/" + (_roots != null ? _roots.Count : 0).ToString("N0") + " roots, queue " + _queue.Count.ToString("N0");
            }
        }

        public float Progress
        {
            get
            {
                if (!_rootsBuilt || _roots == null) return 0f;
                if (_roots.Count == 0) return 1f;
                return Mathf.Clamp01((float)_rootIndex / _roots.Count);
            }
        }

        public RuntimeHeapScanner(ScanTarget target, List<RefResult> results, bool includeStatics, ScriptCodeLocator code)
        {
            _target = target;
            _results = results;
            _includeStatics = includeStatics;
            _code = code;
        }

        public bool StepOnce()
        {
            if (!_rootsBuilt)
            {
                BuildRoots();
                _rootsBuilt = true;
                return true;
            }

            if (_queue.Count > 0)
            {
                var node = _queue.Dequeue();
                Expand(node.container, node.owner, node.path, node.depth, node.field);
                return true;
            }

            if (_roots != null && _rootIndex < _roots.Count)
            {
                var root = _roots[_rootIndex++];
                Expand(root.container, root.owner, root.path, root.depth, root.field);
                return true;
            }

            return false;
        }

        private void BuildRoots()
        {
            _roots = new List<Node>(4096);

            var scenes = new List<Scene>(SceneManager.sceneCount);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.IsValid() && s.isLoaded) scenes.Add(s);
            }

            var buffer = new List<Component>(64);

            for (int i = 0; i < scenes.Count; i++)
            {
                var roots = scenes[i].GetRootGameObjects();
                for (int r = 0; r < roots.Length; r++)
                {
                    AddComponentRoots(roots[r], buffer);
                }
            }

            var ddol = FindDontDestroyOnLoadRoots();
            for (int i = 0; i < ddol.Count; i++) AddComponentRoots(ddol[i], buffer);

            var scriptableObjects = Resources.FindObjectsOfTypeAll<ScriptableObject>();
            for (int i = 0; i < scriptableObjects.Length; i++)
            {
                var so = scriptableObjects[i];
                if (so == null || !so) continue;
                if (!MonoScriptIndex.IsProjectType(so.GetType())) continue;
                if (IsOwnType(so.GetType())) continue;
                if (GetInstanceFields(so.GetType()).Length == 0) continue;

                _roots.Add(new Node
                {
                    container = so,
                    owner = so,
                    path = so.GetType().Name,
                    depth = 0,
                });
            }

            if (_includeStatics) AddStaticRoots();
        }

        private void AddComponentRoots(GameObject root, List<Component> buffer)
        {
            if (root == null) return;

            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                var t = transforms[i];
                if (t == null) continue;

                buffer.Clear();
                t.gameObject.GetComponents(buffer);

                for (int c = 0; c < buffer.Count; c++)
                {
                    var comp = buffer[c];
                    if (comp == null) continue;
                    if (GetInstanceFields(comp.GetType()).Length == 0) continue;

                    _roots.Add(new Node
                    {
                        container = comp,
                        owner = comp,
                        path = comp.GetType().Name,
                        depth = 0,
                    });
                }
            }
        }

        private static List<GameObject> FindDontDestroyOnLoadRoots()
        {
            var list = new List<GameObject>();
            if (!Application.isPlaying) return list;

            var probe = new GameObject("~ddol_probe~");
            probe.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                UnityEngine.Object.DontDestroyOnLoad(probe);
                var scene = probe.scene;

                if (scene.IsValid() && scene.isLoaded)
                {
                    var roots = scene.GetRootGameObjects();
                    for (int i = 0; i < roots.Length; i++)
                    {
                        if (roots[i] != probe) list.Add(roots[i]);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probe);
            }

            return list;
        }

        private void AddStaticRoots()
        {
            Assembly[] assemblies;
            try
            {
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch
            {
                return;
            }

            for (int a = 0; a < assemblies.Length; a++)
            {
                if (!IsProjectAssembly(assemblies[a])) continue;

                Type[] types;
                try
                {
                    types = assemblies[a].GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types;
                }
                catch
                {
                    continue;
                }

                if (types == null) continue;

                for (int t = 0; t < types.Length; t++)
                {
                    var type = types[t];
                    if (type == null) continue;
                    if (type.IsGenericTypeDefinition || type.ContainsGenericParameters) continue;
                    if (type.IsInterface || type.IsEnum) continue;
                    if (IsOwnType(type)) continue;
                    if (GetStaticFields(type).Length == 0) continue;

                    _roots.Add(new Node
                    {
                        container = new StaticRoot { type = type },
                        owner = null,
                        path = type.Name,
                        depth = 0,
                    });
                }
            }
        }

        private static bool IsOwnType(Type type)
        {
            return type != null && type.Namespace == OwnNamespace;
        }

        private static bool IsProjectAssembly(Assembly assembly)
        {
            string name;
            try
            {
                name = assembly.GetName().Name;
            }
            catch
            {
                return false;
            }

            if (name.StartsWith("System", StringComparison.Ordinal)) return false;
            if (name.StartsWith("Unity.", StringComparison.Ordinal)) return false;
            if (name.StartsWith("UnityEngine", StringComparison.Ordinal)) return false;
            if (name.StartsWith("UnityEditor", StringComparison.Ordinal)) return false;
            if (name.StartsWith("Microsoft", StringComparison.Ordinal)) return false;
            if (name.StartsWith("Mono.", StringComparison.Ordinal)) return false;
            if (name == "mscorlib" || name == "netstandard") return false;

            return true;
        }

        private void Expand(object container, UnityEngine.Object owner, string path, int depth, FieldInfo field)
        {
            if (container == null) return;

            var staticRoot = container as StaticRoot;
            if (staticRoot != null)
            {
                ExpandStatics(staticRoot.type, path, depth);
                return;
            }

            var del = container as Delegate;
            if (del != null)
            {
                ExpandDelegate(del, owner, path, depth, field);
                return;
            }

            var unityObject = container as UnityEngine.Object;

            if (unityObject == null)
            {
                var array = container as Array;
                if (array != null)
                {
                    ExpandArray(array, owner, path, depth, field);
                    return;
                }

                var enumerable = container as IEnumerable;
                if (enumerable != null && !(container is string))
                {
                    ExpandEnumerable(enumerable, owner, path, depth, field);
                    return;
                }
            }

            var fields = GetInstanceFields(container.GetType());
            for (int i = 0; i < fields.Length; i++)
            {
                object value;
                try
                {
                    value = fields[i].GetValue(container);
                }
                catch
                {
                    continue;
                }

                Child(value, owner, path + "." + fields[i].Name, depth + 1, fields[i]);
            }
        }

        private void ExpandStatics(Type type, string path, int depth)
        {
            var fields = GetStaticFields(type);

            for (int i = 0; i < fields.Length; i++)
            {
                object value;
                try
                {
                    value = fields[i].GetValue(null);
                }
                catch
                {
                    continue;
                }

                Child(value, null, path + "." + fields[i].Name, depth + 1, fields[i]);
            }
        }

        private void ExpandDelegate(Delegate del, UnityEngine.Object owner, string path, int depth, FieldInfo field)
        {
            Delegate[] list;
            try
            {
                list = del.GetInvocationList();
            }
            catch
            {
                return;
            }

            for (int i = 0; i < list.Length; i++)
            {
                var d = list[i];
                if (d == null) continue;

                string label = path + " -> " + (d.Method != null ? d.Method.Name : "?") + "()";

                var targetObject = d.Target;
                if (targetObject == null) continue;

                Child(targetObject, owner, label, depth + 1, field);
            }
        }

        private void ExpandArray(Array array, UnityEngine.Object owner, string path, int depth, FieldInfo field)
        {
            if (array.Rank != 1)
            {
                try
                {
                    int index = 0;
                    foreach (var item in array)
                    {
                        if (index >= MAX_COLLECTION_ITEMS) break;
                        Child(item, owner, path + "[" + index + "]", depth + 1, field);
                        index++;
                    }
                }
                catch
                {
                }

                return;
            }

            int count = Math.Min(array.Length, MAX_COLLECTION_ITEMS);
            for (int i = 0; i < count; i++)
            {
                object item;
                try
                {
                    item = array.GetValue(i);
                }
                catch
                {
                    continue;
                }

                Child(item, owner, path + "[" + i + "]", depth + 1, field);
            }
        }

        private void ExpandEnumerable(IEnumerable enumerable, UnityEngine.Object owner, string path, int depth, FieldInfo field)
        {
            try
            {
                int index = 0;
                foreach (var item in enumerable)
                {
                    if (index >= MAX_COLLECTION_ITEMS) break;
                    Child(item, owner, path + "[" + index + "]", depth + 1, field);
                    index++;
                }
            }
            catch
            {
            }
        }

        private void Child(object value, UnityEngine.Object owner, string path, int depth, FieldInfo field)
        {
            if (value == null) return;

            var unityObject = value as UnityEngine.Object;
            if (unityObject != null)
            {
                if (!unityObject) return;

                string kind;
                if (_target.TryMatch(unityObject, out kind)) Record(owner, path, kind, field);

                return;
            }

            if (depth >= MAX_DEPTH) return;
            if (value is Type) return;

            var type = value.GetType();
            if (IsLeafType(type)) return;

            if (_visited.Count >= MAX_VISITED)
            {
                Note = "Runtime scan stopped at " + MAX_VISITED + " objects.";
                return;
            }

            if (!type.IsValueType && !_visited.Add(value)) return;

            _queue.Enqueue(new Node
            {
                container = value,
                owner = owner,
                path = path,
                depth = depth,
                field = field,
            });
        }

        private void Record(UnityEngine.Object owner, string path, string kind, FieldInfo field)
        {
            var result = new RefResult
            {
                scope = RefScope.Runtime,
                confidence = RefConfidence.Confirmed,
                ping = owner,
                propertyPath = path,
                propertyLabel = path,
                refKind = kind,
                detail = "runtime C# field",
            };

            if (_code != null)
            {
                string scriptPath;
                int line;
                _code.LocateField(owner, field, out scriptPath, out line);
                result.scriptPath = scriptPath;
                result.line = line;
            }

            if (owner != null && owner)
            {
                result.ownerType = owner.GetType().Name;

                var comp = owner as Component;
                if (comp != null)
                {
                    try
                    {
                        var go = comp.gameObject;
                        result.ownerName = go ? go.name : "(no GameObject)";
                        result.ownerPath = BuildTransformPath(comp.transform);
                    }
                    catch (MissingReferenceException)
                    {
                        result.ownerName = "(destroyed)";
                    }
                }
                else
                {
                    result.ownerName = owner.name;
                }
            }
            else
            {
                int dot = path.IndexOf('.');
                result.ownerType = dot > 0 ? path.Substring(0, dot) : path;
                result.ownerName = "(static)";
                result.detail = "static C# field";
            }

            _results.Add(result);
            Hits++;
        }

        private static string BuildTransformPath(Transform t)
        {
            if (t == null) return "";

            var sb = new System.Text.StringBuilder(t.name);
            var parent = t.parent;

            int guard = 0;
            while (parent != null && guard++ < 64)
            {
                sb.Insert(0, "/");
                sb.Insert(0, parent.name);
                parent = parent.parent;
            }

            return sb.ToString();
        }

        private static bool CanReachUnityObject(Type type)
        {
            if (type == null) return false;

            bool cached;
            if (_reachCache.TryGetValue(type, out cached)) return cached;

            if (_reachInProgress.Contains(type)) return true;

            _reachInProgress.Add(type);
            bool result;

            try
            {
                result = ComputeReach(type);
            }
            catch
            {
                result = true;
            }
            finally
            {
                _reachInProgress.Remove(type);
            }

            _reachCache[type] = result;
            return result;
        }

        private static bool ComputeReach(Type type)
        {
            if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return true;
            if (type.IsPrimitive || type.IsEnum || type.IsPointer) return false;
            if (IsLeafType(type)) return false;

            if (type == typeof(object)) return true;
            if (type.IsInterface) return true;
            if (typeof(Delegate).IsAssignableFrom(type)) return true;
            if (type.IsGenericParameter || type.ContainsGenericParameters) return true;
            if (type.IsAbstract && !type.IsSealed) return true;
            if (type.IsArray) return CanReachUnityObject(type.GetElementType());

            FieldInfo[] fields;
            try
            {
                fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch
            {
                return true;
            }

            for (int i = 0; i < fields.Length; i++)
            {
                if (CanReachUnityObject(fields[i].FieldType)) return true;
            }

            return false;
        }

        private static FieldInfo[] GetInstanceFields(Type type)
        {
            FieldInfo[] cached;
            if (_fieldCache.TryGetValue(type, out cached)) return cached;

            var list = new List<FieldInfo>(16);
            var current = type;

            int guard = 0;
            while (current != null && guard++ < 32 && !IsStopType(current))
            {
                FieldInfo[] declared;
                try
                {
                    declared = current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch
                {
                    break;
                }

                for (int i = 0; i < declared.Length; i++)
                {
                    if (declared[i].IsStatic) continue;
                    if (!CanReachUnityObject(declared[i].FieldType)) continue;
                    list.Add(declared[i]);
                }

                current = current.BaseType;
            }

            cached = list.ToArray();
            _fieldCache[type] = cached;
            return cached;
        }

        private static FieldInfo[] GetStaticFields(Type type)
        {
            FieldInfo[] cached;
            if (_staticFieldCache.TryGetValue(type, out cached)) return cached;

            var list = new List<FieldInfo>(4);

            FieldInfo[] declared;
            try
            {
                declared = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch
            {
                declared = Array.Empty<FieldInfo>();
            }

            for (int i = 0; i < declared.Length; i++)
            {
                if (declared[i].IsLiteral) continue;
                if (!CanReachUnityObject(declared[i].FieldType)) continue;
                list.Add(declared[i]);
            }

            cached = list.ToArray();
            _staticFieldCache[type] = cached;
            return cached;
        }

        private static bool IsStopType(Type type)
        {
            return type == typeof(object)
                || type == typeof(UnityEngine.Object)
                || type == typeof(Component)
                || type == typeof(Behaviour)
                || type == typeof(MonoBehaviour)
                || type == typeof(ScriptableObject)
                || type == typeof(ValueType);
        }

        private static bool IsLeafType(Type type)
        {
            if (type.IsPrimitive || type.IsEnum || type.IsPointer) return true;

            if (type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime)
                || type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(IntPtr))
                return true;

            if (type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4)
                || type == typeof(Vector2Int) || type == typeof(Vector3Int)
                || type == typeof(Quaternion) || type == typeof(Color) || type == typeof(Color32)
                || type == typeof(Matrix4x4) || type == typeof(Rect) || type == typeof(Bounds))
                return true;

            string ns = type.Namespace;
            if (ns != null)
            {
                if (ns.StartsWith("System.Reflection", StringComparison.Ordinal)) return true;
                if (ns.StartsWith("System.Threading", StringComparison.Ordinal)) return true;
                if (ns.StartsWith("System.IO", StringComparison.Ordinal)) return true;
            }

            return false;
        }

        public static void ClearTypeCache()
        {
            _fieldCache.Clear();
        }
    }
}
#endif
