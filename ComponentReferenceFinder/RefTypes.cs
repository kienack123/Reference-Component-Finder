#if UNITY_EDITOR
using UnityEngine;

namespace RefFinder
{
    internal enum RefScope
    {
        PrefabStage = 0,
        Scene = 1,
        PrefabAsset = 2,
        AssetFile = 3,
        SceneAsset = 4,
        Runtime = 5,
    }

    internal enum RefConfidence
    {
        Confirmed = 0,
        Candidate = 1,
    }

    internal enum MatchMode
    {
        Instance = 0,
        Missing = 1,
    }

    internal enum SceneScope
    {
        CurrentScene = 0,
        AllLoadedScenes = 1,
    }

    internal static class RefKind
    {
        public const string Component = "Component";
        public const string GameObject = "GameObject";
        public const string Transform = "Transform";
        public const string Asset = "Asset";
        public const string UnityEvent = "UnityEvent";
        public const string MissingRef = "MissingRef";
        public const string MissingScript = "MissingScript";
    }

    internal sealed class RefResult
    {
        public RefScope scope;
        public RefConfidence confidence = RefConfidence.Confirmed;
        public Object ping;
        public string ownerType = "";
        public string ownerName = "";
        public string ownerPath = "";
        public string assetPath = "";
        public string propertyPath = "";
        public string propertyLabel = "";
        public string refKind = "";
        public string detail = "";
        public string scriptPath;
        public int line = -1;
        public string bundleName = "";
        public int yamlLine = -1;

        public bool HasCode { get { return !string.IsNullOrEmpty(scriptPath) && line > 0; } }
    }

    internal sealed class ScanSettings
    {
        public SceneScope sceneScope = SceneScope.AllLoadedScenes;
        public bool scanPrefabAssets = true;
        public bool scanAssetFiles = true;
        public bool scanUnopenedScenes = false;
        public bool onlyMonoBehaviours = true;
        public bool includeInternalLinks = false;
        public bool turbo = false;
        public bool resolveBundleNames = false;
        public bool scanRuntimeRefs = true;
        public bool scanRuntimeStatics = true;

        public ScanSettings Clone()
        {
            return (ScanSettings)MemberwiseClone();
        }
    }
}
#endif
