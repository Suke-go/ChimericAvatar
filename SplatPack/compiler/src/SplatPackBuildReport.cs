namespace SplatPack.Compiler;

public sealed class SplatPackBuildResult
{
    public required SplatPackPackage Package { get; init; }
    public required SplatPackBuildReport Report { get; init; }
}

public sealed class SplatPackBuildReport
{
    public required string TargetProfile { get; init; }
    public required string RotationOrder { get; init; }
    public required SplatPackBuildReportOptions Options { get; init; }
    public required SplatPackBuildReportCounts Counts { get; init; }
    public required SplatPackBuildReportBounds Bounds { get; init; }
    public required SplatPackScalarStats RawOpacity { get; init; }
    public required SplatPackScalarStats RawMaxAxisLength { get; init; }
    public required SplatPackScalarStats RawAxisRatio { get; init; }
    public required SplatPackScalarStats EmittedOpacity { get; init; }
    public required SplatPackScalarStats EmittedMaxAxisLength { get; init; }
    public required SplatPackScalarStats EmittedAxisRatio { get; init; }
    public required SplatPackChunkStats Chunks { get; init; }
    public required SplatPackMemoryStats Memory { get; init; }
}

public sealed class SplatPackBuildReportOptions
{
    public required int ChunkSize { get; init; }
    public required int MaxSplats { get; init; }
    public required float OpacityPruneThreshold { get; init; }
    public required float AxisLengthCap { get; init; }
    public required float MaxAxisLengthPercentile { get; init; }
    public required float MaxAxisLengthMultiplier { get; init; }
    public required float MaxAxisRatio { get; init; }
    public required float ContributionAxisPower { get; init; }
}

public sealed class SplatPackBuildReportCounts
{
    public required int InputSplats { get; init; }
    public required int EmittedSplats { get; init; }
    public required int PrunedInvalidSplats { get; init; }
    public required int PrunedLowOpacitySplats { get; init; }
    public required int PrunedBudgetSplats { get; init; }
    public required int AxisClampedSplats { get; init; }
    public required int AxisRatioClampedSplats { get; init; }
}

public sealed class SplatPackBuildReportBounds
{
    public required SplatPackVector3Dto Min { get; init; }
    public required SplatPackVector3Dto Max { get; init; }
    public required SplatPackVector3Dto Extents { get; init; }
}

public sealed class SplatPackVector3Dto
{
    public required float X { get; init; }
    public required float Y { get; init; }
    public required float Z { get; init; }
}

public sealed class SplatPackScalarStats
{
    public required int Count { get; init; }
    public required float Min { get; init; }
    public required float P50 { get; init; }
    public required float P90 { get; init; }
    public required float P95 { get; init; }
    public required float P99 { get; init; }
    public required float Max { get; init; }
    public required float Mean { get; init; }
}

public sealed class SplatPackChunkStats
{
    public required int Count { get; init; }
    public required int MinSplats { get; init; }
    public required int MaxSplats { get; init; }
    public required float MeanSplats { get; init; }
}

public sealed class SplatPackMemoryStats
{
    public required int SplatStrideBytes { get; init; }
    public required int ChunkStrideBytes { get; init; }
    public required long SplatBytes { get; init; }
    public required long ChunkBytes { get; init; }
    public required long TotalUncompressedBytes { get; init; }
    public required int RuntimeVertices { get; init; }
}
