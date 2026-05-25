namespace SplatPack.Compiler;

public sealed class SplatPackBuildResult
{
    public required SplatPackPackage Package { get; init; }
    public required SplatPackBuildReport Report { get; init; }

    // Direction C MVP: tally of splats removed by the class-aware drop post-pass.
    // All zero when --drop-{free,floater,layer}-class are not set.
    public SplatPackClassDropReport ClassDrop { get; init; } = SplatPackClassDropReport.None;

    // Direction C: tally of splats removed by the field-aware pruning post-pass.
    // All zero when --field-prune is not set.
    public SplatPackFieldPruneReport FieldPrune { get; init; } = SplatPackFieldPruneReport.None;

    // Direction C: tally of splats added by the synthetic injection post-pass.
    // Disabled (all zero) when --inject-splats is not set.
    public SplatPackSplatInjectionReport Injection { get; init; } = SplatPackSplatInjectionReport.None;
}

public sealed class SplatPackClassDropReport
{
    public required int DroppedFreeSpace { get; init; }
    public required int DroppedFloater { get; init; }
    public required int DroppedLayerRisk { get; init; }
    public required int ProtectedBySupport { get; init; }
    public required float SupportProtectThreshold { get; init; }

    public int TotalDropped => DroppedFreeSpace + DroppedFloater + DroppedLayerRisk;

    public static readonly SplatPackClassDropReport None = new()
    {
        DroppedFreeSpace = 0,
        DroppedFloater = 0,
        DroppedLayerRisk = 0,
        ProtectedBySupport = 0,
        SupportProtectThreshold = 0f,
    };
}

public sealed class SplatPackFieldPruneReport
{
    public required bool Enabled { get; init; }
    public required int VoxelGrid { get; init; }
    public required float PruneFraction { get; init; }
    public required int DroppedSplats { get; init; }
    public required int RetainedSplats { get; init; }
    public required float LowestRetainedScore { get; init; }
    public required float HighestDroppedScore { get; init; }
    public required float MeanScore { get; init; }
    public required float ProtectedByAlpha { get; init; }

    public static readonly SplatPackFieldPruneReport None = new()
    {
        Enabled = false,
        VoxelGrid = 0,
        PruneFraction = 0f,
        DroppedSplats = 0,
        RetainedSplats = 0,
        LowestRetainedScore = 0f,
        HighestDroppedScore = 0f,
        MeanScore = 0f,
        ProtectedByAlpha = 0f,
    };
}

public sealed class SplatPackSplatInjectionReport
{
    public required bool Enabled { get; init; }
    public required int VoxelGrid { get; init; }
    public required int InjectedSplats { get; init; }
    public required int UnderServedVoxels { get; init; }
    public required int DeadVoxels { get; init; }
    public required float MeanNeighborCount { get; init; }
    public required float DeficitThreshold { get; init; }

    public static readonly SplatPackSplatInjectionReport None = new()
    {
        Enabled = false,
        VoxelGrid = 0,
        InjectedSplats = 0,
        UnderServedVoxels = 0,
        DeadVoxels = 0,
        MeanNeighborCount = 0f,
        DeficitThreshold = 0f,
    };
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
    public required SplatPackCompilerOptimizationStats Optimization { get; init; }
    public required SplatPackRenderHintStats RenderHints { get; init; }
    public required SplatPackLayerErrorStats LayerStats { get; init; }
    public required SplatPackArtifactRepairStats ArtifactRepair { get; init; }
    public required SplatPackShellSurfaceRepairStats ShellSurfaceRepair { get; init; }
    public required SplatPackSplatInjectionReport Injection { get; init; }
    public required SplatPackSphericalInformationStats SphericalInformation { get; init; }
    public required SplatPackMemoryStats Memory { get; init; }
}

