using System.IO;
using SplatPack.Runtime;
using UnityEditor.AssetImporters;

namespace SplatPack.Editor
{
    [ScriptedImporter(1, "splatpack")]
    public sealed class SplatPackImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext context)
        {
            byte[] bytes = File.ReadAllBytes(context.assetPath);
            var asset = UnityEngine.ScriptableObject.CreateInstance<SplatPackAsset>();
            asset.SetPackageBytes(bytes);

            context.AddObjectToAsset("SplatPack", asset);
            context.SetMainObject(asset);
        }
    }
}
