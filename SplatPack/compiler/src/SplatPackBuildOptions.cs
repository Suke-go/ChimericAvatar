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

public enum SplatPackOutputFormat
{
    Auto,
    PackedV4,
    ReferenceFloatV5,
    ResearchSectionedV6,
}

public enum SplatPackShMode
{
    Dc,
    Sh1,
    Sh3,
}

public enum SplatPackFilterMode
{
    None,
    Mip2D,
    AnalyticPixel,
}

public enum SplatPackPruningMode
{
    Heuristic,
    ResearchXrSensitivity,
}

public enum SplatPackSortMode
{
    DepthBins,
    StableRadixPerEye,
}

public enum SplatPackArtifactRepairMode
{
    None,
    GaussianPullbackV1,
    GaussianPullbackV2,
    ShellSurfaceV1,
}

public readonly record struct SplatPackPullbackWeights(
    float Coverage,
    float Edge,
    float Texture,
    float Layer,
    float Stereo)
{
    public static readonly SplatPackPullbackWeights Default = new(1f, 1f, 1f, 1f, 1f);

    public override string ToString()
    {
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"coverage={Coverage:0.###},edge={Edge:0.###},texture={Texture:0.###},layer={Layer:0.###},stereo={Stereo:0.###}");
    }
}

public sealed class SplatPackBuildOptions
{
    public int ChunkSize { get; init; } = 4096;
    public SplatPackRotationOrder RotationOrder { get; init; } = SplatPackRotationOrder.Auto;
    public SplatPackTargetProfile TargetProfile { get; init; } = SplatPackTargetProfile.StandaloneXr;
    public SplatPackOutputFormat OutputFormat { get; init; } = SplatPackOutputFormat.Auto;
    public SplatPackShMode ShMode { get; init; } = SplatPackShMode.Sh3;
    public SplatPackFilterMode FilterMode { get; init; } = SplatPackFilterMode.Mip2D;
    public SplatPackPruningMode PruningMode { get; init; } = SplatPackPruningMode.ResearchXrSensitivity;
    public SplatPackSortMode SortMode { get; init; } = SplatPackSortMode.StableRadixPerEye;

    // Post-pass class-aware floater drop (Direction C MVP).
    // Drops splats whose render hint class is one of the configured haze
    // classes (FreeSpace, Floater, optionally LayerRisk). Acts after the
    // standard pruning + budget stages and before final report assembly,
    // so the resulting SplatPack file is permanently free of those splats.
    // Optional SupportProtect threshold preserves splats whose SSPF support
    // is high even if their hint class would otherwise be dropped.
    public bool DropFreeSpaceClass { get; init; }
    public bool DropFloaterClass { get; init; }
    public bool DropLayerRiskClass { get; init; }
    public float ClassDropSupportProtect { get; init; }

    // Direction C: field-aware pruning. Voxelizes splat density into a 3D grid,
    // scores each splat by its local field contribution, and drops the lowest
    // N fraction. Catches floaters that the class-label drop misses.
    public bool EnableFieldAwarePruning { get; init; }
    public int FieldVoxelGrid { get; init; } = 64;
    public float FieldPruneFraction { get; init; } = 0.10f;
    public float FieldNeighborhoodRadiusVoxels { get; init; } = 1.5f;
    public float FieldProtectAlpha { get; init; } = 0.5f;