public sealed class SplatPackBuildReportOptions
{
    public required int ChunkSize { get; init; }
    public required string OutputFormat { get; init; }
    public required string ShMode { get; init; }
    public required string FilterMode { get; init; }
    public required string PruningMode { get; init; }
    public required string SortMode { get; init; }
    public required int MaxSplats { get; init; }
    public required float OpacityPruneThreshold { get; init; }
    public required float AxisLengthCap { get; init; }
    public required float MaxAxisLengthPercentile { get; init; }
    public required float MaxAxisLengthMultiplier { get; init; }
    public required float MaxAxisRatio { get; init; }
    public required float ContributionAxisPower { get; init; }
    public required float ContributionPruneThreshold { get; init; }
    public required bool EnableViewSampledImportance { get; init; }
    public required string ViewSampleSource { get; init; }
    public required int ViewSampleCount { get; init; }
    public required float MaxImportanceError { get; init; }
    public required bool EnableCoverageCompensation { get; init; }
    public required float MaxCoverageBoost { get; init; }
    public required float CoverageAxisPower { get; init; }
    public required float FarLodKeepFraction { get; init; }
    public required float MidLodKeepFraction { get; init; }
    public required bool EnableAttributeQuantization { get; init; }
    public required int PositionQuantizationBits { get; init; }
    public required int AxisQuantizationBits { get; init; }
    public required int ColorQuantizationBits { get; init; }
    public required int OpacityQuantizationBits { get; init; }
    public required bool EnableStreamingLayout { get; init; }
    public required bool EnableSpatialOutlierPrune { get; init; }
    public required int SpatialOutlierGridResolution { get; init; }
    public required int SpatialOutlierMinNeighbors { get; init; }
    public required float SpatialOutlierKeepImportance { get; init; }
    public required string ArtifactRepairMode { get; init; }
    public required int ProbeResolution { get; init; }
    public required int ProbeViewCount { get; init; }
    public required string ProbeViewSource { get; init; }
    public required float RepairBudgetRatio { get; init; }
    public required int RepairIterations { get; init; }
    public required int ProbeCellSize { get; init; }
    public required float ProbeFovYDegrees { get; init; }
    public required float ProbeAspect { get; init; }
    public required float ProbeIpdMeters { get; init; }
    public required int ProbeLayerBins { get; init; }
    public required string PullbackWeights { get; init; }
    public required bool EnableSplatInjection { get; init; }
    public required int InjectionVoxelGrid { get; init; }
    public required int InjectionTargetCount { get; init; }
    public required float InjectionMaxFraction { get; init; }
    public required float InjectionNeighborhoodRadiusVoxels { get; init; }
    public required float InjectionDeficitThreshold { get; init; }
    public required float InjectionAlphaScale { get; init; }
    public required float InjectionScaleFactor { get; init; }
}

public sealed class SplatPackBuildReportCounts
{
    public required int InputSplats { get; init; }
    public required int EmittedSplats { get; init; }
    public required int PrunedInvalidSplats { get; init; }
    public required int PrunedLowOpacitySplats { get; init; }
    public required int PrunedLowContributionSplats { get; init; }
    public required int PrunedSpatialOutlierSplats { get; init; }
    public required int PrunedBudgetSplats { get; init; }
    public required int AxisClampedSplats { get; init; }
    public required int AxisRatioClampedSplats { get; init; }
}

public sealed class SplatPackCompilerOptimizationStats
{
    public required float InputImportanceMass { get; init; }
    public required float EmittedImportanceMass { get; init; }
    public required float RetainedImportanceFraction { get; init; }
    public required float EstimatedDroppedImportanceFraction { get; init; }
    public required float MeanCoverageBoost { get; init; }
    public required float MaxCoverageBoost { get; init; }
    public required int LodTier0Splats { get; init; }
    public required int LodTier1Splats { get; init; }
    public required int LodTier2Splats { get; init; }
    public required int QuantizedSplats { get; init; }
    public required int StreamingChunks { get; init; }
}

public sealed class SplatPackRenderHintStats
{
    public required int SurfaceSplats { get; init; }
    public required int MicroDetailSplats { get; init; }
    public required int BroadSurfaceSplats { get; init; }
    public required int FreeSpaceSplats { get; init; }
    public required int FloaterSplats { get; init; }
    public required int ForegroundSplats { get; init; }
    public required int LayerRiskSplats { get; init; }
}

public sealed class SplatPackLayerErrorStats
{
    public required float MacroCoverageMass { get; init; }
    public required float MicroDetailMass { get; init; }
    public required float FillerMass { get; init; }
    public required float FreeSpaceMass { get; init; }
    public required float MacroErrorProxy { get; init; }
    public required float MicroErrorProxy { get; init; }
    public required float FreeSpaceFraction { get; init; }
}

public sealed class SplatPackArtifactRepairStats
{
    public required string Mode { get; init; }
    public required int ProbeResolution { get; init; }
    public required int ProbeViewCount { get; init; }
    public required int InitialBudgetSplats { get; init; }
    public required int FinalBudgetSplats { get; init; }
    public required int ResidualCells { get; init; }
    public required int CoverageResidualCells { get; init; }
    public required int EdgeResidualCells { get; init; }
    public required int TextureResidualCells { get; init; }
    public required int LayerResidualCells { get; init; }
    public required int StereoResidualCells { get; init; }
    public required int ReinsertedCoverageSplats { get; init; }
    public required int ReinsertedEdgeSplats { get; init; }
    public required int ReinsertedTextureSplats { get; init; }
    public required int ReinsertedLayerSplats { get; init; }
    public required int ReinsertedStereoSplats { get; init; }
    public required int BoostedSplats { get; init; }
    public required int BoostSaturatedCells { get; init; }
    public required int LodUpgradedSplats { get; init; }
    public required int ShPreservedSplats { get; init; }
    public required int QuantizationRelaxedSplats { get; init; }
    public required float CoverageResidualMean { get; init; }
    public required float EdgeResidualMean { get; init; }
    public required float TextureResidualMean { get; init; }
    public required float LayerResidualMean { get; init; }
    public required float StereoResidualMean { get; init; }
    public required float MaxAppliedBoost { get; init; }
    public required float ResidualBefore { get; init; }
    public required float ResidualAfter { get; init; }
    public required float ResidualReductionRatio { get; init; }
    public required int RejectedRepairs { get; init; }
    public required float StereoMismatchBefore { get; init; }
    public required float StereoMismatchAfter { get; init; }
    public required int LayerCrossBoostRejected { get; init; }
}

