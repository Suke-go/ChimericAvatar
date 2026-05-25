using System.Numerics;

namespace SplatPack.Compiler;

public readonly record struct SplatPackBounds(Vector3 Min, Vector3 Max);

public readonly record struct SplatPackChunk(
    Vector3 BoundsMin,
    Vector3 BoundsMax,
    int SplatOffset,
    int SplatCount,
    int LodLevel);

public readonly record struct SplatPackSplat(
    Vector4 CenterWS,
    Vector4 Axis0WS,
    Vector4 Axis1WS,
    Vector4 Axis2WS,
    Vector4 Color,
    Vector4 Meta,
    Vector4 Sh1R,
    Vector4 Sh1G,
    Vector4 Sh1B);

public readonly record struct SplatPackSupportMetadata(uint Packed0, uint Packed1);

public sealed class SplatPackPackage
{
    public required SplatPackBounds Bounds { get; init; }
    public required SplatPackChunk[] Chunks { get; init; }
    public required SplatPackSplat[] Splats { get; init; }
    public Vector4[] Sh3Coefficients { get; init; } = Array.Empty<Vector4>();
    public Vector4[] ResearchMetadata { get; init; } = Array.Empty<Vector4>();
    public Vector4[] ArtifactRepairMetadata { get; init; } = Array.Empty<Vector4>();
    public Vector4[] CellArtifactMetadata { get; init; } = Array.Empty<Vector4>();
    public int CellArtifactMetadataStride { get; init; } = SplatPackWriter.CellArtifactMetadataStride;
    public SplatPackSupportMetadata[] SupportMetadata { get; init; } = Array.Empty<SplatPackSupportMetadata>();
    public Vector4[] SphericalSupportField { get; init; } = Array.Empty<Vector4>();
    public int SphericalSupportFieldStride { get; init; } = SplatPackWriter.SphericalSupportFieldStride;
}
