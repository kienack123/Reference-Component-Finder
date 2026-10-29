#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace RefFinder
{
    public static class ComponentReferenceFinder
    {
        [MenuItem("CONTEXT/Component/Find Refs")]
        private static void FindComponent(MenuCommand command)
        {
            var c = command.context as Component;
            if (c == null) return;

            FindRefsWindow.Open(ScanTarget.FromComponent(c));
        }

        [MenuItem("CONTEXT/Component/Find Refs", true)]
        private static bool FindComponentValidate(MenuCommand command)
        {
            return command.context is Component;
        }

        [MenuItem("GameObject/Find Refs", false, 30)]
        private static void FindGameObject(MenuCommand command)
        {
            var go = command.context as GameObject;
            if (go == null) return;
            if (Selection.activeGameObject != null && Selection.activeGameObject != go) return;

            FindRefsWindow.Open(ScanTarget.FromGameObject(go));
        }

        [MenuItem("GameObject/Find Refs", true, 30)]
        private static bool FindGameObjectValidate(MenuCommand command)
        {
            return command.context is GameObject;
        }

        [MenuItem("Assets/Find Refs", false, 39)]
        private static void FindAsset()
        {
            var obj = Selection.activeObject;
            if (obj == null) return;

            FindRefsWindow.Open(ScanTarget.FromAsset(obj));
        }

        [MenuItem("Assets/Find Refs", true, 39)]
        private static bool FindAssetValidate()
        {
            return Selection.activeObject != null && EditorUtility.IsPersistent(Selection.activeObject);
        }

        [MenuItem("Tools/Find Refs/Find Refs (Selection) %&f")]
        private static void FindSelection()
        {
            var obj = Selection.activeObject;
            if (obj == null) return;

            if (EditorUtility.IsPersistent(obj))
            {
                FindRefsWindow.Open(ScanTarget.FromAsset(obj));
                return;
            }

            var go = obj as GameObject;
            if (go != null)
            {
                FindRefsWindow.Open(ScanTarget.FromGameObject(go));
                return;
            }

            var c = obj as Component;
            if (c != null) FindRefsWindow.Open(ScanTarget.FromComponent(c));
        }

        [MenuItem("Tools/Find Refs/Find Missing References")]
        private static void FindMissing()
        {
            FindRefsWindow.Open(ScanTarget.ForMissing());
        }
    }
}
#endif