public sealed class SplatPackShellSurfaceRepairStats
{
    public static SplatPackShellSurfaceRepairStats None { get; } = new()
    {
        Enabled = false,
        Mode = SplatPackArtifactRepairMode.None.ToString(),
        InitialSelectedSplats = 0,
        FinalSelectedSplats = 0,
        ReinsertedSplats = 0,
        ResidualBefore = 0f,
        ResidualAfter = 0f,
        ResidualReductionRatio = 0f,
        SurfaceResidualBefore = 0f,
        SurfaceResidualAfter = 0f,
        MicroResidualBefore = 0f,
        MicroResidualAfter = 0f,
        HighBandResidualBefore = 0f,
        HighBandResidualAfter = 0f,
        LowBandExcessBefore = 0f,
        LowBandExcessAfter = 0f,
        VoxelHoleResidualBefore = 0f,
        VoxelHoleResidualAfter = 0f,
        WeakShellCellsBefore = 0,
        WeakShellCellsAfter = 0,
    };

    public required bool Enabled { get; init; }
    public required string Mode { get; init; }
    public required int InitialSelectedSplats { get; init; }
    public required int FinalSelectedSplats { get; init; }
    public required int ReinsertedSplats { get; init; }
    public required float ResidualBefore { get; init; }
    public required float ResidualAfter { get; init; }
    public required float ResidualReductionRatio { get; init; }
    public required float SurfaceResidualBefore { get; init; }
    public required float SurfaceResidualAfter { get; init; }
    public required float MicroResidualBefore { get; init; }
    public required float MicroResidualAfter { get; init; }
    public required float HighBandResidualBefore { get; init; }
    public required float HighBandResidualAfter { get; init; }
    public required float LowBandExcessBefore { get; init; }
    public required float LowBandExcessAfter { get; init; }
    public required float VoxelHoleResidualBefore { get; init; }
    public required float VoxelHoleResidualAfter { get; init; }
    public required int WeakShellCellsBefore { get; init; }
    public required int WeakShellCellsAfter { get; init; }
}

public sealed class SplatPackSphericalInformationStats
{
    public static SplatPackSphericalInformationStats Empty { get; } = new()
    {
        Mode = "disabled",
        LongitudeCells = 0,
        LatitudeCells = 0,
        CellCount = 0,
        SourceNonEmptyCells = 0,
        EmittedNonEmptyCells = 0,
        SourceSolidAngleMass = 0f,
        EmittedSolidAngleMass = 0f,
        CoverageResidual = 0f,
        SurfaceResidual = 0f,
        MicroDetailResidual = 0f,
        FreeSpaceResidual = 0f,
        LowBandResidual = 0f,
        MidBandResidual = 0f,
        HighBandResidual = 0f,
        AngularFootprintMean = 0f,
        AngularFootprintUnderRate = 0f,
        AngularFootprintOverRate = 0f,
        SupportFieldRecords = 0,
        SupportFieldSupportedCells = 0,
        AngularDeficit = 0f,
        UnsupportedAngularMass = 0f,
        ParticleRisk = 0f,
        BlurRisk = 0f,
    };

    public required string Mode { get; init; }
    public required int LongitudeCells { get; init; }
    public required int LatitudeCells { get; init; }
    public required int CellCount { get; init; }
    public required int SourceNonEmptyCells { get; init; }
    public required int EmittedNonEmptyCells { get; init; }
    public required float SourceSolidAngleMass { get; init; }
    public required float EmittedSolidAngleMass { get; init; }
    public required float CoverageResidual { get; init; }
    public required float SurfaceResidual { get; init; }
    public required float MicroDetailResidual { get; init; }
    public required float FreeSpaceResidual { get; init; }
    public required float LowBandResidual { get; init; }
    public required float MidBandResidual { get; init; }
    public required float HighBandResidual { get; init; }
    public required float AngularFootprintMean { get; init; }
    public required float AngularFootprintUnderRate { get; init; }
    public required float AngularFootprintOverRate { get; init; }
    public required int SupportFieldRecords { get; init; }
    public required int SupportFieldSupportedCells { get; init; }
    public required float AngularDeficit { get; init; }
    public required float UnsupportedAngularMass { get; init; }
    public required float ParticleRisk { get; init; }
    public required float BlurRisk { get; init; }
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
    public required long SupportMetadataBytes { get; init; }
    public required long SphericalSupportFieldBytes { get; init; }
    public required long TotalUncompressedBytes { get; init; }
    public required int RuntimeVertices { get; init; }
}
