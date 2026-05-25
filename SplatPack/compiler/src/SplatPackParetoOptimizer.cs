using System.Globalization;
using System.Text;

namespace SplatPack.Compiler;

public sealed class SplatPackParetoSweepOptions
{
    public required int[] MaxSplatBudgets { get; init; }
    public required float[] MaxCoverageBoosts { get; init; }
    public required float[] MaxImportanceErrors { get; init; }
    public required SplatPackDeviceBudget DeviceBudget { get; init; }
}

public sealed record class SplatPackParetoRow
{
    public required int MaxSplats { get; init; }
    public required float MaxCoverageBoost { get; init; }
    public required float MaxImportanceError { get; init; }
    public required int EmittedSplats { get; init; }
    public required int Chunks { get; init; }
    public required float RetainedImportance { get; init; }
    public required float DroppedImportance { get; init; }
    public required float MeanCoverageBoost { get; init; }
    public required float ObservedMaxCoverageBoost { get; init; }
    public required int LodTier0Splats { get; init; }
    public required int LodTier1Splats { get; init; }
    public required int LodTier2Splats { get; init; }
    public required int AxisClampedSplats { get; init; }
    public required int AxisRatioClampedSplats { get; init; }
    public required int QuantizedSplats { get; init; }
    public required float PackageMegabytes { get; init; }
    public required float EstimatedGpuRuntimeMegabytes { get; init; }
    public required float EstimatedResidentMegabytes { get; init; }
    public required int RuntimeVisibleSplats { get; init; }
    public required long ProjectionScans { get; init; }
    public required long SortScans { get; init; }
    public required long SortProjectedReads { get; init; }
    public required bool FitsDeviceBudget { get; init; }
    public required float SelectionScore { get; init; }
    public required int RuntimeVertices { get; init; }
}

public static class SplatPackParetoOptimizer
{
    public static IReadOnlyList<SplatPackParetoRow> Sweep(
        GaussianSplatStream stream,
        SplatPackBuildOptions baseOptions,
        SplatPackParetoSweepOptions sweepOptions)
    {
        var rows = new List<SplatPackParetoRow>();
        foreach (int budget in sweepOptions.MaxSplatBudgets.Distinct().Order())
        {
            if (budget <= 0)
            {
                continue;
            }

            foreach (float coverageBoost in sweepOptions.MaxCoverageBoosts.Distinct().Order())
            {
                foreach (float importanceError in sweepOptions.MaxImportanceErrors.Distinct().Order())
                {
                    SplatPackBuildOptions options = CopyOptions(
                        baseOptions,
                        maxSplats: budget,
                        maxCoverageBoost: MathF.Max(1f, coverageBoost),
                        maxImportanceError: Math.Clamp(importanceError, 0f, 0.95f));
                    SplatPackBuildResult result = SplatPackBuilder.BuildWithReport(stream, options);
                    SplatPackBuildReport report = result.Report;
                    int runtimeVisible = Math.Min(report.Counts.EmittedSplats, Math.Max(1, sweepOptions.DeviceBudget.VisibleSplatBudget));
                    long projectionScans = (long)runtimeVisible * Math.Clamp(sweepOptions.DeviceBudget.StereoEyes, 1, 2);
                    long sortScans = projectionScans;
                    long sortProjectedReads = sortScans * 2L;
                    float gpuRuntimeMb = EstimateGpuRuntimeMegabytes(runtimeVisible, sweepOptions.DeviceBudget.StereoEyes);
                    float residentMb = gpuRuntimeMb + report.Memory.TotalUncompressedBytes / (1024f * 1024f) + report.Counts.EmittedSplats * 144f / (1024f * 1024f);
                    var row = new SplatPackParetoRow
                    {
                        MaxSplats = budget,
                        MaxCoverageBoost = options.MaxCoverageBoost,
                        MaxImportanceError = options.MaxImportanceError,
                        EmittedSplats = report.Counts.EmittedSplats,
                        Chunks = report.Chunks.Count,
                        RetainedImportance = report.Optimization.RetainedImportanceFraction,
                        DroppedImportance = report.Optimization.EstimatedDroppedImportanceFraction,
                        MeanCoverageBoost = report.Optimization.MeanCoverageBoost,
                        ObservedMaxCoverageBoost = report.Optimization.MaxCoverageBoost,
                        LodTier0Splats = report.Optimization.LodTier0Splats,
                        LodTier1Splats = report.Optimization.LodTier1Splats,
                        LodTier2Splats = report.Optimization.LodTier2Splats,
                        AxisClampedSplats = report.Counts.AxisClampedSplats,
                        AxisRatioClampedSplats = report.Counts.AxisRatioClampedSplats,
                        QuantizedSplats = report.Optimization.QuantizedSplats,
                        PackageMegabytes = report.Memory.TotalUncompressedBytes / (1024f * 1024f),
                        EstimatedGpuRuntimeMegabytes = gpuRuntimeMb,
                        EstimatedResidentMegabytes = residentMb,
                        RuntimeVisibleSplats = runtimeVisible,
                        ProjectionScans = projectionScans,
                        SortScans = sortScans,
                        SortProjectedReads = sortProjectedReads,
                        FitsDeviceBudget = false,
                        SelectionScore = 0f,
                        RuntimeVertices = report.Memory.RuntimeVertices,
                    };
                    bool fits = sweepOptions.DeviceBudget.Fits(row);
                    rows.Add(row with
                    {
                        FitsDeviceBudget = fits,
                        SelectionScore = Score(row, sweepOptions.DeviceBudget, fits),
                    });
                }
            }
        }

        return rows
            .OrderBy(row => row.MaxSplats)
            .ThenBy(row => row.MaxCoverageBoost)
            .ThenBy(row => row.MaxImportanceError)
            .ToArray();
    }

