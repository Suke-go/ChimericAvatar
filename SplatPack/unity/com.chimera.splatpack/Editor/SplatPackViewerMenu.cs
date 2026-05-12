using SplatPack.Runtime;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SplatPack.Editor
{
    public static class SplatPackViewerMenu
    {
        [MenuItem("Tools/SplatPack/Create Viewer From Selection", true)]
        private static bool ValidateCreateViewerFromSelection()
        {
            return Selection.activeObject is SplatPackAsset;
        }

        [MenuItem("Tools/SplatPack/Create Viewer From Selection")]
        private static void CreateViewerFromSelection()
        {
            var asset = Selection.activeObject as SplatPackAsset;
            if (asset == null)
            {
                EditorUtility.DisplayDialog("SplatPack", "Select a .splatpack asset first.", "OK");
                return;
            }

            CreateViewer(asset);
        }

        [MenuItem("Tools/SplatPack/Create Viewer From First Sample")]
        private static void CreateViewerFromFirstSample()
        {
            SplatPackAsset asset = FindFirstSampleAsset();
            if (asset == null)
            {
                const string message = "No usable .splatpack sample was found under Assets/SplatPackSamples.\n\n"
                    + "To verify SplatPack in Unity, copy a compiled .splatpack into Assets/SplatPackSamples, "
                    + "or select any imported SplatPackAsset and run Tools > SplatPack > Create Viewer From Selection.";
                Debug.LogWarning("[SplatPack] " + message.Replace('\n', ' '));
                EditorUtility.DisplayDialog(
                    "SplatPack",
                    message,
                    "OK");
                return;
            }

            Debug.Log("[SplatPack] Creating viewer from first sample: " + DescribeAsset(asset));
            CreateViewer(asset);
        }

        [MenuItem("Tools/SplatPack/Log Renderer Quality Defaults")]
        public static void LogRendererQualityDefaults()
        {
            var probe = new GameObject("SplatPack Renderer Defaults Probe");
            probe.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                var renderer = probe.AddComponent<SplatPackRenderer>();
                Debug.Log("[SplatPack] Renderer quality defaults: " + BuildRendererQualitySummary(renderer));
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }

        private static SplatPackAsset FindFirstSampleAsset()
        {
            const string sampleFolder = "Assets/SplatPackSamples";
            StageLocalExperimentSampleIfNeeded(sampleFolder);
            AssetDatabase.Refresh();

            if (!AssetDatabase.IsValidFolder(sampleFolder))
            {
                return null;
            }

            string[] files = Directory.GetFiles(sampleFolder, "*.splatpack", SearchOption.AllDirectories);
            foreach (string file in files)
            {
                string assetPath = file.Replace('\\', '/');
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                SplatPackAsset asset = AssetDatabase.LoadAssetAtPath<SplatPackAsset>(assetPath);
                if (asset != null)
                {
                    Debug.Log("[SplatPack] Found sample asset: " + DescribeAsset(asset));
                    return asset;
                }
            }

            return null;
        }

        private static void StageLocalExperimentSampleIfNeeded(string sampleFolder)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
            {
                return;
            }

            string destinationFolder = Path.Combine(projectRoot, sampleFolder.Replace('/', Path.DirectorySeparatorChar), "External");
            if (Directory.Exists(destinationFolder) &&
                Directory.GetFiles(destinationFolder, "*.splatpack", SearchOption.TopDirectoryOnly).Length > 0)
            {
                return;
            }

            string repoRoot = Directory.GetParent(projectRoot)?.FullName;
            if (string.IsNullOrEmpty(repoRoot))
            {
                return;
            }

            string sourcePath = Path.Combine(
                repoRoot,
                "SplatPack",
                "experiments",
                "data",
                "smoke",
                "j0n45_point_cloud.splatpack");
            if (!File.Exists(sourcePath))
            {
                return;
            }

            Directory.CreateDirectory(destinationFolder);
            string destinationPath = Path.Combine(destinationFolder, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, destinationPath, overwrite: true);
            Debug.Log("[SplatPack] Staged local experiment sample: " + ToAssetPath(destinationPath));
        }

        private static void CreateViewer(SplatPackAsset asset)
        {
            ComputeShader projection = FindProjectionCompute();
            if (projection == null)
            {
                EditorUtility.DisplayDialog("SplatPack", "Could not find SplatPackProjection.compute in the package.", "OK");
                return;
            }

            ComputeShader sort = FindSortCompute();

            Material material = FindOrCreateMaterial();
            if (material == null)
            {
                EditorUtility.DisplayDialog("SplatPack", "Could not find or create the SplatPack material.", "OK");
                return;
            }

            var viewer = new GameObject("SplatPack Viewer");
            Undo.RegisterCreatedObjectUndo(viewer, "Create SplatPack Viewer");
            var renderer = viewer.AddComponent<SplatPackRenderer>();
            renderer.Configure(asset, projection, material, Camera.main, recenterOnFirstDraw: true, sort: sort);

            Selection.activeObject = viewer;
            EditorGUIUtility.PingObject(viewer);

            Debug.Log(
                "[SplatPack] Created viewer: "
                + $"asset={DescribeAsset(asset)}, "
                + $"projection={AssetDatabase.GetAssetPath(projection)}, "
                + $"sort={(sort != null ? AssetDatabase.GetAssetPath(sort) : "not assigned")}, "
                + $"material={AssetDatabase.GetAssetPath(material)}, "
                + $"camera={(Camera.main != null ? Camera.main.name : "none")}, "
                + $"recenterOnFirstDraw=true, "
                + $"quality={BuildRendererQualitySummary(renderer)}.");
        }

        private static ComputeShader FindSortCompute()
        {
            string[] guids = AssetDatabase.FindAssets("SplatPackDepthSort t:ComputeShader");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains("com.chimera.splatpack"))
                {
                    return AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                }
            }

            return null;
        }

        private static ComputeShader FindProjectionCompute()
        {
            string[] guids = AssetDatabase.FindAssets("SplatPackProjection t:ComputeShader");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains("com.chimera.splatpack"))
                {
                    return AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                }
            }

            return null;
        }

        private static Material FindOrCreateMaterial()
        {
            string[] guids = AssetDatabase.FindAssets("SplatPackGeneratedMaterial t:Material");
            foreach (string guid in guids)
            {
                string materialPath = AssetDatabase.GUIDToAssetPath(guid);
                Material existing = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (existing != null && existing.shader != null && existing.shader.name == "Chimera/SplatPack Gaussian Splat")
                {
                    return existing;
                }
            }

            Shader shader = Shader.Find("Chimera/SplatPack Gaussian Splat");
            if (shader == null)
            {
                return null;
            }

            const string folder = "Assets/SplatPack";
            const string path = "Assets/SplatPack/SplatPackGeneratedMaterial.mat";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder("Assets", "SplatPack");
            }

            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();
            return material;
        }

        private static string DescribeAsset(SplatPackAsset asset)
        {
            if (asset == null)
            {
                return "none";
            }

            string path = AssetDatabase.GetAssetPath(asset);
            int bytes = asset.PackageBytes != null ? asset.PackageBytes.Length : 0;
            return $"{path} ({FormatBytes(bytes)})";
        }

        private static string FormatBytes(int bytes)
        {
            if (bytes >= 1024 * 1024)
            {
                return $"{bytes / (1024f * 1024f):0.##} MiB";
            }

            if (bytes >= 1024)
            {
                return $"{bytes / 1024f:0.##} KiB";
            }

            return bytes + " bytes";
        }

        private static string BuildRendererQualitySummary(SplatPackRenderer renderer)
        {
            var serialized = new SerializedObject(renderer);
            return string.Join(
                ", ",
                "opacityScale=" + ReadFloat(serialized, "opacityScale"),
                "opacityPower=" + ReadFloat(serialized, "opacityPower"),
                "splatScale=" + ReadFloat(serialized, "splatScale"),
                "alphaClip=" + ReadFloat(serialized, "alphaClip"),
                "lowAlphaKnee=" + ReadFloat(serialized, "lowAlphaKnee"),
                "tailExtentMin=" + ReadFloat(serialized, "tailExtentMin"),
                "tailExtentMax=" + ReadFloat(serialized, "tailExtentMax"),
                "minScreenRadiusPixels=" + ReadFloat(serialized, "minScreenRadiusPixels"),
                "maxScreenRadiusPixels=" + ReadFloat(serialized, "maxScreenRadiusPixels"),
                "maxScreenEccentricity=" + ReadFloat(serialized, "maxScreenEccentricity"),
                "kernel2DSize=" + ReadFloat(serialized, "kernel2DSize"),
                "enforceStandaloneRadiusCap=" + ReadBool(serialized, "enforceStandaloneRadiusCap"),
                "sortMode=" + ReadEnum(serialized, "sortMode"),
                "sortIntervalFrames=" + ReadInt(serialized, "sortIntervalFrames"),
                "xrSortIntervalFrames=" + ReadInt(serialized, "xrSortIntervalFrames"),
                "depthSortBinCount=" + ReadInt(serialized, "depthSortBinCount"),
                "reuseStableProjectionCache=" + ReadBool(serialized, "reuseStableProjectionCache"),
                "logDiagnostics=" + ReadBool(serialized, "logDiagnostics"));
        }

        private static string ReadFloat(SerializedObject serialized, string propertyName)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            return property != null ? property.floatValue.ToString("0.####") : "missing";
        }

        private static string ReadInt(SerializedObject serialized, string propertyName)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            return property != null ? property.intValue.ToString() : "missing";
        }

        private static string ReadBool(SerializedObject serialized, string propertyName)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            return property != null ? property.boolValue.ToString() : "missing";
        }

        private static string ReadEnum(SerializedObject serialized, string propertyName)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null
                || property.enumValueIndex < 0
                || property.enumValueIndex >= property.enumDisplayNames.Length)
            {
                return "missing";
            }

            return property.enumDisplayNames[property.enumValueIndex];
        }

        private static string ToAssetPath(string fullPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
            {
                return fullPath;
            }

            string normalizedFullPath = fullPath.Replace('\\', '/');
            string normalizedProjectRoot = projectRoot.Replace('\\', '/').TrimEnd('/');
            if (normalizedFullPath.StartsWith(normalizedProjectRoot + "/"))
            {
                return normalizedFullPath.Substring(normalizedProjectRoot.Length + 1);
            }

            return normalizedFullPath;
        }
    }
}