    // Direction C: synthetic splat injection. Adds splats to under-served regions
    // detected via the same voxel field used by ApplyFieldAwarePruning. Used to
    // complete holes in object-centric captures without retraining.
    public bool EnableSplatInjection { get; init; }
    public int InjectionVoxelGrid { get; init; } = 64;
    public int InjectionTargetCount { get; init; }
    public float InjectionMaxFraction { get; init; } = 0.10f;
    // Number of drop-inject passes. 1 = original single-pass behaviour.
    // >1 re-evaluates the voxel density grid after each pass so newly-injected
    // splats can promote previously sub-threshold cavities (fixes the counter
    // fragmentation failure mode). Capped at 8 to keep compile time bounded.
    public int InjectionPasses { get; init; } = 1;
    // Scene classifier: when enabled, auto-detects whether the input asset is
    // object-centric or scene-centric. Scene-centric assets (heavy background
    // contamination) cause injection to fragment fidelity rather than improve
    // it; the classifier skips injection in that case. Disabled by default to
    // keep the headline configuration deterministic and explicit.
    public bool EnableSceneClassifier { get; init; }
    public float SceneClassifierCoreFraction { get; init; } = 0.45f;
    // Surface-normal-aware injection: estimate the local surface plane from the
    // neighbour splats via 3x3 covariance + smallest-eigenvector normal, then
    // (a) snap the injected position from voxel centre onto the plane, and
    // (b) reorient the injected splat's axes so its smallest axis lies along
    // the surface normal. This reduces grazing-angle holes on object surfaces
    // (kadan1 flowers, bonsai leaves) compared with axis-aligned voxel-centre
    // injection. Disabled by default so the headline single-pass result stays
    // reproducible without this flag.
    public bool EnableSurfaceAwareInjection { get; init; }
    public float SurfaceAwareThicknessScale { get; init; } = 0.35f;
    public float InjectionNeighborhoodRadiusVoxels { get; init; } = 1.5f;
    public float InjectionDeficitThreshold { get; init; } = 0.5f;
    public float InjectionAlphaScale { get; init; } = 0.6f;
    public float InjectionScaleFactor { get; init; } = 1.0f;
    public int MaxSplats { get; init; } = 120_000;
    public float OpacityPruneThreshold { get; init; } = 0.002f;
    public float MaxAxisLength { get; init; }
    public float MaxAxisLengthPercentile { get; init; } = 90f;
    public float MaxAxisLengthMultiplier { get; init; } = 1.1f;
    public float MaxAxisRatio { get; init; } = 8f;
    public float ContributionAxisPower { get; init; } = 0.5f;
    public float ContributionPruneThreshold { get; init; } = 0.0015f;
    public bool EnableViewSampledImportance { get; init; } = true;
    public string ViewSampleSource { get; init; } = "default-octant";
    public SplatPackViewSample[] ViewSamples { get; init; } = Array.Empty<SplatPackViewSample>();
    public float MaxImportanceError { get; init; } = 0f;
    public bool EnableCoverageCompensation { get; init; } = true;
    public float MaxCoverageBoost { get; init; } = 2.4f;
    public float CoverageAxisPower { get; init; } = 1.0f;
    public float FarLodKeepFraction { get; init; } = 0.35f;
    public float MidLodKeepFraction { get; init; } = 0.68f;
    public bool EnableAttributeQuantization { get; init; } = true;
    public int PositionQuantizationBits { get; init; } = 16;
    public int AxisQuantizationBits { get; init; } = 14;
    public int ColorQuantizationBits { get; init; } = 8;
    public int OpacityQuantizationBits { get; init; } = 10;
    public bool EnableStreamingLayout { get; init; } = true;
    public bool EnableSpatialOutlierPrune { get; init; } = true;
    public int SpatialOutlierGridResolution { get; init; }
    public int SpatialOutlierMinNeighbors { get; init; } = 5;
    public float SpatialOutlierKeepImportance { get; init; } = 0.55f;
    // Gaussian pullback repair is O(candidates * probe views * iterations) and adds
    // tens of minutes to default-budget compiles of >100k-splat captures. Default to
    // None so the build path is fast; the CLI / Unity tooling expose the opt-in flag.
    public SplatPackArtifactRepairMode ArtifactRepairMode { get; init; } = SplatPackArtifactRepairMode.None;
    public int ProbeResolution { get; init; } = 256;
    public int ProbeViewCount { get; init; } = 16;
    public string ProbeViewSource { get; init; } = "default-probe";
    public SplatPackViewSample[] ProbeViewSamples { get; init; } = Array.Empty<SplatPackViewSample>();
    public float RepairBudgetRatio { get; init; } = 0.12f;
    public int RepairIterations { get; init; } = 3;
    public int ProbeCellSize { get; init; } = 4;
    public float ProbeFovYDegrees { get; init; } = 70f;
    public float ProbeAspect { get; init; } = 1f;
    public float ProbeIpdMeters { get; init; } = 0.064f;
    public int ProbeLayerBins { get; init; } = 2;
    public SplatPackPullbackWeights PullbackWeights { get; init; } = SplatPackPullbackWeights.Default;

