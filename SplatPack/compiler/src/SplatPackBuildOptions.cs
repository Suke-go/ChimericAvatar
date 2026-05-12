namespace SplatPack.Compiler;

public enum SplatPackRotationOrder
{
    Auto,
    XYZW,
    WXYZ,
}

public sealed class SplatPackBuildOptions
{
    public int ChunkSize { get; init; } = 4096;
    public SplatPackRotationOrder RotationOrder { get; init; } = SplatPackRotationOrder.Auto;
}
