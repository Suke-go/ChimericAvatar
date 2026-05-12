namespace SplatPack.Compiler;

public enum SplatPackRotationOrder
{
    Auto,
    XYZW,
    WXYZ,
}

public enum SplatPackTargetProfile
{
    Reference,
    StandaloneXr,
}

public sealed class SplatPackBuildOptions
{
    public int ChunkSize { get; init; } = 4096;
    public SplatPackRotationOrder RotationOrder { get; init; } = SplatPackRotationOrder.Auto;
    public SplatPackTargetProfile TargetProfile { get; init; } = SplatPackTargetProfile.StandaloneXr;
    public float OpacityPruneThreshold { get; init; } = 0.002f;
    public float MaxAxisLength { get; init; }
    public float MaxAxisLengthPercentile { get; init; } = 99f;
    public float MaxAxisLengthMultiplier { get; init; } = 1.1f;
}
