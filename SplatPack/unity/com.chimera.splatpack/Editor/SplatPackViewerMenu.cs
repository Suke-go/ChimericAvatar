using SplatPack.Runtime;
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
            string[] guids = AssetDatabase.FindAssets("t:SplatPackAsset", new[] { "Assets/SplatPackSamples" });
            if (guids.Length == 0)
            {
                EditorUtility.DisplayDialog(
                    "SplatPack",
                    "No imported .splatpack sample was found under Assets/SplatPackSamples.",
                    "OK");
                return;
            }

            string assetPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            SplatPackAsset asset = AssetDatabase.LoadAssetAtPath<SplatPackAsset>(assetPath);
            if (asset == null)
            {
                EditorUtility.DisplayDialog("SplatPack", $"Could not load sample asset at {assetPath}.", "OK");
                return;
            }

            CreateViewer(asset);
        }

        private static void CreateViewer(SplatPackAsset asset)
        {
            ComputeShader projection = FindProjectionCompute();
            if (projection == null)
            {
                EditorUtility.DisplayDialog("SplatPack", "Could not find SplatPackProjection.compute in the package.", "OK");
                return;
            }

            Material material = FindOrCreateMaterial();
            if (material == null)
            {
                EditorUtility.DisplayDialog("SplatPack", "Could not find or create the SplatPack material.", "OK");
                return;
            }

            var viewer = new GameObject("SplatPack Viewer");
            Undo.RegisterCreatedObjectUndo(viewer, "Create SplatPack Viewer");
            var renderer = viewer.AddComponent<SplatPackRenderer>();
            renderer.Configure(asset, projection, material, Camera.main);

            Selection.activeObject = viewer;
            EditorGUIUtility.PingObject(viewer);
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
