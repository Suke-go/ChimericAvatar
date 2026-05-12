using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SplatPack.Runtime
{
    [Serializable]
    public struct SplatPackChunk
    {
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public int SplatOffset;
        public int SplatCount;
        public int LodLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SplatPackSplat
    {
        public Vector4 CenterWS;
        public Vector4 Axis0WS;
        public Vector4 Axis1WS;
        public Vector4 Axis2WS;
        public Vector4 Color;
        public Vector4 Meta;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SplatPackProjectedSplat
    {
        public Vector4 ClipCenter;
        public Vector4 Axis0Ndc;
        public Vector4 Axis1Ndc;
        public Vector4 Color;
        public Vector4 Meta;
    }

    public sealed class SplatPackPackage
    {
        public Bounds Bounds;
        public SplatPackChunk[] Chunks = Array.Empty<SplatPackChunk>();
        public SplatPackSplat[] Splats = Array.Empty<SplatPackSplat>();

        public int SplatCount => Splats.Length;
        public int ChunkCount => Chunks.Length;
    }

    public static class SplatPackFormat
    {
        public const int Version = 1;
        public const int SplatStride = 24 * sizeof(float);
        public const int ProjectedSplatStride = 20 * sizeof(float);
        public const int ChunkStride = 6 * sizeof(float) + 3 * sizeof(int);
        public const int LegacyHeaderChunkStride = 8 * sizeof(float) + 3 * sizeof(int);
    }
}
