#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace RefFinder
{
    internal static class SerializedRefWalker
    {
        private const int MAX_DEPTH = 24;
        private const string EVENT_MARK = ".m_PersistentCalls.m_Calls.Array.data[";

        private static readonly HashSet<string> InternalRootPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_GameObject",
            "m_ObjectHideFlags",
            "m_CorrespondingSourceObject",
            "m_PrefabInstance",
            "m_PrefabAsset",
            "m_EditorClassIdentifier",
            "m_EditorHideFlags",
            "m_Father",
            "m_Children",
            "m_Name",
        };

        private static readonly HashSet<string> PrimitiveArrayElements = new HashSet<string>(StringComparer.Ordinal)
        {
            "int", "float", "double", "bool", "char", "string",
            "SInt8", "UInt8", "SInt16", "UInt16", "SInt32", "UInt32", "SInt64", "UInt64",
            "Vector2f", "Vector3f", "Vector4f", "Quaternionf",
            "ColorRGBA", "Matrix4x4f", "Rectf", "AABB", "BoneWeight",
        };

        internal struct Hit
        {
            public string propertyPath;
            public UnityEngine.Object value;
            public bool missing;
        }

        public static void Collect(SerializedObject so, bool includeInternalLinks, bool includeMissing, List<Hit> outHits)
        {
            if (so == null || outHits == null) return;

            SerializedProperty it;
            try
            {
                it = so.GetIterator();
            }
            catch
            {
                return;
            }

            bool enter = true;

            while (true)
            {
                bool moved;
                try
                {
                    moved = it.Next(enter);
                }
                catch
                {
                    return;
                }

                if (!moved) return;

                enter = true;

                if (it.depth > MAX_DEPTH)
                {
                    enter = false;
                    continue;
                }

                if (it.depth == 0)
                {
                    string root = it.propertyPath;
                    if (root == "m_Script" || (!includeInternalLinks && InternalRootPaths.Contains(root)))
                    {
                        enter = false;
                        continue;
                    }
                }

                if (it.propertyType == SerializedPropertyType.ObjectReference)
                {
                    enter = false;

                    var v = it.objectReferenceValue;
                    if (v != null)
                    {
                        outHits.Add(new Hit { propertyPath = it.propertyPath, value = v, missing = false });
                    }
                    else if (includeMissing && IsUnresolvedReference(it))
                    {
                        outHits.Add(new Hit { propertyPath = it.propertyPath, value = null, missing = true });
                    }

                    continue;
                }

                if (it.isArray && it.propertyType != SerializedPropertyType.String)
                {
                    string elem = null;
                    try
                    {
                        elem = it.arrayElementType;
                    }
                    catch
                    {
                    }

                    if (!string.IsNullOrEmpty(elem) && PrimitiveArrayElements.Contains(elem))
                    {
                        enter = false;
                        continue;
                    }
                }

                switch (it.propertyType)
                {
                    case SerializedPropertyType.Generic:
                    case SerializedPropertyType.ManagedReference:
                    case SerializedPropertyType.ExposedReference:
                        break;
                    default:
                        enter = false;
                        break;
                }
            }
        }

        private static bool _probeInitialized;
        private static PropertyInfo _instanceIdProperty;
        private static PropertyInfo _entityIdProperty;

        private static bool IsUnresolvedReference(SerializedProperty property)
        {
            if (!_probeInitialized)
            {
                _probeInitialized = true;

                var type = typeof(SerializedProperty);
                _instanceIdProperty = type.GetProperty("objectReferenceInstanceIDValue", BindingFlags.Public | BindingFlags.Instance);
                _entityIdProperty = type.GetProperty("objectReferenceEntityIdValue", BindingFlags.Public | BindingFlags.Instance);
            }

            try
            {
                if (_instanceIdProperty != null)
                {
                    object raw = _instanceIdProperty.GetValue(property, null);
                    if (raw is int) return (int)raw != 0;
                }

                if (_entityIdProperty != null)
                {
                    object raw = _entityIdProperty.GetValue(property, null);
                    if (raw == null) return false;

                    object empty = Activator.CreateInstance(_entityIdProperty.PropertyType);
                    return !raw.Equals(empty);
                }
            }
            catch
            {
            }

            return false;
        }

        public static bool TryDescribeUnityEvent(SerializedObject so, string propertyPath, out string eventName, out string methodName)
        {
            eventName = null;
            methodName = null;

            if (string.IsNullOrEmpty(propertyPath)) return false;

            int idx = propertyPath.IndexOf(EVENT_MARK, StringComparison.Ordinal);
            if (idx <= 0) return false;

            eventName = propertyPath.Substring(0, idx);

            int close = propertyPath.IndexOf(']', idx);
            if (close < 0) return true;

            string callPath = propertyPath.Substring(0, close + 1);

            try
            {
                var mp = so.FindProperty(callPath + ".m_MethodName");
                if (mp != null) methodName = mp.stringValue;
            }
            catch
            {
            }

            return true;
        }

        public static string RootFieldName(string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath)) return null;

            int dot = propertyPath.IndexOf('.');
            string root = dot > 0 ? propertyPath.Substring(0, dot) : propertyPath;

            int bracket = root.IndexOf('[');
            if (bracket > 0) root = root.Substring(0, bracket);

            return root;
        }
    }
}
#endif
