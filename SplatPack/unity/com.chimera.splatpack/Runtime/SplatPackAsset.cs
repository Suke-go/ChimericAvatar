using UnityEngine;

namespace SplatPack.Runtime
{
    [CreateAssetMenu(menuName = "SplatPack/SplatPack Asset")]
    public sealed class SplatPackAsset : ScriptableObject
    {
        [SerializeField] private byte[] packageBytes = System.Array.Empty<byte>();

        public byte[] PackageBytes => packageBytes;

        public void SetPackageBytes(byte[] bytes)
        {
            packageBytes = bytes ?? System.Array.Empty<byte>();
        }

        public SplatPackPackage Load()
        {
            if (packageBytes == null || packageBytes.Length == 0)
            {
                throw new System.InvalidOperationException("SplatPackAsset has no package bytes.");
            }

            return SplatPackLoader.Load(packageBytes);
        }
    }
}