    public static void WriteCsv(string path, IReadOnlyList<SplatPackParetoRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        var builder = new StringBuilder();
        builder.AppendLine("maxSplats,maxCoverageBoost,maxImportanceError,emittedSplats,chunks,retainedImportance,droppedImportance,meanCoverageBoost,observedMaxCoverageBoost,lodTier0,lodTier1,lodTier2,axisClamped,axisRatioClamped,quantizedSplats,packageMB,estimatedGpuRuntimeMB,estimatedResidentMB,runtimeVisibleSplats,projectionScans,sortScans,sortProjectedReads,fitsDeviceBudget,selectionScore,runtimeVertices");
        foreach (SplatPackParetoRow row in rows)
        {
            builder.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{row.MaxSplats},{row.MaxCoverageBoost:0.####},{row.MaxImportanceError:0.####},{row.EmittedSplats},{row.Chunks},{row.RetainedImportance:0.######},{row.DroppedImportance:0.######},{row.MeanCoverageBoost:0.######},{row.ObservedMaxCoverageBoost:0.######},{row.LodTier0Splats},{row.LodTier1Splats},{row.LodTier2Splats},{row.AxisClampedSplats},{row.AxisRatioClampedSplats},{row.QuantizedSplats},{row.PackageMegabytes:0.######},{row.EstimatedGpuRuntimeMegabytes:0.######},{row.EstimatedResidentMegabytes:0.######},{row.RuntimeVisibleSplats},{row.ProjectionScans},{row.SortScans},{row.SortProjectedReads},{row.FitsDeviceBudget},{row.SelectionScore:0.######},{row.RuntimeVertices}"));
        }

        File.WriteAllText(path, builder.ToString());
    }

    public static SplatPackParetoRow? SelectBest(IReadOnlyList<SplatPackParetoRow> rows)
    {
        SplatPackParetoRow? bestFit = rows
            .Where(row => row.FitsDeviceBudget)
            .OrderByDescending(row => row.SelectionScore)
            .ThenByDescending(row => row.RetainedImportance)
            .ThenBy(row => row.EstimatedResidentMegabytes)
            .FirstOrDefault();
        if (bestFit is not null)
        {
            return bestFit;
        }

        return rows
            .OrderByDescending(row => row.SelectionScore)
            .ThenByDescending(row => row.RetainedImportance)
            .ThenBy(row => row.EstimatedResidentMegabytes)
            .FirstOrDefault();
    }

    private static SplatPackBuildOptions CopyOptions(
        SplatPackBuildOptions source,
        int maxSplats,
        float maxCoverageBoost,
        float maxImportanceError)
    {
        return new SplatPackBuildOptions
        {
            ChunkSize = source.ChunkSize,
            RotationOrder = source.RotationOrder,
            TargetProfile = source.TargetProfile,
            OutputFormat = source.OutputFormat,
            ShMode = source.ShMode,
            FilterMode = source.FilterMode,
            PruningMode = source.PruningMode,
            SortMode = source.SortMode,
            MaxSplats = maxSplats,
            OpacityPruneThreshold = source.OpacityPruneThreshold,
            MaxAxisLength = source.MaxAxisLength,
            MaxAxisLengthPercentile = source.MaxAxisLengthPercentile,
            MaxAxisLengthMultiplier = source.MaxAxisLengthMultiplier,
            MaxAxisRatio = source.MaxAxisRatio,
            ContributionAxisPower = source.ContributionAxisPower,
            ContributionPruneThreshold = source.ContributionPruneThreshold,
            EnableViewSampledImportance = source.EnableViewSampledImportance,
            ViewSampleSource = source.ViewSampleSource,
            ViewSamples = source.ViewSamples,
            MaxImportanceError = maxImportanceError,
            EnableCoverageCompensation = source.EnableCoverageCompensation,
            MaxCoverageBoost = maxCoverageBoost,
            CoverageAxisPower = source.CoverageAxisPower,
            FarLodKeepFraction = source.FarLodKeepFraction,
            MidLodKeepFraction = source.MidLodKeepFraction,
            EnableAttributeQuantization = source.EnableAttributeQuantization,
            PositionQuantizationBits = source.PositionQuantizationBits,
            AxisQuantizationBits = source.AxisQuantizationBits,
            ColorQuantizationBits = source.ColorQuantizationBits,
            OpacityQuantizationBits = source.OpacityQuantizationBits,
            EnableStreamingLayout = source.EnableStreamingLayout,
            EnableSpatialOutlierPrune = source.EnableSpatialOutlierPrune,
            SpatialOutlierGridResolution = source.SpatialOutlierGridResolution,
            SpatialOutlierMinNeighbors = source.SpatialOutlierMinNeighbors,
            SpatialOutlierKeepImportance = source.SpatialOutlierKeepImportance,
            ArtifactRepairMode = source.ArtifactRepairMode,
            ProbeResolution = source.ProbeResolution,
            ProbeViewCount = source.ProbeViewCount,
            ProbeViewSource = source.ProbeViewSource,
            ProbeViewSamples = source.ProbeViewSamples,
            RepairBudgetRatio = source.RepairBudgetRatio,
            RepairIterations = source.RepairIterations,
            ProbeCellSize = source.ProbeCellSize,
            ProbeFovYDegrees = source.ProbeFovYDegrees,
            ProbeAspect = source.ProbeAspect,
            ProbeIpdMeters = source.ProbeIpdMeters,
            ProbeLayerBins = source.ProbeLayerBins,
            PullbackWeights = source.PullbackWeights,
        }.NormalizeForBuild();
    }

    private static float EstimateGpuRuntimeMegabytes(int runtimeVisibleSplats, int stereoEyes)
    {
        int eyes = Math.Clamp(stereoEyes, 1, 2);
        long splatBytes = (long)runtimeVisibleSplats * 144L;
        long projectedBytes = (long)runtimeVisibleSplats * eyes * 80L;
        long drawOrderBytes = (long)runtimeVisibleSplats * sizeof(uint);
        long binBytes = 4096L * sizeof(uint) * 2L;
        long drawArgsBytes = 4L * sizeof(uint);
        return (splatBytes + projectedBytes + drawOrderBytes + binBytes + drawArgsBytes) / (1024f * 1024f);
    }

    private static float Score(SplatPackParetoRow row, SplatPackDeviceBudget budget, bool fits)
    {
        float fitBonus = fits ? 1f : 0f;
        float memoryPressure = row.EstimatedResidentMegabytes / Math.Max(1f, budget.RuntimeMemoryBudgetMB);
        float projectionPressure = row.ProjectionScans / Math.Max(1f, budget.ProjectionScanBudget);
        float visiblePressure = row.RuntimeVisibleSplats / Math.Max(1f, budget.VisibleSplatBudget);
        float coveragePenalty = MathF.Max(0f, row.MeanCoverageBoost - 1f) * 0.05f;
        return fitBonus
               + row.RetainedImportance * 2.0f
               - memoryPressure * 0.35f
               - projectionPressure * 0.25f
               - visiblePressure * 0.15f
               - coveragePenalty;
    }
}