    public SplatPackBuildOptions NormalizeForBuild()
    {
        if (TargetProfile != SplatPackTargetProfile.Reference)
        {
            return this;
        }

        return new SplatPackBuildOptions
        {
            ChunkSize = ChunkSize,
            RotationOrder = RotationOrder,
            TargetProfile = TargetProfile,
            OutputFormat = OutputFormat == SplatPackOutputFormat.Auto ? SplatPackOutputFormat.ReferenceFloatV5 : OutputFormat,
            ShMode = ShMode,
            FilterMode = SplatPackFilterMode.None,
            PruningMode = SplatPackPruningMode.Heuristic,
            SortMode = SortMode,
            MaxSplats = 0,
            OpacityPruneThreshold = 0f,
            MaxAxisLength = 0f,
            MaxAxisLengthPercentile = 0f,
            MaxAxisLengthMultiplier = MaxAxisLengthMultiplier,
            MaxAxisRatio = 0f,
            ContributionAxisPower = ContributionAxisPower,
            ContributionPruneThreshold = 0f,
            EnableViewSampledImportance = false,
            ViewSampleSource = ViewSampleSource,
            ViewSamples = Array.Empty<SplatPackViewSample>(),
            MaxImportanceError = 0f,
            EnableCoverageCompensation = false,
            MaxCoverageBoost = 1f,
            CoverageAxisPower = 0f,
            FarLodKeepFraction = 1f,
            MidLodKeepFraction = 1f,
            EnableAttributeQuantization = false,
            PositionQuantizationBits = 0,
            AxisQuantizationBits = 0,
            ColorQuantizationBits = 0,
            OpacityQuantizationBits = 0,
            EnableStreamingLayout = false,
            EnableSpatialOutlierPrune = false,
            SpatialOutlierGridResolution = 0,
            SpatialOutlierMinNeighbors = SpatialOutlierMinNeighbors,
            SpatialOutlierKeepImportance = SpatialOutlierKeepImportance,
            ArtifactRepairMode = SplatPackArtifactRepairMode.None,
            ProbeResolution = ProbeResolution,
            ProbeViewCount = ProbeViewCount,
            ProbeViewSource = ProbeViewSource,
            ProbeViewSamples = Array.Empty<SplatPackViewSample>(),
            RepairBudgetRatio = RepairBudgetRatio,
            RepairIterations = RepairIterations,
            ProbeCellSize = ProbeCellSize,
            ProbeFovYDegrees = ProbeFovYDegrees,
            ProbeAspect = ProbeAspect,
            ProbeIpdMeters = ProbeIpdMeters,
            ProbeLayerBins = ProbeLayerBins,
            PullbackWeights = PullbackWeights,
            EnableFieldAwarePruning = false,
            FieldVoxelGrid = FieldVoxelGrid,
            FieldPruneFraction = FieldPruneFraction,
            FieldNeighborhoodRadiusVoxels = FieldNeighborhoodRadiusVoxels,
            FieldProtectAlpha = FieldProtectAlpha,
            EnableSplatInjection = false,
            InjectionVoxelGrid = InjectionVoxelGrid,
            InjectionTargetCount = InjectionTargetCount,
            InjectionMaxFraction = InjectionMaxFraction,
            InjectionPasses = InjectionPasses,
            InjectionNeighborhoodRadiusVoxels = InjectionNeighborhoodRadiusVoxels,
            InjectionDeficitThreshold = InjectionDeficitThreshold,
            InjectionAlphaScale = InjectionAlphaScale,
            InjectionScaleFactor = InjectionScaleFactor,
            EnableSceneClassifier = EnableSceneClassifier,
            SceneClassifierCoreFraction = SceneClassifierCoreFraction,
            EnableSurfaceAwareInjection = EnableSurfaceAwareInjection,
            SurfaceAwareThicknessScale = SurfaceAwareThicknessScale,
        };
    }
}
