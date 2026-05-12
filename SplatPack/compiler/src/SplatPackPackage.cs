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
    Vector4 Meta);

public sealed class SplatPackPackage
{
    public required SplatPackBounds Bounds { get; init; }
    public required SplatPackChunk[] Chunks { get; init; }
    public required SplatPackSplat[] Splats { get; init; }
}
