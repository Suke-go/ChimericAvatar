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
                EditorUtility.DisplayDialog(
                    "SplatPack",
                    "No usable .splatpack sample was found under Assets/SplatPackSamples.",
                    "OK");
                return;
            }

            CreateViewer(asset);
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
    }
}
