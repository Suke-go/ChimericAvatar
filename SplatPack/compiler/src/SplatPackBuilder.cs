using System.Numerics;

namespace SplatPack.Compiler;

public static partial class SplatPackBuilder
{
    private const float ShC0 = 0.28209479177387814f;
    private const float MinimumScale = 1e-8f;
    private const int RenderHintSurface = 0;
    private const int RenderHintMicroDetail = 1;
    private const int RenderHintBroadSurface = 2;
    private const int RenderHintFreeSpace = 3;
    private const int RenderHintFloater = 4;
    private const int RenderHintForeground = 5;
    private const int RenderHintLayerRisk = 6;
    private const int ArtifactNone = 0;
    private const int ArtifactCoverage = 1;
    private const int ArtifactEdge = 2;
    private const int ArtifactTexture = 3;
    private const int ArtifactLayer = 4;
    private const int ArtifactStereo = 5;
    private const int LayerNone = 0;
    private const int LayerNear = 1;
    private const int LayerFar = 2;
    private const int RepairActionNone = 0;
    private const int RepairActionBoost = 1;
    private const int RepairActionReinsert = 2;
    private const int RepairActionShPreserve = 3;
    private const int RepairActionQuantRelax = 4;
    private const int RepairActionLodUpgrade = 5;

    private readonly record struct Candidate(
        int SourceIndex,
        float Opacity,
        float MaxAxisLength,
        float AxisRatio,
        float Importance,
        float StructuralImportance,
        float CoverageBoost,
        int BudgetRenderClass,
        int ArtifactClass,
        int LayerId,
        int RepairAction);

    private readonly record struct GridCoord(int X, int Y, int Z);

    private sealed class BudgetCell
    {
        public required long Key { get; init; }
        public required List<Candidate> Candidates { get; init; }
        public required float BestScore { get; init; }
        public required double ImportanceMass { get; init; }
        public int Quota { get; set; }
        public double Remainder { get; set; }
    }

    private sealed class BuildCell
    {
        public required long Key { get; init; }
        public required List<Candidate> Candidates { get; init; }
        public required double ImportanceMass { get; init; }
        public required float BestImportance { get; init; }
    }

    private readonly record struct CompilerOptimizationSummary(
        float InputImportanceMass,
        float EmittedImportanceMass,
        int LodTier0Splats,
        int LodTier1Splats,
        int LodTier2Splats,
        int QuantizedSplats,
        int StreamingChunks,
        float MeanCoverageBoost,
        float MaxCoverageBoost);

    internal readonly record struct ClassDropSummary(
        int DroppedFreeSpace,
        int DroppedFloater,
        int DroppedLayerRisk,
        int ProtectedBySupport,
        float SupportProtectThreshold)
    {
        public int TotalDropped => DroppedFreeSpace + DroppedFloater + DroppedLayerRisk;
        public static readonly ClassDropSummary Empty = new(0, 0, 0, 0, 0f);
    }

    internal readonly record struct FieldPruneSummary(
        int VoxelGrid,
        float PruneFraction,
        int DroppedSplats,
        int RetainedSplats,
        float LowestRetainedScore,
        float HighestDroppedScore,
        float MeanScore,
        int ProtectedByAlpha)
    {
        public static readonly FieldPruneSummary Empty = new(0, 0f, 0, 0, 0f, 0f, 0f, 0);
    }

    internal readonly record struct SplatInjectionSummary(
        int VoxelGrid,
        int InjectedSplats,
        int UnderServedVoxels,
        int DeadVoxels,
        float MeanNeighborCount,
        float DeficitThreshold)
    {
        public static readonly SplatInjectionSummary Empty = new(0, 0, 0, 0, 0f, 0f);
    }

    private sealed class ArtifactRepairSummary
    {
        public static readonly ArtifactRepairSummary Empty = new();

        public string Mode { get; init; } = SplatPackArtifactRepairMode.None.ToString();
        public int ProbeResolution { get; init; }
        public int ProbeViewCount { get; init; }
        public int InitialBudgetSplats { get; init; }
        public int FinalBudgetSplats { get; init; }
        public int ResidualCells { get; init; }
        public int CoverageResidualCells { get; init; }
        public int EdgeResidualCells { get; init; }
        public int TextureResidualCells { get; init; }
        public int LayerResidualCells { get; init; }
        public int StereoResidualCells { get; init; }
        public int ReinsertedCoverageSplats { get; init; }
        public int ReinsertedEdgeSplats { get; init; }
        public int ReinsertedTextureSplats { get; init; }
        public int ReinsertedLayerSplats { get; init; }
        public int ReinsertedStereoSplats { get; init; }
        public int BoostedSplats { get; init; }
        public int BoostSaturatedCells { get; init; }
        public int LodUpgradedSplats { get; init; }
        public int ShPreservedSplats { get; init; }
        public int QuantizationRelaxedSplats { get; init; }
        public float CoverageResidualMean { get; init; }
        public float EdgeResidualMean { get; init; }
        public float TextureResidualMean { get; init; }
        public float LayerResidualMean { get; init; }
        public float StereoResidualMean { get; init; }
        public float MaxAppliedBoost { get; init; } = 1f;
        public float ResidualBefore { get; init; }
        public float ResidualAfter { get; init; }
        public float ResidualReductionRatio { get; init; }
        public int RejectedRepairs { get; init; }
        public float StereoMismatchBefore { get; init; }
        public float StereoMismatchAfter { get; init; }
        public int LayerCrossBoostRejected { get; init; }
        public Vector4[] CellMetadata { get; init; } = Array.Empty<Vector4>();
        public int CellMetadataStride { get; init; } = SplatPackWriter.CellArtifactMetadataStride;
    }

    private sealed class ShellSurfaceRepairSummary
    {
        public static readonly ShellSurfaceRepairSummary Empty = new();

        public bool Enabled { get; init; }
        public string Mode { get; init; } = SplatPackArtifactRepairMode.None.ToString();
        public int InitialSelectedSplats { get; init; }
        public int FinalSelectedSplats { get; init; }
        public int ReinsertedSplats { get; init; }
        public float ResidualBefore { get; init; }
        public float ResidualAfter { get; init; }
        public float ResidualReductionRatio { get; init; }
        public float SurfaceResidualBefore { get; init; }
        public float SurfaceResidualAfter { get; init; }
        public float MicroResidualBefore { get; init; }
        public float MicroResidualAfter { get; init; }
        public float HighBandResidualBefore { get; init; }
        public float HighBandResidualAfter { get; init; }
        public float LowBandExcessBefore { get; init; }
        public float LowBandExcessAfter { get; init; }
        public float VoxelHoleResidualBefore { get; init; }
        public float VoxelHoleResidualAfter { get; init; }
        public int WeakShellCellsBefore { get; init; }
        public int WeakShellCellsAfter { get; init; }
    }

    public static SplatPackPackage Build(GaussianSplatStream stream, int targetChunkSize)
    {
        return Build(stream, new SplatPackBuildOptions { ChunkSize = targetChunkSize });
    }

    public static SplatPackPackage Build(GaussianSplatStream stream, SplatPackBuildOptions options)
    {
        return BuildWithReport(stream, options).Package;
    }

    public static SplatPackBuildResult BuildWithReport(GaussianSplatStream stream, SplatPackBuildOptions options)
    {
        options = options.NormalizeForBuild();

        if (stream.Count <= 0)
        {
            var emptyPackage = new SplatPackPackage
            {
                Bounds = new SplatPackBounds(Vector3.Zero, Vector3.Zero),
                Chunks = Array.Empty<SplatPackChunk>(),
                Splats = Array.Empty<SplatPackSplat>(),
            };
            return new SplatPackBuildResult
            {
                Package = emptyPackage,
                Report = BuildReport(emptyPackage, options, SplatPackRotationOrder.WXYZ, 0f, 0, 0, 0, 0, 0, 0, 0, Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), new CompilerOptimizationSummary(0f, 0f, 0, 0, 0, 0, 0, 1f, 1f)),
            };
        }

        int targetChunkSize = Math.Max(1, options.ChunkSize);
        SplatPackRotationOrder rotationOrder = ResolveRotationOrder(stream, options.RotationOrder);
        Candidate[] candidates = SelectCandidates(
            stream,
            options,
            out int prunedInvalid,
            out int prunedLowOpacity,
            out float[] rawOpacities,
            out float[] rawMaxAxisLengths,
            out float[] rawAxisRatios);
        int prunedLowContribution = 0;
        int prunedSpatialOutliers = 0;
        int prunedBudget = 0;
        ArtifactRepairSummary artifactRepair = ArtifactRepairSummary.Empty;
        ShellSurfaceRepairSummary shellSurfaceRepair = ShellSurfaceRepairSummary.Empty;

        if (candidates.Length <= 0)
        {
            var emptyPackage = new SplatPackPackage
            {
                Bounds = new SplatPackBounds(Vector3.Zero, Vector3.Zero),
                Chunks = Array.Empty<SplatPackChunk>(),
                Splats = Array.Empty<SplatPackSplat>(),
            };
            return new SplatPackBuildResult
            {
                Package = emptyPackage,
                Report = BuildReport(emptyPackage, options, rotationOrder, 0f, prunedInvalid, prunedLowOpacity, prunedLowContribution, prunedSpatialOutliers, prunedBudget, 0, 0, rawOpacities, rawMaxAxisLengths, rawAxisRatios, Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), new CompilerOptimizationSummary(0f, 0f, 0, 0, 0, 0, 0, 1f, 1f)),
            };
        }

        float axisLengthCap = ResolveAxisLengthCap(candidates, options);
        candidates = AssignViewSampledImportance(stream, candidates, options, axisLengthCap);
        candidates = AssignBudgetRenderClasses(stream, candidates, options, axisLengthCap);
        Candidate[] sphericalSourceCandidates = candidates;
        float inputImportanceMass = ImportanceMass(candidates);
        candidates = PruneLowContribution(candidates, options, axisLengthCap, out prunedLowContribution);
        candidates = PruneSpatialOutliers(stream, candidates, options, out prunedSpatialOutliers);
        if (candidates.Length <= 0)
        {
            var emptyPackage = new SplatPackPackage
            {
                Bounds = new SplatPackBounds(Vector3.Zero, Vector3.Zero),
                Chunks = Array.Empty<SplatPackChunk>(),
                Splats = Array.Empty<SplatPackSplat>(),
            };
            return new SplatPackBuildResult
            {
                Package = emptyPackage,
                Report = BuildReport(emptyPackage, options, rotationOrder, axisLengthCap, prunedInvalid, prunedLowOpacity, prunedLowContribution, prunedSpatialOutliers, prunedBudget, 0, 0, rawOpacities, rawMaxAxisLengths, rawAxisRatios, Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), new CompilerOptimizationSummary(inputImportanceMass, 0f, 0, 0, 0, 0, 0, 1f, 1f)),
            };
        }

        candidates = ApplySplatBudget(stream, candidates, sphericalSourceCandidates, options, axisLengthCap, rotationOrder, out prunedBudget, out artifactRepair, out shellSurfaceRepair);
        SplatPackBounds bounds = ComputeBounds(stream, candidates);
        int gridResolution = Math.Max(1, (int)Math.Ceiling(Math.Pow(candidates.Length / (double)targetChunkSize, 1.0 / 3.0)));

        var splats = new SplatPackSplat[candidates.Length];
        var chunks = new List<SplatPackChunk>();
        var emittedOpacities = new float[candidates.Length];
        var emittedMaxAxisLengths = new float[candidates.Length];
        var emittedAxisRatios = new float[candidates.Length];
        int axisClampedSplats = 0;
        int axisRatioClampedSplats = 0;
        int lodTier0Splats = 0;
        int lodTier1Splats = 0;
        int lodTier2Splats = 0;
        int quantizedSplats = 0;

        List<BuildCell> buildCells = BuildStreamingCells(stream, candidates, bounds, gridResolution, options);
        int cursor = 0;
        foreach (BuildCell cell in buildCells)
        {
            int cellCursor = 0;
            while (cellCursor < cell.Candidates.Count)
            {
                int start = cursor;
                int count = Math.Min(targetChunkSize, cell.Candidates.Count - cellCursor);
                for (int i = 0; i < count; i++)
                {
                    Candidate candidate = cell.Candidates[cellCursor + i];
                    int lodTier = ResolveLodTier(i, count, options);
                    splats[cursor] = ConvertSplat(
                        stream,
                        candidate.SourceIndex,
                        rotationOrder,
                        axisLengthCap,
                        options.MaxAxisRatio,
                        bounds,
                        options,
                        ShouldUpgradeLod(candidate) ? Math.Max(0, lodTier - 1) : lodTier,
                        candidate.Importance,
                        candidate.CoverageBoost,
                        candidate.BudgetRenderClass,
                        candidate.ArtifactClass,
                        candidate.RepairAction,
                        out bool axisClamped,
                        out bool axisRatioClamped,
                        out bool quantized,
                        out float emittedMaxAxisLength,
                        out float emittedAxisRatio);
                    emittedOpacities[cursor] = splats[cursor].Color.W;
                    emittedMaxAxisLengths[cursor] = emittedMaxAxisLength;
                    emittedAxisRatios[cursor] = emittedAxisRatio;
                    if (axisClamped)
                    {
                        axisClampedSplats++;
                    }

                    if (axisRatioClamped)
                    {
                        axisRatioClampedSplats++;
                    }

                    if (quantized)
                    {
                        quantizedSplats++;
                    }

                    if (lodTier == 0)
                    {
                        lodTier0Splats++;
                    }
                    else if (lodTier == 1)
                    {
                        lodTier1Splats++;
                    }
                    else
                    {
                        lodTier2Splats++;
                    }

                    cursor++;
                }

                chunks.Add(BuildChunk(splats, start, count, ResolveChunkLodLevel(cell, buildCells)));
                cellCursor += count;
            }
        }

        Vector4[] sh3Coefficients = BuildSh3Coefficients(stream, splats, options.ShMode);
        Vector4[] researchMetadata = BuildResearchMetadata(splats, emittedMaxAxisLengths, emittedAxisRatios);
        Vector4[] artifactRepairMetadata = BuildArtifactRepairMetadata(candidates);
        SplatPackSupportMetadata[] supportMetadata = BuildSupportMetadata(splats, bounds, emittedMaxAxisLengths, emittedAxisRatios);
        var packageWithoutSupportField = new SplatPackPackage
        {
            Bounds = bounds,
            Chunks = chunks.ToArray(),
            Splats = splats,
            Sh3Coefficients = sh3Coefficients,
            ResearchMetadata = researchMetadata,
            ArtifactRepairMetadata = artifactRepairMetadata,
            CellArtifactMetadata = artifactRepair.CellMetadata,
            CellArtifactMetadataStride = artifactRepair.CellMetadataStride,
            SupportMetadata = supportMetadata,
        };
        Vector4[] sphericalSupportField = BuildSphericalSupportField(
            stream,
            sphericalSourceCandidates,
            packageWithoutSupportField,
            options,
            rotationOrder,
            axisLengthCap);
        var package = new SplatPackPackage
        {
            Bounds = bounds,
            Chunks = chunks.ToArray(),
            Splats = splats,
            Sh3Coefficients = sh3Coefficients,
            ResearchMetadata = researchMetadata,
            ArtifactRepairMetadata = artifactRepairMetadata,
            CellArtifactMetadata = artifactRepair.CellMetadata,
            CellArtifactMetadataStride = artifactRepair.CellMetadataStride,
            SupportMetadata = supportMetadata,
            SphericalSupportField = sphericalSupportField,
            SphericalSupportFieldStride = sphericalSupportField.Length > 0
                ? SplatPackWriter.SphericalSupportFieldV1Stride
                : SplatPackWriter.SphericalSupportFieldStride,
        };
        ClassDropSummary classDrop = ApplyClassDrop(ref package, options);
        FieldPruneSummary fieldPrune = options.EnableFieldAwarePruning
            ? ApplyFieldAwarePruning(ref package, options)
            : FieldPruneSummary.Empty;
        SplatInjectionSummary injection = SplatInjectionSummary.Empty;
        bool injectionGatedByClassifier = false;
        if (options.EnableSplatInjection)
        {
            if (options.EnableSceneClassifier && !IsObjectCentric(package, options.SceneClassifierCoreFraction))
            {
                injectionGatedByClassifier = true;
            }
        }
        if (options.EnableSplatInjection && !injectionGatedByClassifier)
        {
            int passes = Math.Clamp(options.InjectionPasses, 1, 8);
            int initialCount = package.Splats.Length;
            int totalBudget = (int)MathF.Floor(initialCount * Math.Clamp(options.InjectionMaxFraction, 0f, 1f));
            int totalInjected = 0;
            SplatInjectionSummary lastNonEmpty = SplatInjectionSummary.Empty;
            // EXPERIMENTAL. Naive multi-pass diverges in practice: pass 2's
            // density grid sees pass 1's injected splats as newly populated but
            // sub-threshold-dense voxels, and re-injects nearby, fragmenting
            // the spectral fidelity score. Default passes=1 reproduces the
            // single-pass behaviour reported in the paper. Kept as an option
            // for follow-up research on principled iterative refinement.
            for (int p = 0; p < passes; p++)
            {
                int remaining = totalBudget - totalInjected;
                if (remaining <= 0) break;
                SplatInjectionSummary pass = ApplySplatInjection(ref package, options, hardBudgetOverride: remaining);
                totalInjected += pass.InjectedSplats;
                lastNonEmpty = pass;
                if (pass.InjectedSplats == 0)
                {
                    // Converged: nothing further to inject under the current
                    // density grid + deficit threshold.
                    break;
                }
            }
            injection = lastNonEmpty with { InjectedSplats = totalInjected };
        }
        SplatPackSphericalInformationStats sphericalInformation = BuildSphericalInformationStats(
            stream,
            sphericalSourceCandidates,
            package,
            options,
            rotationOrder,
            axisLengthCap);
        return new SplatPackBuildResult
        {
            Package = package,
            Report = BuildReport(
                package,
                options,
                rotationOrder,
                axisLengthCap,
                prunedInvalid,
                prunedLowOpacity,
                prunedLowContribution,
                prunedSpatialOutliers,
                prunedBudget,
                axisClampedSplats,
                axisRatioClampedSplats,
                rawOpacities,
                rawMaxAxisLengths,
                rawAxisRatios,
                emittedOpacities,
                emittedMaxAxisLengths,
                emittedAxisRatios,
                new CompilerOptimizationSummary(
                    inputImportanceMass,
                    ImportanceMass(candidates),
                    lodTier0Splats,
                    lodTier1Splats,
                    lodTier2Splats,
                    quantizedSplats,
                    chunks.Count,
                    MeanCoverageBoost(candidates),
                    MaxCoverageBoost(candidates)),
                artifactRepair,
                shellSurfaceRepair,
                sphericalInformation,
                options.EnableSplatInjection
                    ? new SplatPackSplatInjectionReport
                    {
                        Enabled = true,
                        VoxelGrid = injection.VoxelGrid,
                        InjectedSplats = injection.InjectedSplats,
                        UnderServedVoxels = injection.UnderServedVoxels,
                        DeadVoxels = injection.DeadVoxels,
                        MeanNeighborCount = injection.MeanNeighborCount,
                        DeficitThreshold = injection.DeficitThreshold,
                    }
                    : SplatPackSplatInjectionReport.None),
            ClassDrop = new SplatPackClassDropReport
            {
                DroppedFreeSpace = classDrop.DroppedFreeSpace,
                DroppedFloater = classDrop.DroppedFloater,
                DroppedLayerRisk = classDrop.DroppedLayerRisk,
                ProtectedBySupport = classDrop.ProtectedBySupport,
                SupportProtectThreshold = classDrop.SupportProtectThreshold,
            },
            FieldPrune = options.EnableFieldAwarePruning
                ? new SplatPackFieldPruneReport
                {
                    Enabled = true,
                    VoxelGrid = fieldPrune.VoxelGrid,
                    PruneFraction = fieldPrune.PruneFraction,
                    DroppedSplats = fieldPrune.DroppedSplats,
                    RetainedSplats = fieldPrune.RetainedSplats,
                    LowestRetainedScore = fieldPrune.LowestRetainedScore,
                    HighestDroppedScore = fieldPrune.HighestDroppedScore,
                    MeanScore = fieldPrune.MeanScore,
                    ProtectedByAlpha = fieldPrune.ProtectedByAlpha,
                }
                : SplatPackFieldPruneReport.None,
            Injection = options.EnableSplatInjection
                ? new SplatPackSplatInjectionReport
                {
                    Enabled = true,
                    VoxelGrid = injection.VoxelGrid,
                    InjectedSplats = injection.InjectedSplats,
                    UnderServedVoxels = injection.UnderServedVoxels,
                    DeadVoxels = injection.DeadVoxels,
                    MeanNeighborCount = injection.MeanNeighborCount,
                    DeficitThreshold = injection.DeficitThreshold,
                }
                : SplatPackSplatInjectionReport.None,
        };
    }

    private static Candidate[] SelectCandidates(
        GaussianSplatStream stream,
        SplatPackBuildOptions options,
        out int prunedInvalid,
        out int prunedLowOpacity,
        out float[] rawOpacities,
        out float[] rawMaxAxisLengths,
        out float[] rawAxisRatios)
    {
        float opacityPruneThreshold = MathF.Max(0f, options.OpacityPruneThreshold);
        var candidates = new List<Candidate>(stream.Count);
        var opacityStats = new List<float>(stream.Count);
        var axisStats = new List<float>(stream.Count);
        var axisRatioStats = new List<float>(stream.Count);
        prunedInvalid = 0;
        prunedLowOpacity = 0;

        for (int i = 0; i < stream.Count; i++)
        {
            if (!TryInspectRawSplat(stream, i, out float opacity, out float maxAxisLength, out float axisRatio))
            {
                prunedInvalid++;
                continue;
            }

            opacityStats.Add(opacity);
            axisStats.Add(maxAxisLength);
            axisRatioStats.Add(axisRatio);
            if (opacity < opacityPruneThreshold)
            {
                prunedLowOpacity++;
                continue;
            }

            candidates.Add(new Candidate(i, opacity, maxAxisLength, axisRatio, 0f, 0f, 1f, RenderHintSurface, ArtifactNone, LayerNone, RepairActionNone));
        }

        rawOpacities = opacityStats.ToArray();
        rawMaxAxisLengths = axisStats.ToArray();
        rawAxisRatios = axisRatioStats.ToArray();
        return candidates.ToArray();
    }

    private static Candidate[] PruneLowContribution(
        Candidate[] candidates,
        SplatPackBuildOptions options,
        float axisLengthCap,
        out int prunedLowContribution)
    {
        float threshold = MathF.Max(0f, options.ContributionPruneThreshold);
        if (threshold <= 0f || candidates.Length == 0)
        {
            prunedLowContribution = 0;
            return candidates;
        }

        float axisPower = Math.Clamp(options.ContributionAxisPower, 0f, 2f);
        var selected = new List<Candidate>(candidates.Length);
        Candidate best = candidates[0];
        float bestScore = ContributionPruneScore(best, axisLengthCap, axisPower, options.PruningMode);
        foreach (Candidate candidate in candidates)
        {
            float score = ContributionPruneScore(candidate, axisLengthCap, axisPower, options.PruningMode);
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }

            if (!IsUnsupportedGiantAxisCandidate(candidate, axisLengthCap, options)
                && (score >= threshold || IsResearchBudgetProtectedClass(candidate.BudgetRenderClass)))
            {
                selected.Add(candidate);
            }
        }

        if (selected.Count == 0)
        {
            selected.Add(best);
        }

        prunedLowContribution = candidates.Length - selected.Count;
        return selected.ToArray();
    }

    private static bool IsUnsupportedGiantAxisCandidate(
        Candidate candidate,
        float axisLengthCap,
        SplatPackBuildOptions options)
    {
        if (options.TargetProfile != SplatPackTargetProfile.StandaloneXr
            || options.PruningMode != SplatPackPruningMode.ResearchXrSensitivity
            || axisLengthCap <= MinimumScale)
        {
            return false;
        }

        float rawAxisRatio = candidate.MaxAxisLength / MathF.Max(MinimumScale, axisLengthCap);
        return rawAxisRatio > 12f;
    }

    private static Candidate[] PruneSpatialOutliers(
        GaussianSplatStream stream,
        Candidate[] candidates,
        SplatPackBuildOptions options,
        out int prunedSpatialOutliers)
    {
        prunedSpatialOutliers = 0;
        if (!options.EnableSpatialOutlierPrune || candidates.Length < 4096)
        {
            return candidates;
        }

        int gridResolution = ResolveSpatialOutlierGridResolution(candidates.Length, options);
        int minNeighbors = Math.Max(1, options.SpatialOutlierMinNeighbors);
        float keepImportance = Math.Clamp(options.SpatialOutlierKeepImportance, 0f, 1f);
        SplatPackBounds bounds = ComputeBounds(stream, candidates);
        var occupancy = new Dictionary<long, int>(candidates.Length / 4);
        var coords = new GridCoord[candidates.Length];
        for (int i = 0; i < candidates.Length; i++)
        {
            GridCoord coord = GridCoordFor(stream, bounds, gridResolution, candidates[i].SourceIndex);
            coords[i] = coord;
            long key = GridKey(coord, gridResolution);
            occupancy[key] = occupancy.GetValueOrDefault(key) + 1;
        }

        var selected = new List<Candidate>(candidates.Length);
        for (int i = 0; i < candidates.Length; i++)
        {
            Candidate candidate = candidates[i];
            if (candidate.Importance >= keepImportance)
            {
                selected.Add(candidate);
                continue;
            }

            int neighborCount = CountOccupiedNeighborSplats(occupancy, coords[i], gridResolution);
            if (neighborCount >= minNeighbors)
            {
                selected.Add(candidate);
            }
        }

        if (selected.Count == 0)
        {
            prunedSpatialOutliers = 0;
            return candidates;
        }

        prunedSpatialOutliers = candidates.Length - selected.Count;
        return selected.ToArray();
    }

    private static Candidate[] AssignViewSampledImportance(
        GaussianSplatStream stream,
        Candidate[] candidates,
        SplatPackBuildOptions options,
        float axisLengthCap)
    {
        if (candidates.Length == 0)
        {
            return candidates;
        }

        float axisPower = Math.Clamp(options.ContributionAxisPower, 0f, 2f);
        if (!options.EnableViewSampledImportance)
        {
            return AssignFallbackImportance(candidates, axisLengthCap, axisPower);
        }

        SplatPackBounds bounds = ComputeBounds(stream, candidates);
        Vector3 center = (bounds.Min + bounds.Max) * 0.5f;
        float radius = MathF.Max(0.01f, (bounds.Max - bounds.Min).Length() * 0.5f);
        SplatPackViewSample[] views = options.ViewSamples.Length > 0
            ? options.ViewSamples
            : CreateDefaultViewSamples(center, radius);

        var values = new float[candidates.Length];
        float maxImportance = MinimumScale;
        for (int i = 0; i < candidates.Length; i++)
        {
            Candidate candidate = candidates[i];
            Vector3 p = Position(stream, candidate.SourceIndex);
            Vector3 rel = p - center;
            float maxAxis = axisLengthCap > 0f ? MathF.Min(candidate.MaxAxisLength, axisLengthCap) : candidate.MaxAxisLength;
            float axisTerm = axisPower <= 0f ? 1f : MathF.Pow(MathF.Max(MinimumScale, maxAxis), axisPower);
            float projected = 0f;
            float totalWeight = 0f;
            foreach (SplatPackViewSample view in views)
            {
                Vector3 cameraToPoint = p - view.Position;
                float depth = Vector3.Dot(cameraToPoint, view.Forward);
                if (depth <= 0.05f)
                {
                    continue;
                }

                Vector3 lateralVector = cameraToPoint - view.Forward * depth;
                float centrality = 1f / (1f + MathF.Max(0f, lateralVector.Length()) / radius);
                float weight = MathF.Max(0f, view.Weight);
                projected += weight * centrality / (depth * depth);
                totalWeight += weight;
            }

            float importance = totalWeight > 1e-8f
                ? candidate.Opacity * axisTerm * projected / totalWeight
                : ContributionScore(candidate, axisLengthCap, axisPower);
            values[i] = importance;
            maxImportance = MathF.Max(maxImportance, importance);
        }

        if (maxImportance <= MinimumScale * 2f)
        {
            return AssignFallbackImportance(candidates, axisLengthCap, axisPower);
        }

        var assigned = new Candidate[candidates.Length];
        for (int i = 0; i < candidates.Length; i++)
        {
            assigned[i] = candidates[i] with { Importance = Math.Clamp(values[i] / maxImportance, 0f, 1f) };
        }

        return assigned;
    }

    private static Candidate[] AssignFallbackImportance(Candidate[] candidates, float axisLengthCap, float axisPower)
    {
        var fallback = new Candidate[candidates.Length];
        float maxScore = MinimumScale;
        for (int i = 0; i < candidates.Length; i++)
        {
            maxScore = MathF.Max(maxScore, ContributionScore(candidates[i], axisLengthCap, axisPower));
        }

        for (int i = 0; i < candidates.Length; i++)
        {
            float importance = ContributionScore(candidates[i], axisLengthCap, axisPower) / maxScore;
            fallback[i] = candidates[i] with { Importance = importance };
        }

        return fallback;
    }

    private static Candidate[] AssignBudgetRenderClasses(
        GaussianSplatStream stream,
        Candidate[] candidates,
        SplatPackBuildOptions options,
        float axisLengthCap)
    {
        if (candidates.Length == 0)
        {
            return candidates;
        }

        float axisPower = Math.Clamp(options.ContributionAxisPower, 0f, 2f);
        float maxStructuralImportance = MinimumScale;
        for (int i = 0; i < candidates.Length; i++)
        {
            maxStructuralImportance = MathF.Max(maxStructuralImportance, RawContributionScore(candidates[i], axisLengthCap, axisPower));
        }

        var assigned = new Candidate[candidates.Length];
        for (int i = 0; i < candidates.Length; i++)
        {
            Candidate candidate = candidates[i];
            Vector3 rgb = ReadShDcRgb(stream, candidate.SourceIndex);
            float structuralImportance = Math.Clamp(RawContributionScore(candidate, axisLengthCap, axisPower) / maxStructuralImportance, 0f, 1f);
            int renderClass = ClassifyRenderHint(
                rgb,
                candidate.Opacity,
                candidate.MaxAxisLength,
                candidate.AxisRatio,
                axisLengthCap,
                structuralImportance,
                candidate.CoverageBoost,
                0,
                candidate.ArtifactClass,
                candidate.RepairAction);
            assigned[i] = candidate with
            {
                StructuralImportance = structuralImportance,
                BudgetRenderClass = renderClass,
            };
        }

        return assigned;
    }

    private static SplatPackViewSample[] CreateDefaultViewSamples(Vector3 center, float radius)
    {
        float cameraDistance = MathF.Max(0.25f, radius * 2.0f);
        Vector3[] directions =
        [
            Vector3.UnitX,
            -Vector3.UnitX,
            Vector3.UnitY,
            -Vector3.UnitY,
            Vector3.UnitZ,
            -Vector3.UnitZ,
            Vector3.Normalize(new Vector3(1f, 1f, 1f)),
            Vector3.Normalize(new Vector3(-1f, 1f, -1f)),
        ];

        var views = new SplatPackViewSample[directions.Length];
        for (int i = 0; i < directions.Length; i++)
        {
            Vector3 forward = -directions[i];
            views[i] = new SplatPackViewSample(center + directions[i] * cameraDistance, forward, 1f);
        }

        return views;
    }

    private static Candidate[] ApplySplatBudget(
        GaussianSplatStream stream,
        Candidate[] candidates,
        Candidate[] supportSourceCandidates,
        SplatPackBuildOptions options,
        float axisLengthCap,
        SplatPackRotationOrder rotationOrder,
        out int prunedBudget,
        out ArtifactRepairSummary artifactRepair,
        out ShellSurfaceRepairSummary shellSurfaceRepair)
    {
        artifactRepair = ArtifactRepairSummary.Empty;
        shellSurfaceRepair = ShellSurfaceRepairSummary.Empty;
        int finalMaxSplats = Math.Max(0, options.MaxSplats);
        if (finalMaxSplats == 0 || candidates.Length <= finalMaxSplats)
        {
            prunedBudget = 0;
            return candidates;
        }

        finalMaxSplats = ResolveErrorBoundedBudget(candidates, finalMaxSplats, options.MaxImportanceError);
        int maxSplats = ResolvePreInjectionBudget(finalMaxSplats, options);

        float axisPower = Math.Clamp(options.ContributionAxisPower, 0f, 2f);
        SplatPackBounds bounds = ComputeBounds(stream, candidates);
        int targetChunkSize = Math.Max(1, options.ChunkSize);
        bool reserveRepairBudget = IsGaussianPullbackRepairEnabled(options) || IsShellSurfaceRepairEnabled(options);
        int selectionBudget = reserveRepairBudget
            ? Math.Clamp((int)MathF.Floor(maxSplats * (1f - Math.Clamp(options.RepairBudgetRatio, 0.01f, 0.5f))), 1, maxSplats)
            : maxSplats;
        int gridResolution = ResolveBudgetGridResolution(selectionBudget, targetChunkSize, candidates.Length, options.PruningMode);

        var cells = new Dictionary<long, List<Candidate>>();
        foreach (Candidate candidate in candidates)
        {
            long key = GridKey(stream, bounds, gridResolution, candidate.SourceIndex);
            if (!cells.TryGetValue(key, out List<Candidate>? cell))
            {
                cell = new List<Candidate>();
                cells.Add(key, cell);
            }

            cell.Add(candidate);
        }

        var budgetCells = new List<BudgetCell>(cells.Count);
        long[] orderedKeys = cells.Keys.ToArray();
        Array.Sort(orderedKeys);

        foreach (long key in orderedKeys)
        {
            List<Candidate> cellCandidates = cells[key];
            cellCandidates.Sort((a, b) => CompareByBudgetPriority(a, b, axisLengthCap, axisPower, options.PruningMode));
            budgetCells.Add(new BudgetCell
            {
                Key = key,
                Candidates = cellCandidates,
                BestScore = BudgetPriorityScore(cellCandidates[0], axisLengthCap, axisPower, options.PruningMode),
                ImportanceMass = cellCandidates.Sum(candidate => Math.Max(0.0, BudgetQuotaMass(candidate, axisLengthCap, axisPower, options.PruningMode))),
            });
        }

        AllocateSpatialQuotas(budgetCells, candidates.Length, selectionBudget);

        var selected = new List<Candidate>(selectionBudget);
        var selectedSourceIndices = new HashSet<int>();
        foreach (BudgetCell cell in budgetCells)
        {
            int quota = Math.Clamp(cell.Quota, 0, cell.Candidates.Count);
            for (int i = 0; i < quota; i++)
            {
                if (selected.Count == selectionBudget)
                {
                    break;
                }

                Candidate candidate = cell.Candidates[i];
                selected.Add(candidate);
                selectedSourceIndices.Add(candidate.SourceIndex);
            }
        }

        if (selected.Count < maxSplats)
        {
            var ranked = (Candidate[])candidates.Clone();
            Array.Sort(ranked, (a, b) => CompareByBudgetPriority(a, b, axisLengthCap, axisPower, options.PruningMode));
            foreach (Candidate candidate in ranked)
            {
                if (selected.Count == selectionBudget)
                {
                    break;
                }

                if (selectedSourceIndices.Add(candidate.SourceIndex))
                {
                    selected.Add(candidate);
                }
            }
        }

        selected = ApplyAngularSupportBudgetRepair(
            stream,
            supportSourceCandidates,
            selected,
            bounds,
            axisLengthCap,
            rotationOrder,
            options,
            selectionBudget);

        if (IsGaussianPullbackRepairEnabled(options))
        {
            selected = ApplyGaussianPullbackRepair(
                stream,
                candidates,
                selected,
                bounds,
                axisLengthCap,
                rotationOrder,
                options,
                maxSplats,
                out artifactRepair);
            selected = ApplyAngularSupportBudgetRepair(
                stream,
                supportSourceCandidates,
                selected,
                bounds,
                axisLengthCap,
                rotationOrder,
                options,
                maxSplats);
            prunedBudget = candidates.Length - selected.Count;
            return selected.ToArray();
        }

        if (IsShellSurfaceRepairEnabled(options))
        {
            selected = ApplyShellSurfaceRepair(
                stream,
                supportSourceCandidates,
                selected,
                bounds,
                axisLengthCap,
                rotationOrder,
                options,
                maxSplats,
                out shellSurfaceRepair);
            selected = ApplyAngularSupportBudgetRepair(
                stream,
                supportSourceCandidates,
                selected,
                bounds,
                axisLengthCap,
                rotationOrder,
                options,
                maxSplats);
            if (selected.Count < maxSplats)
            {
                var shellSelectedSourceIndices = new HashSet<int>(selected.Select(static candidate => candidate.SourceIndex));
                var ranked = (Candidate[])candidates.Clone();
                Array.Sort(ranked, (a, b) => CompareByBudgetPriority(a, b, axisLengthCap, axisPower, options.PruningMode));
                foreach (Candidate candidate in ranked)
                {
                    if (selected.Count >= maxSplats)
                    {
                        break;
                    }

                    if (shellSelectedSourceIndices.Add(candidate.SourceIndex))
                    {
                        selected.Add(candidate);
                    }
                }

                selected = ApplyAngularSupportBudgetRepair(
                    stream,
                    supportSourceCandidates,
                    selected,
                    bounds,
                    axisLengthCap,
                    rotationOrder,
                    options,
                    maxSplats);
            }

            prunedBudget = candidates.Length - selected.Count;
            return ApplyCoverageCompensation(stream, candidates, selected, bounds, gridResolution, axisLengthCap, options);
        }

        selected = ApplyAngularSupportBudgetRepair(
            stream,
            supportSourceCandidates,
            selected,
            bounds,
            axisLengthCap,
            rotationOrder,
            options,
            maxSplats);
        prunedBudget = candidates.Length - selected.Count;
        return ApplyCoverageCompensation(stream, candidates, selected, bounds, gridResolution, axisLengthCap, options);
    }

    private static Candidate[] ApplyCoverageCompensation(
        GaussianSplatStream stream,
        Candidate[] allCandidates,
        List<Candidate> selected,
        SplatPackBounds bounds,
        int gridResolution,
        float axisLengthCap,
        SplatPackBuildOptions options)
    {
        if (!options.EnableCoverageCompensation || selected.Count == 0)
        {
            return selected.ToArray();
        }

        float maxBoost = MathF.Max(1f, options.MaxCoverageBoost);
        float axisPower = Math.Clamp(options.CoverageAxisPower, 0f, 2f);
        var sourceCoverage = new Dictionary<long, double>();
        foreach (Candidate candidate in allCandidates)
        {
            long key = GridKey(stream, bounds, gridResolution, candidate.SourceIndex);
            sourceCoverage[key] = sourceCoverage.GetValueOrDefault(key) + CoverageScore(candidate, axisLengthCap, axisPower);
        }

        var selectedCoverage = new Dictionary<long, double>();
        foreach (Candidate candidate in selected)
        {
            long key = GridKey(stream, bounds, gridResolution, candidate.SourceIndex);
            selectedCoverage[key] = selectedCoverage.GetValueOrDefault(key) + CoverageScore(candidate, axisLengthCap, axisPower);
        }

        var compensated = new Candidate[selected.Count];
        for (int i = 0; i < selected.Count; i++)
        {
            Candidate candidate = selected[i];
            long key = GridKey(stream, bounds, gridResolution, candidate.SourceIndex);
            double selectedMass = Math.Max(1e-12, selectedCoverage.GetValueOrDefault(key));
            double sourceMass = Math.Max(selectedMass, sourceCoverage.GetValueOrDefault(key));
            bool hazeClass = candidate.BudgetRenderClass == RenderHintFreeSpace
                             || candidate.BudgetRenderClass == RenderHintFloater
                             || candidate.BudgetRenderClass == RenderHintBroadSurface
                             || candidate.BudgetRenderClass == RenderHintLayerRisk;
            float boost = options.ArtifactRepairMode == SplatPackArtifactRepairMode.ShellSurfaceV1 && hazeClass
                ? 1f
                : Math.Clamp((float)(sourceMass / selectedMass), 1f, maxBoost);
            compensated[i] = candidate with { CoverageBoost = boost };
        }

        return compensated;
    }

    private static int ResolveErrorBoundedBudget(Candidate[] candidates, int maxSplats, float maxImportanceError)
    {
        float error = Math.Clamp(maxImportanceError, 0f, 0.95f);
        if (error <= 0f || candidates.Length <= maxSplats)
        {
            return maxSplats;
        }

        double total = candidates.Sum(candidate => Math.Max(0.0, candidate.Importance));
        if (total <= 1e-12)
        {
            return maxSplats;
        }

        var ranked = (Candidate[])candidates.Clone();
        Array.Sort(ranked, (a, b) => b.Importance.CompareTo(a.Importance));
        double retained = 0.0;
        double target = total * (1.0 - error);
        for (int i = 0; i < ranked.Length; i++)
        {
            retained += Math.Max(0.0, ranked[i].Importance);
            if (retained >= target)
            {
                return Math.Min(maxSplats, Math.Max(1, i + 1));
            }
        }

        return maxSplats;
    }

    private static int ResolvePreInjectionBudget(int finalMaxSplats, SplatPackBuildOptions options)
    {
        if (!options.EnableSplatInjection || finalMaxSplats <= 0)
        {
            return finalMaxSplats;
        }

        if (options.InjectionTargetCount > 0)
        {
            return Math.Clamp(finalMaxSplats - options.InjectionTargetCount, 1, finalMaxSplats);
        }

        float fraction = Math.Clamp(options.InjectionMaxFraction, 0f, 1f);
        if (fraction <= 0f)
        {
            return finalMaxSplats;
        }

        return Math.Clamp((int)MathF.Floor(finalMaxSplats / (1f + fraction)), 1, finalMaxSplats);
    }

    private static void AllocateSpatialQuotas(List<BudgetCell> cells, int candidateCount, int maxSplats)
    {
        int minimumQuota = cells.Count <= maxSplats ? 1 : 0;
        int quotaTotal = minimumQuota * cells.Count;
        int remainingBudget = maxSplats - quotaTotal;
        double remainingMass = cells.Sum(cell => Math.Max(0.0, cell.ImportanceMass));

        // If the budget can touch every occupied cell, reserve one splat per cell.
        // Otherwise some cells must receive zero; the largest-remainder pass below
        // chooses them deterministically by density, contribution, then grid key.
        foreach (BudgetCell cell in cells)
        {
            cell.Quota = minimumQuota;
            cell.Remainder = 0.0;

            int capacity = Math.Max(0, cell.Candidates.Count - cell.Quota);
            if (remainingBudget <= 0 || remainingMass <= 1e-12 || capacity == 0)
            {
                continue;
            }

            double exactAdditionalQuota = remainingBudget * Math.Max(0.0, cell.ImportanceMass) / remainingMass;
            int additionalQuota = Math.Min(capacity, (int)Math.Floor(exactAdditionalQuota));
            cell.Quota += additionalQuota;
            quotaTotal += additionalQuota;
            cell.Remainder = exactAdditionalQuota - additionalQuota;
        }

        int toAdd = maxSplats - quotaTotal;
        if (toAdd <= 0)
        {
            return;
        }

        var fillOrder = cells
            .Where(cell => cell.Quota < cell.Candidates.Count)
            .ToList();
        fillOrder.Sort(CompareQuotaFillPriority);

        int cursor = 0;
        int idlePasses = 0;
        while (toAdd > 0 && fillOrder.Count > 0 && idlePasses < fillOrder.Count)
        {
            BudgetCell cell = fillOrder[cursor];
            if (cell.Quota < cell.Candidates.Count)
            {
                cell.Quota++;
                toAdd--;
                idlePasses = 0;
            }
            else
            {
                idlePasses++;
            }

            cursor = (cursor + 1) % fillOrder.Count;
        }
    }

    private static int ResolveBudgetGridResolution(
        int selectionBudget,
        int targetChunkSize,
        int candidateCount,
        SplatPackPruningMode pruningMode)
    {
        int coarseGrid = Math.Max(1, (int)Math.Ceiling(Math.Pow(selectionBudget / (double)Math.Max(1, targetChunkSize), 1.0 / 3.0)));
        if (pruningMode != SplatPackPruningMode.ResearchXrSensitivity)
        {
            return coarseGrid;
        }

        int representativeCells = Math.Max(1, Math.Min(selectionBudget, candidateCount));
        int coverageGrid = (int)Math.Ceiling(Math.Pow(representativeCells, 1.0 / 3.0));
        return Math.Clamp(Math.Max(coarseGrid, coverageGrid), 1, 96);
    }

    private static int CompareQuotaFillPriority(BudgetCell a, BudgetCell b)
    {
        int byRemainder = b.Remainder.CompareTo(a.Remainder);
        if (byRemainder != 0)
        {
            return byRemainder;
        }

        int byScore = b.BestScore.CompareTo(a.BestScore);
        if (byScore != 0)
        {
            return byScore;
        }

        int byMass = b.ImportanceMass.CompareTo(a.ImportanceMass);
        return byMass != 0 ? byMass : a.Key.CompareTo(b.Key);
    }

    private static int CompareByContribution(Candidate a, Candidate b, float axisLengthCap, float axisPower)
    {
        float scoreA = ContributionScore(a, axisLengthCap, axisPower);
        float scoreB = ContributionScore(b, axisLengthCap, axisPower);
        int byScore = scoreB.CompareTo(scoreA);
        return byScore != 0 ? byScore : a.SourceIndex.CompareTo(b.SourceIndex);
    }

    private static int CompareByBudgetPriority(
        Candidate a,
        Candidate b,
        float axisLengthCap,
        float axisPower,
        SplatPackPruningMode pruningMode)
    {
        int tierA = ResearchBudgetClassTier(a.BudgetRenderClass, pruningMode);
        int tierB = ResearchBudgetClassTier(b.BudgetRenderClass, pruningMode);
        int byTier = tierB.CompareTo(tierA);
        if (byTier != 0)
        {
            return byTier;
        }

        float scoreA = BudgetPriorityScore(a, axisLengthCap, axisPower, pruningMode);
        float scoreB = BudgetPriorityScore(b, axisLengthCap, axisPower, pruningMode);
        int byScore = scoreB.CompareTo(scoreA);
        return byScore != 0 ? byScore : a.SourceIndex.CompareTo(b.SourceIndex);
    }

    private static float BudgetPriorityScore(
        Candidate candidate,
        float axisLengthCap,
        float axisPower,
        SplatPackPruningMode pruningMode)
    {
        return pruningMode == SplatPackPruningMode.ResearchXrSensitivity
            ? ResearchSensitivityScore(candidate, axisLengthCap, axisPower)
            : ContributionScore(candidate, axisLengthCap, axisPower);
    }

    private static float ContributionPruneScore(
        Candidate candidate,
        float axisLengthCap,
        float axisPower,
        SplatPackPruningMode pruningMode)
    {
        return pruningMode == SplatPackPruningMode.ResearchXrSensitivity
            ? ResearchSensitivityScore(candidate, axisLengthCap, axisPower)
            : ContributionScore(candidate, axisLengthCap, axisPower);
    }

    private static double BudgetQuotaMass(
        Candidate candidate,
        float axisLengthCap,
        float axisPower,
        SplatPackPruningMode pruningMode)
    {
        double score = candidate.StructuralImportance > 0f
            ? candidate.StructuralImportance
            : BudgetPriorityScore(candidate, axisLengthCap, axisPower, pruningMode);
        if (pruningMode != SplatPackPruningMode.ResearchXrSensitivity)
        {
            return score;
        }

        return score;
    }

    private static float ResearchSensitivityScore(Candidate candidate, float axisLengthCap, float axisPower)
    {
        float viewContribution = ContributionScore(candidate, axisLengthCap, axisPower);
        float structuralContribution = candidate.StructuralImportance > 0f ? candidate.StructuralImportance : viewContribution;
        float contribution = MathF.Max(viewContribution, structuralContribution);
        float axis = axisLengthCap > 0f ? MathF.Min(candidate.MaxAxisLength, axisLengthCap) : candidate.MaxAxisLength;
        float detailSupport = MathF.Sqrt(MathF.Max(MinimumScale, axis)) * Math.Clamp(candidate.AxisRatio, 1f, 8f) / 8f;
        float opacitySupport = MathF.Sqrt(Math.Clamp(candidate.Opacity, 0f, 1f));
        float supportImportance = MathF.Max(Math.Clamp(candidate.Importance, 0f, 1f), Math.Clamp(candidate.StructuralImportance, 0f, 1f));
        float surfaceSensitivity = opacitySupport * (0.55f + supportImportance * 1.45f);
        float thinStructureReserve = candidate.AxisRatio > 4f && candidate.Opacity > 0.025f ? 0.18f : 0f;
        float rawAxisRatio = axisLengthCap > 0f ? candidate.MaxAxisLength / MathF.Max(MinimumScale, axisLengthCap) : 1f;
        float giantAxisPenalty = rawAxisRatio > 6f
            ? MathF.Pow(6f / MathF.Max(6f, rawAxisRatio), 0.75f)
            : 1f;
        return (contribution * 0.62f + surfaceSensitivity * 0.28f + detailSupport * 0.08f + thinStructureReserve) * giantAxisPenalty;
    }

    private static int ResearchBudgetClassTier(int renderClass, SplatPackPruningMode pruningMode)
    {
        if (pruningMode != SplatPackPruningMode.ResearchXrSensitivity)
        {
            return 1;
        }

        return IsResearchBudgetProtectedClass(renderClass) ? 2 : 0;
    }

    private static bool IsResearchBudgetProtectedClass(int renderClass)
    {
        return IsResearchBudgetSurfaceClass(renderClass) || IsResearchBudgetDetailClass(renderClass);
    }

    private static bool IsResearchBudgetSurfaceClass(int renderClass)
    {
        return renderClass == RenderHintSurface || renderClass == RenderHintBroadSurface;
    }

    private static bool IsResearchBudgetDetailClass(int renderClass)
    {
        return renderClass == RenderHintMicroDetail || renderClass == RenderHintForeground;
    }

    private static float ContributionScore(Candidate candidate, float axisLengthCap, float axisPower)
    {
        if (candidate.Importance > 0f)
        {
            return candidate.Importance;
        }

        return RawContributionScore(candidate, axisLengthCap, axisPower);
    }

    private static float RawContributionScore(Candidate candidate, float axisLengthCap, float axisPower)
    {
        float maxAxis = axisLengthCap > 0f
            ? MathF.Min(candidate.MaxAxisLength, axisLengthCap)
            : candidate.MaxAxisLength;
        float axisTerm = axisPower <= 0f ? 1f : MathF.Pow(MathF.Max(MinimumScale, maxAxis), axisPower);
        return candidate.Opacity * axisTerm;
    }

    private static double CoverageScore(Candidate candidate, float axisLengthCap, float axisPower)
    {
        float maxAxis = axisLengthCap > 0f
            ? MathF.Min(candidate.MaxAxisLength, axisLengthCap)
            : candidate.MaxAxisLength;
        float axisTerm = axisPower <= 0f
            ? 1f
            : MathF.Pow(MathF.Max(MinimumScale, maxAxis), axisPower);
        return Math.Max(0.0, candidate.Opacity * axisTerm);
    }

    private static float ApplyCoverageBoost(float opacity, float boost)
    {
        float clampedOpacity = Math.Clamp(opacity, 0f, 0.999f);
        float clampedBoost = MathF.Max(1f, boost);
        return Math.Clamp(1f - MathF.Pow(1f - clampedOpacity, clampedBoost), 0f, 0.999f);
    }

    private static bool TryInspectRawSplat(GaussianSplatStream stream, int index, out float opacity, out float maxAxisLength, out float axisRatio)
    {
        opacity = 0f;
        maxAxisLength = 0f;
        axisRatio = 0f;
        Vector3 position = Position(stream, index);
        if (!IsFinite(position))
        {
            return false;
        }

        float sx = SafeExp(stream.ScalesLog[index * 3 + 0]);
        float sy = SafeExp(stream.ScalesLog[index * 3 + 1]);
        float sz = SafeExp(stream.ScalesLog[index * 3 + 2]);
        if (!IsFinite(sx) || !IsFinite(sy) || !IsFinite(sz) || sx <= 0f || sy <= 0f || sz <= 0f)
        {
            return false;
        }

        int rotationOffset = index * 4;
        for (int i = 0; i < 4; i++)
        {
            if (!IsFinite(stream.RotationsRaw[rotationOffset + i]))
            {
                return false;
            }
        }

        float rawOpacity = stream.OpacitiesRaw[index];
        if (!IsFinite(rawOpacity))
        {
            return false;
        }

        opacity = Sigmoid(rawOpacity);
        maxAxisLength = MathF.Max(sx, MathF.Max(sy, sz));
        axisRatio = AxisRatio(new Vector3(sx, sy, sz));
        return IsFinite(opacity) && IsFinite(maxAxisLength) && IsFinite(axisRatio);
    }

    private static float ResolveAxisLengthCap(Candidate[] candidates, SplatPackBuildOptions options)
    {
        if (options.MaxAxisLength > 0f)
        {
            return options.MaxAxisLength;
        }

        if (options.MaxAxisLengthPercentile <= 0f || candidates.Length == 0)
        {
            return 0f;
        }

        var values = new float[candidates.Length];
        for (int i = 0; i < candidates.Length; i++)
        {
            values[i] = candidates[i].MaxAxisLength;
        }

        float percentile = Percentile(values, Math.Clamp(options.MaxAxisLengthPercentile, 0f, 100f));
        float multiplier = MathF.Max(0.01f, options.MaxAxisLengthMultiplier);
        return MathF.Max(MinimumScale, percentile * multiplier);
    }

    private static SplatPackBounds ComputeBounds(GaussianSplatStream stream, IReadOnlyList<Candidate> candidates)
    {
        var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        for (int i = 0; i < candidates.Count; i++)
        {
            Vector3 p = Position(stream, candidates[i].SourceIndex);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return new SplatPackBounds(min, max);
    }

    private static List<BuildCell> BuildStreamingCells(
        GaussianSplatStream stream,
        Candidate[] candidates,
        SplatPackBounds bounds,
        int gridResolution,
        SplatPackBuildOptions options)
    {
        var cells = new Dictionary<long, List<Candidate>>();
        foreach (Candidate candidate in candidates)
        {
            long key = GridKey(stream, bounds, gridResolution, candidate.SourceIndex);
            if (!cells.TryGetValue(key, out List<Candidate>? list))
            {
                list = new List<Candidate>();
                cells.Add(key, list);
            }

            list.Add(candidate);
        }

        var buildCells = new List<BuildCell>(cells.Count);
        foreach ((long key, List<Candidate> list) in cells)
        {
            list.Sort(CompareByImportance);
            double mass = list.Sum(candidate => Math.Max(0.0, candidate.Importance));
            buildCells.Add(new BuildCell
            {
                Key = key,
                Candidates = list,
                ImportanceMass = mass,
                BestImportance = list.Count > 0 ? list[0].Importance : 0f,
            });
        }

        if (options.EnableStreamingLayout)
        {
            buildCells.Sort(CompareStreamingPriority);
        }
        else
        {
            buildCells.Sort((a, b) => a.Key.CompareTo(b.Key));
        }

        return buildCells;
    }

    private static int CompareByImportance(Candidate a, Candidate b)
    {
        int byImportance = b.Importance.CompareTo(a.Importance);
        return byImportance != 0 ? byImportance : a.SourceIndex.CompareTo(b.SourceIndex);
    }

    private static int CompareStreamingPriority(BuildCell a, BuildCell b)
    {
        int byBest = b.BestImportance.CompareTo(a.BestImportance);
        if (byBest != 0)
        {
            return byBest;
        }

        int byMass = b.ImportanceMass.CompareTo(a.ImportanceMass);
        return byMass != 0 ? byMass : a.Key.CompareTo(b.Key);
    }

    private static int ResolveLodTier(int localIndex, int chunkCount, SplatPackBuildOptions options)
    {
        if (chunkCount <= 1)
        {
            return 0;
        }

        float normalizedRank = (localIndex + 1) / (float)chunkCount;
        float farKeep = Math.Clamp(options.FarLodKeepFraction, 0.01f, 1f);
        float midKeep = Math.Clamp(MathF.Max(options.MidLodKeepFraction, farKeep), farKeep, 1f);
        if (normalizedRank <= farKeep)
        {
            return 0;
        }

        return normalizedRank <= midKeep ? 1 : 2;
    }

    private static int ResolveChunkLodLevel(BuildCell cell, List<BuildCell> cells)
    {
        if (cells.Count <= 1)
        {
            return 0;
        }

        float best = cells[0].BestImportance;
        float normalized = best > 1e-8f ? cell.BestImportance / best : 1f;
        if (normalized >= 0.66f)
        {
            return 0;
        }

        return normalized >= 0.33f ? 1 : 2;
    }

    private static float ImportanceMass(IReadOnlyList<Candidate> candidates)
    {
        double sum = 0.0;
        for (int i = 0; i < candidates.Count; i++)
        {
            sum += Math.Max(0.0, candidates[i].Importance);
        }

        return (float)sum;
    }

    private static float MeanCoverageBoost(IReadOnlyList<Candidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return 1f;
        }

        double sum = 0.0;
        for (int i = 0; i < candidates.Count; i++)
        {
            sum += Math.Max(1.0, candidates[i].CoverageBoost);
        }

        return (float)(sum / candidates.Count);
    }

    private static float MaxCoverageBoost(IReadOnlyList<Candidate> candidates)
    {
        float max = 1f;
        for (int i = 0; i < candidates.Count; i++)
        {
            max = MathF.Max(max, candidates[i].CoverageBoost);
        }

        return max;
    }

    private static SplatPackChunk BuildChunk(SplatPackSplat[] splats, int offset, int count, int lodLevel)
    {
        var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        for (int i = offset; i < offset + count; i++)
        {
            Vector3 p = new(splats[i].CenterWS.X, splats[i].CenterWS.Y, splats[i].CenterWS.Z);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return new SplatPackChunk(min, max, offset, count, lodLevel);
    }

    private static Vector4[] BuildSh3Coefficients(GaussianSplatStream stream, SplatPackSplat[] splats, SplatPackShMode shMode)
    {
        const int vectorsPerSplat = 12;
        if (shMode != SplatPackShMode.Sh3 || stream.ShRest == null || stream.ShRestStride < 45 || splats.Length == 0)
        {
            return Array.Empty<Vector4>();
        }

        var coefficients = new Vector4[splats.Length * vectorsPerSplat];
        for (int i = 0; i < splats.Length; i++)
        {
            int sourceIndex = Math.Clamp((int)MathF.Round(splats[i].Meta.X), 0, Math.Max(0, stream.Count - 1));
            int sourceOffset = sourceIndex * stream.ShRestStride;
            int destinationOffset = i * vectorsPerSplat;
            for (int channel = 0; channel < 3; channel++)
            {
                int channelOffset = sourceOffset + channel * 15;
                int packedOffset = destinationOffset + channel * 4;
                coefficients[packedOffset + 0] = ReadSh4(stream.ShRest, channelOffset, 0, 15);
                coefficients[packedOffset + 1] = ReadSh4(stream.ShRest, channelOffset, 4, 15);
                coefficients[packedOffset + 2] = ReadSh4(stream.ShRest, channelOffset, 8, 15);
                coefficients[packedOffset + 3] = ReadSh4(stream.ShRest, channelOffset, 12, 15);
            }
        }

        return coefficients;
    }

    private static Vector4[] BuildResearchMetadata(SplatPackSplat[] splats, float[] maxAxisLengths, float[] axisRatios)
    {
        if (splats.Length == 0)
        {
            return Array.Empty<Vector4>();
        }

        var metadata = new Vector4[splats.Length];
        for (int i = 0; i < splats.Length; i++)
        {
            int flags = Math.Clamp((int)MathF.Round(splats[i].Meta.Y), 0, 255);
            int renderClass = (flags >> 1) & 0x7;
            int coverageHint = (flags >> 4) & 0xF;
            metadata[i] = new Vector4(
                renderClass,
                coverageHint / 15f,
                i < maxAxisLengths.Length ? maxAxisLengths[i] : 0f,
                i < axisRatios.Length ? axisRatios[i] : 1f);
        }

        return metadata;
    }

    private static Vector4[] BuildArtifactRepairMetadata(Candidate[] candidates)
    {
        if (candidates.Length == 0)
        {
            return Array.Empty<Vector4>();
        }

        var metadata = new Vector4[candidates.Length];
        for (int i = 0; i < candidates.Length; i++)
        {
            Candidate candidate = candidates[i];
            metadata[i] = new Vector4(
                candidate.ArtifactClass,
                candidate.LayerId,
                MathF.Max(1f, candidate.CoverageBoost),
                candidate.RepairAction);
        }

        return metadata;
    }

    private static SplatPackSupportMetadata[] BuildSupportMetadata(
        SplatPackSplat[] splats,
        SplatPackBounds bounds,
        float[] maxAxisLengths,
        float[] axisRatios)
    {
        if (splats.Length == 0)
        {
            return Array.Empty<SplatPackSupportMetadata>();
        }

        var metadata = new SplatPackSupportMetadata[splats.Length];
        Vector3 packageCenter = (bounds.Min + bounds.Max) * 0.5f;
        float diagonal = MathF.Max(MinimumScale, (bounds.Max - bounds.Min).Length());
        for (int i = 0; i < splats.Length; i++)
        {
            SplatPackSplat splat = splats[i];
            Vector3 position = new(splat.CenterWS.X, splat.CenterWS.Y, splat.CenterWS.Z);
            Vector3 supportDirection = position - packageCenter;
            if (supportDirection.LengthSquared() <= MinimumScale)
            {
                supportDirection = Vector3.UnitZ;
            }
            else
            {
                supportDirection = Vector3.Normalize(supportDirection);
            }

            EncodeOctahedral8(supportDirection, out int encodedX, out int encodedY);

            int renderClass = DecodeRenderHintClass(splat);
            float opacity = Math.Clamp(splat.Color.W, 0f, 1f);
            float importance = Math.Clamp(splat.Meta.Z, 0f, 1f);
            float maxAxis = i < maxAxisLengths.Length ? MathF.Max(MinimumScale, maxAxisLengths[i]) : MaxAxisLength(splat);
            float axisRatio = i < axisRatios.Length ? MathF.Max(1f, axisRatios[i]) : AxisRatioForSplat(splat);
            float axisRelative = Math.Clamp(maxAxis / diagonal, 0f, 1f);
            float saturation = Saturation(new Vector3(splat.Color.X, splat.Color.Y, splat.Color.Z));
            float supportConfidence = ConservativeSupportConfidence(renderClass, opacity, importance, axisRelative, axisRatio, saturation);
            float freeFloaterConfidence = ConservativeFreeFloaterConfidence(renderClass, opacity, importance, axisRelative, axisRatio, saturation);
            float layerRisk = renderClass == RenderHintLayerRisk
                ? 0.82f
                : Math.Clamp((axisRatio - 5f) / 6f, 0f, 1f) * Math.Clamp(axisRelative * 18f, 0f, 1f) * 0.5f;
            int etaBucket = QuantizeEtaBucket(axisRelative, axisRatio, renderClass);
            float coneDegrees = ConservativeSupportConeDegrees(renderClass, supportConfidence, freeFloaterConfidence, layerRisk);

            uint packed0 =
                (uint)(encodedX & 0xFF)
                | ((uint)(encodedY & 0xFF) << 8)
                | ((uint)QuantizeByte(coneDegrees / 180f) << 16)
                | ((uint)(etaBucket & 0xF) << 24);
            uint packed1 =
                (uint)QuantizeByte(supportConfidence)
                | ((uint)QuantizeByte(freeFloaterConfidence) << 8)
                | ((uint)QuantizeByte(layerRisk) << 16)
                | ((uint)(renderClass & 0x7) << 24);
            metadata[i] = new SplatPackSupportMetadata(packed0, packed1);
        }

        return metadata;
    }

    private static float ConservativeSupportConfidence(
        int renderClass,
        float opacity,
        float importance,
        float axisRelative,
        float axisRatio,
        float saturation)
    {
        float classBase = renderClass switch
        {
            RenderHintMicroDetail => 0.66f,
            RenderHintBroadSurface => 0.7f,
            RenderHintForeground => 0.62f,
            RenderHintLayerRisk => 0.48f,
            RenderHintFreeSpace => 0.18f,
            RenderHintFloater => 0.12f,
            _ => 0.58f,
        };
        float signal = opacity * 0.12f + importance * 0.16f + saturation * 0.05f;
        float smearPenalty = Math.Clamp((axisRelative - 0.01f) / 0.07f, 0f, 1f) * 0.34f;
        float eccentricPenalty = Math.Clamp((axisRatio - 4f) / 6f, 0f, 1f) * 0.26f;
        float lowDetailPenalty = Math.Clamp((0.24f - saturation) * 2.6f, 0f, 1f)
                                 * Math.Clamp((0.58f - opacity) * 1.9f, 0f, 1f)
                                 * Math.Clamp(axisRelative * 28f, 0f, 1f)
                                 * 0.18f;
        if (renderClass == RenderHintMicroDetail)
        {
            smearPenalty *= 1.2f;
            lowDetailPenalty *= 0.45f;
        }

        return Math.Clamp(classBase + signal - smearPenalty - eccentricPenalty - lowDetailPenalty, 0f, 1f);
    }

    private static float ConservativeFreeFloaterConfidence(
        int renderClass,
        float opacity,
        float importance,
        float axisRelative,
        float axisRatio,
        float saturation)
    {
        float classBase = renderClass switch
        {
            RenderHintFreeSpace => 0.86f,
            RenderHintFloater => 0.94f,
            RenderHintLayerRisk => 0.38f,
            RenderHintMicroDetail => 0.12f,
            RenderHintForeground => 0.18f,
            RenderHintBroadSurface => 0.14f,
            _ => 0.18f,
        };
        float weakSignal = (1f - Math.Clamp(importance * 3.2f + opacity * 1.6f, 0f, 1f)) * 0.28f;
        float smear = Math.Clamp((axisRelative - 0.008f) / 0.075f, 0f, 1f) * 0.42f;
        float stringy = Math.Clamp((axisRatio - 5f) / 7f, 0f, 1f) * 0.3f;
        float pale = Math.Clamp((0.26f - saturation) * 2.8f, 0f, 1f) * Math.Clamp((0.55f - opacity) * 2.4f, 0f, 1f) * 0.28f;
        return Math.Clamp(classBase + weakSignal + smear + stringy + pale, 0f, 1f);
    }

    private static float ConservativeSupportConeDegrees(int renderClass, float supportConfidence, float freeFloaterConfidence, float layerRisk)
    {
        float cone = renderClass switch
        {
            RenderHintMicroDetail => 78f,
            RenderHintForeground => 72f,
            RenderHintBroadSurface => 112f,
            RenderHintFreeSpace => 42f,
            RenderHintFloater => 36f,
            RenderHintLayerRisk => 70f,
            _ => 96f,
        };
        cone += supportConfidence * 12f;
        cone -= freeFloaterConfidence * 26f;
        cone -= layerRisk * 18f;
        return Math.Clamp(cone, 24f, 128f);
    }

    private static int QuantizeEtaBucket(float axisRelative, float axisRatio, int renderClass)
    {
        float risk = axisRelative * 48f + Math.Clamp((axisRatio - 1f) / 8f, 0f, 1f) * 2f;
        if (renderClass == RenderHintMicroDetail)
        {
            risk *= 0.8f;
        }

        return Math.Clamp((int)MathF.Round(risk), 0, 15);
    }

    private static void EncodeOctahedral8(Vector3 direction, out int encodedX, out int encodedY)
    {
        direction = Vector3.Normalize(direction);
        float invL1 = 1f / MathF.Max(1e-6f, MathF.Abs(direction.X) + MathF.Abs(direction.Y) + MathF.Abs(direction.Z));
        float x = direction.X * invL1;
        float y = direction.Y * invL1;
        if (direction.Z < 0f)
        {
            float oldX = x;
            x = (1f - MathF.Abs(y)) * MathF.CopySign(1f, oldX);
            y = (1f - MathF.Abs(oldX)) * MathF.CopySign(1f, y);
        }

        encodedX = Math.Clamp((int)MathF.Round((x * 0.5f + 0.5f) * 255f), 0, 255);
        encodedY = Math.Clamp((int)MathF.Round((y * 0.5f + 0.5f) * 255f), 0, 255);
    }

    private static int QuantizeByte(float value)
    {
        return Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);
    }

    private static float MaxAxisLength(SplatPackSplat splat)
    {
        return MathF.Max(
            AxisLength(splat.Axis0WS),
            MathF.Max(AxisLength(splat.Axis1WS), AxisLength(splat.Axis2WS)));
    }

    private static float AxisRatioForSplat(SplatPackSplat splat)
    {
        float a = AxisLength(splat.Axis0WS);
        float b = AxisLength(splat.Axis1WS);
        float c = AxisLength(splat.Axis2WS);
        float max = MathF.Max(a, MathF.Max(b, c));
        float min = MathF.Max(MinimumScale, MathF.Min(a, MathF.Min(b, c)));
        return max / min;
    }

    private static float AxisLength(Vector4 axis)
    {
        return MathF.Sqrt(axis.X * axis.X + axis.Y * axis.Y + axis.Z * axis.Z);
    }

    private static bool ShouldUpgradeLod(Candidate candidate)
    {
        return candidate.RepairAction == RepairActionLodUpgrade
               || candidate.ArtifactClass == ArtifactEdge
               || candidate.ArtifactClass == ArtifactTexture
               || candidate.ArtifactClass == ArtifactStereo;
    }

    private static Vector4 ReadSh4(float[] source, int channelOffset, int localStart, int channelCoefficientCount)
    {
        return new Vector4(
            ReadSh(source, channelOffset, localStart + 0, channelCoefficientCount),
            ReadSh(source, channelOffset, localStart + 1, channelCoefficientCount),
            ReadSh(source, channelOffset, localStart + 2, channelCoefficientCount),
            ReadSh(source, channelOffset, localStart + 3, channelCoefficientCount));
    }

    private static float ReadSh(float[] source, int channelOffset, int localIndex, int channelCoefficientCount)
    {
        int index = channelOffset + localIndex;
        return localIndex >= 0 && localIndex < channelCoefficientCount && index >= 0 && index < source.Length ? source[index] : 0f;
    }

    private static Vector3 ReadShDcRgb(GaussianSplatStream stream, int index)
    {
        if (stream.ShDc == null)
        {
            return Vector3.One;
        }

        Vector3 rgb = new(
            0.5f + ShC0 * stream.ShDc[index * 3 + 0],
            0.5f + ShC0 * stream.ShDc[index * 3 + 1],
            0.5f + ShC0 * stream.ShDc[index * 3 + 2]);
        return Vector3.Clamp(rgb, Vector3.Zero, Vector3.One);
    }

    private static SplatPackSplat ConvertSplat(
        GaussianSplatStream stream,
        int index,
        SplatPackRotationOrder rotationOrder,
        float axisLengthCap,
        float maxAxisRatio,
        SplatPackBounds bounds,
        SplatPackBuildOptions options,
        int lodTier,
        float importance,
        float coverageBoost,
        int budgetRenderClass,
        int artifactClass,
        int repairAction,
        out bool axisClamped,
        out bool axisRatioClamped,
        out bool quantized,
        out float emittedMaxAxisLength,
        out float emittedAxisRatio)
    {
        Vector3 position = Position(stream, index);
        Vector3 scale = new(
            SafeExp(stream.ScalesLog[index * 3 + 0]),
            SafeExp(stream.ScalesLog[index * 3 + 1]),
            SafeExp(stream.ScalesLog[index * 3 + 2]));
        Vector3 unclampedScale = scale;
        if (axisLengthCap > 0f)
        {
            scale = Vector3.Min(scale, new Vector3(axisLengthCap));
        }

        Vector3 lengthClampedScale = scale;
        Vector3 rgb = ReadShDcRgb(stream, index);
        ExtractFirstOrderSh(stream, index, out Vector3 sh1R, out Vector3 sh1G, out Vector3 sh1B);
        float opacity = Sigmoid(stream.OpacitiesRaw[index]);
        float preClampMaxAxisLength = MathF.Max(lengthClampedScale.X, MathF.Max(lengthClampedScale.Y, lengthClampedScale.Z));
        float preClampAxisRatio = AxisRatio(lengthClampedScale);
        int renderHintClass = ClassifyRenderHint(
            rgb,
            opacity,
            preClampMaxAxisLength,
            preClampAxisRatio,
            axisLengthCap,
            importance,
            coverageBoost,
            lodTier,
            artifactClass,
            repairAction);
        if (artifactClass == ArtifactNone && repairAction == RepairActionNone)
        {
            renderHintClass = Math.Clamp(budgetRenderClass, 0, 7);
        }

        float classMaxAxisRatio = ResolveClassMaxAxisRatio(maxAxisRatio, renderHintClass, artifactClass, repairAction);
        scale = ClampAxisRatio(scale, classMaxAxisRatio, out axisRatioClamped);
        axisClamped = lengthClampedScale != unclampedScale;
        axisRatioClamped = axisRatioClamped && scale != lengthClampedScale;
        emittedMaxAxisLength = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
        emittedAxisRatio = AxisRatio(scale);

        Quaternion rotation = DecodeRotation(stream, index, rotationOrder);

        Vector3 axis0 = Vector3.Transform(Vector3.UnitX, rotation) * scale.X;
        Vector3 axis1 = Vector3.Transform(Vector3.UnitY, rotation) * scale.Y;
        Vector3 axis2 = Vector3.Transform(Vector3.UnitZ, rotation) * scale.Z;
        float effectiveCoverageBoost = renderHintClass == RenderHintSurface || renderHintClass == RenderHintBroadSurface
            ? coverageBoost
            : 1f;
        opacity = ApplyCoverageBoost(opacity, effectiveCoverageBoost);
        quantized = false;
        if (options.EnableAttributeQuantization)
        {
            bool relaxColorQuantization = artifactClass == ArtifactTexture
                || repairAction == RepairActionQuantRelax
                || repairAction == RepairActionShPreserve;
            Vector3 qPosition = QuantizeVector(position, bounds.Min, bounds.Max, options.PositionQuantizationBits);
            float axisRange = MathF.Max(MinimumScale, axisLengthCap > 0f ? axisLengthCap : emittedMaxAxisLength);
            Vector3 qAxis0 = QuantizeVector(axis0, new Vector3(-axisRange), new Vector3(axisRange), options.AxisQuantizationBits);
            Vector3 qAxis1 = QuantizeVector(axis1, new Vector3(-axisRange), new Vector3(axisRange), options.AxisQuantizationBits);
            Vector3 qAxis2 = QuantizeVector(axis2, new Vector3(-axisRange), new Vector3(axisRange), options.AxisQuantizationBits);
            Vector3 qRgb = relaxColorQuantization ? rgb : QuantizeVector(rgb, Vector3.Zero, Vector3.One, options.ColorQuantizationBits);
            float qOpacity = relaxColorQuantization ? opacity : QuantizeScalar(opacity, 0f, 1f, options.OpacityQuantizationBits);
            quantized = qPosition != position || qAxis0 != axis0 || qAxis1 != axis1 || qAxis2 != axis2 || qRgb != rgb || !NearlyEqual(qOpacity, opacity);
            position = qPosition;
            axis0 = qAxis0;
            axis1 = qAxis1;
            axis2 = qAxis2;
            rgb = qRgb;
            opacity = qOpacity;
        }

        int coverageHint = QuantizeCoverageHint(effectiveCoverageBoost, options.MaxCoverageBoost);
        int packedMetaFlags = PackMetaFlags(quantized, renderHintClass, coverageHint);

        return new SplatPackSplat(
            new Vector4(position, 1f),
            new Vector4(axis0, 0f),
            new Vector4(axis1, 0f),
            new Vector4(axis2, 0f),
            new Vector4(rgb, opacity),
            new Vector4(index, packedMetaFlags, Math.Clamp(importance, 0f, 1f), Math.Clamp(lodTier, 0, 2)),
            new Vector4(sh1R, 0f),
            new Vector4(sh1G, 0f),
            new Vector4(sh1B, 0f));
    }

    private static int PackMetaFlags(bool quantized, int renderHintClass, int coverageHint)
    {
        int flags = quantized ? 1 : 0;
        flags |= (Math.Clamp(renderHintClass, 0, 7) & 0x7) << 1;
        flags |= (Math.Clamp(coverageHint, 0, 15) & 0xF) << 4;
        return flags;
    }

    private static int QuantizeCoverageHint(float coverageBoost, float maxCoverageBoost)
    {
        if (!IsFinite(coverageBoost) || maxCoverageBoost <= 1.0001f)
        {
            return 0;
        }

        float t = Math.Clamp((coverageBoost - 1f) / MathF.Max(1e-4f, maxCoverageBoost - 1f), 0f, 1f);
        return Math.Clamp((int)MathF.Round(t * 15f), 0, 15);
    }

    private static int ClassifyRenderHint(
        Vector3 rgb,
        float opacity,
        float maxAxisLength,
        float axisRatio,
        float axisLengthCap,
        float importance,
        float coverageBoost,
        int lodTier,
        int artifactClass,
        int repairAction)
    {
        float maxRgb = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));
        float minRgb = MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z));
        float saturation = maxRgb > 1e-4f ? Math.Clamp((maxRgb - minRgb) / maxRgb, 0f, 1f) : 0f;
        float blueDominance = Math.Clamp((rgb.Z - MathF.Max(rgb.X, rgb.Y)) * 4f, 0f, 1f)
            * Math.Clamp((maxRgb - 0.32f) * 2f, 0f, 1f);
        bool paleLowDetail = maxRgb > 0.68f && saturation < 0.18f;
        bool likelyFreeSpace = (blueDominance > 0.22f || paleLowDetail)
            && importance < 0.42f
            && opacity < 0.5f;

        float axisReference = MathF.Max(MinimumScale, axisLengthCap > 0f ? axisLengthCap : maxAxisLength);
        float relativeAxis = Math.Clamp(maxAxisLength / axisReference, 0f, 4f);
        bool lowConfidence = importance < 0.05f && opacity < 0.1f && coverageBoost < 1.28f;
        bool stringyLowConfidence = axisRatio > 5.5f && importance < 0.16f && opacity < 0.14f;
        bool tinyWeakFarTier = relativeAxis < 0.18f && lodTier >= 2 && importance < 0.06f && opacity < 0.075f;
        bool largeWeakAxis = relativeAxis > 0.72f && (importance < 0.18f || opacity < 0.12f);
        bool hugeWeakAxis = relativeAxis > 1.15f && importance < 0.28f;
        bool unsupportedSmear = (largeWeakAxis || hugeWeakAxis || stringyLowConfidence)
            && artifactClass != ArtifactLayer;
        bool supportedForeground = (artifactClass == ArtifactEdge || artifactClass == ArtifactStereo)
            && !unsupportedSmear
            && !likelyFreeSpace
            && opacity > 0.16f
            && importance > 0.08f
            && relativeAxis < 0.52f
            && axisRatio < 5.5f;
        bool textureLikeArtifact = artifactClass == ArtifactTexture
            || repairAction == RepairActionShPreserve
            || repairAction == RepairActionQuantRelax;
        bool supportedMicroDetail = textureLikeArtifact
            && !likelyFreeSpace
            && !unsupportedSmear
            && opacity > 0.055f
            && importance > 0.045f
            && relativeAxis < 0.72f
            && axisRatio < 8f;

        if (artifactClass == ArtifactLayer)
        {
            return RenderHintLayerRisk;
        }

        if (likelyFreeSpace)
        {
            return RenderHintFreeSpace;
        }

        if (lowConfidence || unsupportedSmear || tinyWeakFarTier)
        {
            return RenderHintFloater;
        }

        if (supportedMicroDetail)
        {
            return RenderHintMicroDetail;
        }

        if (supportedForeground)
        {
            return RenderHintForeground;
        }

        if (artifactClass == ArtifactEdge || artifactClass == ArtifactStereo)
        {
            return importance > 0.08f && opacity > 0.08f && relativeAxis < 0.6f
                ? RenderHintMicroDetail
                : RenderHintFloater;
        }

        if (textureLikeArtifact)
        {
            return RenderHintFloater;
        }

        if (saturation > 0.32f
            && relativeAxis < 0.55f
            && opacity > 0.02f
            && importance > 0.025f)
        {
            return RenderHintMicroDetail;
        }

        if ((importance > 0.72f || (opacity > 0.45f && importance > 0.35f))
            && !unsupportedSmear
            && !likelyFreeSpace
            && relativeAxis < 0.72f
            && axisRatio < 7.5f)
        {
            return RenderHintForeground;
        }

        bool supportedBroadSurface = !likelyFreeSpace
            && !unsupportedSmear
            && axisRatio < 6.5f
            && opacity > 0.16f
            && importance > 0.16f
            && relativeAxis < 0.62f;
        bool coverageBackedBroadSurface = supportedBroadSurface
            && coverageBoost > 1.45f
            && importance > 0.22f;
        bool neutralPlanarSurface = supportedBroadSurface
            && saturation < 0.3f
            && opacity > 0.28f
            && importance > 0.24f;
        bool nearLodBroadSurface = supportedBroadSurface
            && lodTier == 0
            && opacity > 0.38f
            && importance > 0.3f;
        if (coverageBackedBroadSurface || neutralPlanarSurface || nearLodBroadSurface)
        {
            return RenderHintBroadSurface;
        }

        return RenderHintSurface;
    }

    private static void ExtractFirstOrderSh(
        GaussianSplatStream stream,
        int index,
        out Vector3 sh1R,
        out Vector3 sh1G,
        out Vector3 sh1B)
    {
        sh1R = Vector3.Zero;
        sh1G = Vector3.Zero;
        sh1B = Vector3.Zero;
        if (stream.ShRest == null || stream.ShRestStride < 9)
        {
            return;
        }

        int offset = index * stream.ShRestStride;
        if (stream.ShRestStride >= 45)
        {
            sh1R = new Vector3(
                stream.ShRest[offset + 2],
                stream.ShRest[offset + 0],
                stream.ShRest[offset + 1]);
            sh1G = new Vector3(
                stream.ShRest[offset + 15 + 2],
                stream.ShRest[offset + 15 + 0],
                stream.ShRest[offset + 15 + 1]);
            sh1B = new Vector3(
                stream.ShRest[offset + 30 + 2],
                stream.ShRest[offset + 30 + 0],
                stream.ShRest[offset + 30 + 1]);
            return;
        }

        if (stream.ShRestStride >= 12)
        {
            sh1R = new Vector3(stream.ShRest[offset + 9], stream.ShRest[offset + 0], stream.ShRest[offset + 3]);
            sh1G = new Vector3(stream.ShRest[offset + 10], stream.ShRest[offset + 1], stream.ShRest[offset + 4]);
            sh1B = new Vector3(stream.ShRest[offset + 11], stream.ShRest[offset + 2], stream.ShRest[offset + 5]);
        }
    }

    private static Vector3 QuantizeVector(Vector3 value, Vector3 min, Vector3 max, int bits)
    {
        return new Vector3(
            QuantizeScalar(value.X, min.X, max.X, bits),
            QuantizeScalar(value.Y, min.Y, max.Y, bits),
            QuantizeScalar(value.Z, min.Z, max.Z, bits));
    }

    private static float QuantizeScalar(float value, float min, float max, int bits)
    {
        if (bits <= 0 || max <= min || !IsFinite(value))
        {
            return value;
        }

        int clampedBits = Math.Clamp(bits, 1, 24);
        double levels = Math.Pow(2.0, clampedBits) - 1.0;
        double t = Math.Clamp((value - min) / (double)(max - min), 0.0, 1.0);
        return (float)(min + Math.Round(t * levels) / levels * (max - min));
    }

    private static bool NearlyEqual(float a, float b)
    {
        return MathF.Abs(a - b) <= 1e-7f;
    }

    private static Vector3 ClampAxisRatio(Vector3 scale, float maxAxisRatio, out bool clamped)
    {
        clamped = false;
        if (maxAxisRatio <= 0f)
        {
            return scale;
        }

        float middle = Middle(scale.X, scale.Y, scale.Z);
        float cap = MathF.Max(MinimumScale, middle * MathF.Max(1f, maxAxisRatio));
        Vector3 clampedScale = Vector3.Min(scale, new Vector3(cap));
        clamped = clampedScale != scale;
        return clampedScale;
    }

    private static float ResolveClassMaxAxisRatio(
        float maxAxisRatio,
        int renderHintClass,
        int artifactClass,
        int repairAction)
    {
        float repairMaxAxisRatio = ResolveRepairMaxAxisRatio(maxAxisRatio, artifactClass, repairAction);
        if (repairMaxAxisRatio <= 0f)
        {
            return repairMaxAxisRatio;
        }

        return renderHintClass switch
        {
            RenderHintMicroDetail => MathF.Min(repairMaxAxisRatio, 6f),
            RenderHintForeground => MathF.Min(repairMaxAxisRatio, 10f),
            RenderHintLayerRisk => MathF.Min(repairMaxAxisRatio, 5f),
            RenderHintFreeSpace => MathF.Min(repairMaxAxisRatio, 4f),
            RenderHintFloater => MathF.Min(repairMaxAxisRatio, 2.5f),
            _ => repairMaxAxisRatio,
        };
    }

    private static float ResolveRepairMaxAxisRatio(float maxAxisRatio, int artifactClass, int repairAction)
    {
        if (maxAxisRatio <= 0f)
        {
            return maxAxisRatio;
        }

        return maxAxisRatio;
    }

    private static float AxisRatio(Vector3 scale)
    {
        float middle = MathF.Max(MinimumScale, Middle(scale.X, scale.Y, scale.Z));
        float max = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
        return max / middle;
    }

    private static float Middle(float a, float b, float c)
    {
        return a + b + c - MathF.Min(a, MathF.Min(b, c)) - MathF.Max(a, MathF.Max(b, c));
    }

    private static Vector3 Position(GaussianSplatStream stream, int index)
    {
        return new Vector3(
            stream.PositionsXYZ[index * 3 + 0],
            stream.PositionsXYZ[index * 3 + 1],
            stream.PositionsXYZ[index * 3 + 2]);
    }

    private static SplatPackRotationOrder ResolveRotationOrder(GaussianSplatStream stream, SplatPackRotationOrder requested)
    {
        if (requested != SplatPackRotationOrder.Auto || stream.Count <= 0)
        {
            return requested == SplatPackRotationOrder.Auto ? SplatPackRotationOrder.WXYZ : requested;
        }

        int sampleCount = Math.Min(stream.Count, 8192);
        double rot0Abs = 0.0;
        double rot3Abs = 0.0;
        for (int i = 0; i < sampleCount; i++)
        {
            int offset = i * 4;
            rot0Abs += Math.Abs(stream.RotationsRaw[offset]);
            rot3Abs += Math.Abs(stream.RotationsRaw[offset + 3]);
        }

        return rot0Abs >= rot3Abs ? SplatPackRotationOrder.WXYZ : SplatPackRotationOrder.XYZW;
    }

    public static SplatPackRotationOrder DetectRotationOrder(GaussianSplatStream stream)
    {
        return ResolveRotationOrder(stream, SplatPackRotationOrder.Auto);
    }

    private static Quaternion DecodeRotation(GaussianSplatStream stream, int index, SplatPackRotationOrder rotationOrder)
    {
        int offset = index * 4;
        float r0 = stream.RotationsRaw[offset];
        float r1 = stream.RotationsRaw[offset + 1];
        float r2 = stream.RotationsRaw[offset + 2];
        float r3 = stream.RotationsRaw[offset + 3];

        return rotationOrder switch
        {
            SplatPackRotationOrder.XYZW => Normalize(new Quaternion(r0, r1, r2, r3)),
            _ => Normalize(new Quaternion(r1, r2, r3, r0)),
        };
    }

    private static long GridKey(GaussianSplatStream stream, SplatPackBounds bounds, int resolution, int index)
    {
        return GridKey(GridCoordFor(stream, bounds, resolution, index), resolution);
    }

    private static GridCoord GridCoordFor(GaussianSplatStream stream, SplatPackBounds bounds, int resolution, int index)
    {
        Vector3 p = Position(stream, index);
        Vector3 extent = Vector3.Max(bounds.Max - bounds.Min, new Vector3(1e-6f));
        Vector3 normalized = Vector3.Clamp(
            new Vector3(
                (p.X - bounds.Min.X) / extent.X,
                (p.Y - bounds.Min.Y) / extent.Y,
                (p.Z - bounds.Min.Z) / extent.Z),
            Vector3.Zero,
            new Vector3(0.999999f));
        int x = Math.Clamp((int)(normalized.X * resolution), 0, resolution - 1);
        int y = Math.Clamp((int)(normalized.Y * resolution), 0, resolution - 1);
        int z = Math.Clamp((int)(normalized.Z * resolution), 0, resolution - 1);
        return new GridCoord(x, y, z);
    }

    private static long GridKey(GridCoord coord, int resolution)
    {
        return coord.X + (long)coord.Y * resolution + (long)coord.Z * resolution * resolution;
    }

    private static int CountOccupiedNeighborSplats(Dictionary<long, int> occupancy, GridCoord coord, int resolution)
    {
        int count = 0;
        for (int z = Math.Max(0, coord.Z - 1); z <= Math.Min(resolution - 1, coord.Z + 1); z++)
        {
            for (int y = Math.Max(0, coord.Y - 1); y <= Math.Min(resolution - 1, coord.Y + 1); y++)
            {
                for (int x = Math.Max(0, coord.X - 1); x <= Math.Min(resolution - 1, coord.X + 1); x++)
                {
                    count += occupancy.GetValueOrDefault(GridKey(new GridCoord(x, y, z), resolution));
                }
            }
        }

        return count;
    }

    private static int ResolveSpatialOutlierGridResolution(int candidateCount, SplatPackBuildOptions options)
    {
        if (options.SpatialOutlierGridResolution > 0)
        {
            return Math.Clamp(options.SpatialOutlierGridResolution, 4, 128);
        }

        int resolution = (int)MathF.Ceiling(MathF.Pow(MathF.Max(1, candidateCount) / 16f, 1f / 3f));
        return Math.Clamp(resolution, 12, 64);
    }

    private static Quaternion Normalize(Quaternion q)
    {
        float length = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        if (length <= 1e-6f)
        {
            return Quaternion.Identity;
        }

        float inv = 1f / length;
        return new Quaternion(q.X * inv, q.Y * inv, q.Z * inv, q.W * inv);
    }

    private static float Sigmoid(float value)
    {
        return 1f / (1f + MathF.Exp(-value));
    }

    private static float SafeExp(float value)
    {
        if (!IsFinite(value))
        {
            return float.NaN;
        }

        return MathF.Exp(Math.Clamp(value, -40f, 40f));
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static SplatPackBuildReport BuildReport(
        SplatPackPackage package,
        SplatPackBuildOptions options,
        SplatPackRotationOrder rotationOrder,
        float axisLengthCap,
        int prunedInvalid,
        int prunedLowOpacity,
        int prunedLowContribution,
        int prunedSpatialOutliers,
        int prunedBudget,
        int axisClampedSplats,
        int axisRatioClampedSplats,
        float[] rawOpacities,
        float[] rawMaxAxisLengths,
        float[] rawAxisRatios,
        float[] emittedOpacities,
        float[] emittedMaxAxisLengths,
        float[] emittedAxisRatios,
        CompilerOptimizationSummary optimization,
        ArtifactRepairSummary? artifactRepair = null,
        ShellSurfaceRepairSummary? shellSurfaceRepair = null,
        SplatPackSphericalInformationStats? sphericalInformation = null,
        SplatPackSplatInjectionReport? injection = null)
    {
        artifactRepair ??= ArtifactRepairSummary.Empty;
        shellSurfaceRepair ??= ShellSurfaceRepairSummary.Empty;
        sphericalInformation ??= SplatPackSphericalInformationStats.Empty;
        injection ??= SplatPackSplatInjectionReport.None;
        Vector3 extents = package.Bounds.Max - package.Bounds.Min;
        int splatStride = SplatPackWriter.SplatStrideForFormat(SplatPackWriter.FormatForOptions(options));
        return new SplatPackBuildReport
        {
            TargetProfile = options.TargetProfile.ToString(),
            RotationOrder = rotationOrder.ToString(),
            Options = new SplatPackBuildReportOptions
            {
                ChunkSize = Math.Max(1, options.ChunkSize),
                OutputFormat = options.OutputFormat.ToString(),
                ShMode = options.ShMode.ToString(),
                FilterMode = options.FilterMode.ToString(),
                PruningMode = options.PruningMode.ToString(),
                SortMode = options.SortMode.ToString(),
                MaxSplats = Math.Max(0, options.MaxSplats),
                OpacityPruneThreshold = MathF.Max(0f, options.OpacityPruneThreshold),
                AxisLengthCap = axisLengthCap,
                MaxAxisLengthPercentile = options.MaxAxisLengthPercentile,
                MaxAxisLengthMultiplier = options.MaxAxisLengthMultiplier,
                MaxAxisRatio = options.MaxAxisRatio,
                ContributionAxisPower = options.ContributionAxisPower,
                ContributionPruneThreshold = options.ContributionPruneThreshold,
                EnableViewSampledImportance = options.EnableViewSampledImportance,
                ViewSampleSource = options.ViewSampleSource,
                ViewSampleCount = options.ViewSamples.Length > 0
                    ? options.ViewSamples.Length
                    : options.EnableViewSampledImportance ? 8 : 0,
                MaxImportanceError = options.MaxImportanceError,
                EnableCoverageCompensation = options.EnableCoverageCompensation,
                MaxCoverageBoost = options.MaxCoverageBoost,
                CoverageAxisPower = options.CoverageAxisPower,
                FarLodKeepFraction = options.FarLodKeepFraction,
                MidLodKeepFraction = options.MidLodKeepFraction,
                EnableAttributeQuantization = options.EnableAttributeQuantization,
                PositionQuantizationBits = options.PositionQuantizationBits,
                AxisQuantizationBits = options.AxisQuantizationBits,
                ColorQuantizationBits = options.ColorQuantizationBits,
                OpacityQuantizationBits = options.OpacityQuantizationBits,
                EnableStreamingLayout = options.EnableStreamingLayout,
                EnableSpatialOutlierPrune = options.EnableSpatialOutlierPrune,
                SpatialOutlierGridResolution = options.EnableSpatialOutlierPrune
                    ? ResolveSpatialOutlierGridResolution(package.Splats.Length, options)
                    : 0,
                SpatialOutlierMinNeighbors = Math.Max(1, options.SpatialOutlierMinNeighbors),
                SpatialOutlierKeepImportance = Math.Clamp(options.SpatialOutlierKeepImportance, 0f, 1f),
                ArtifactRepairMode = options.ArtifactRepairMode.ToString(),
                ProbeResolution = Math.Clamp(options.ProbeResolution, 16, 1024),
                ProbeViewCount = Math.Clamp(options.ProbeViewCount, 1, 128),
                ProbeViewSource = options.ProbeViewSource,
                RepairBudgetRatio = Math.Clamp(options.RepairBudgetRatio, 0f, 0.5f),
                RepairIterations = Math.Clamp(options.RepairIterations, 1, 8),
                ProbeCellSize = Math.Clamp(options.ProbeCellSize, 1, 32),
                ProbeFovYDegrees = Math.Clamp(options.ProbeFovYDegrees, 20f, 140f),
                ProbeAspect = Math.Clamp(options.ProbeAspect, 0.25f, 4f),
                ProbeIpdMeters = Math.Clamp(options.ProbeIpdMeters, 0f, 0.2f),
                ProbeLayerBins = Math.Clamp(options.ProbeLayerBins, 1, 4),
                PullbackWeights = options.PullbackWeights.ToString(),
                EnableSplatInjection = options.EnableSplatInjection,
                InjectionVoxelGrid = Math.Clamp(options.InjectionVoxelGrid, 4, 256),
                InjectionTargetCount = Math.Max(0, options.InjectionTargetCount),
                InjectionMaxFraction = Math.Clamp(options.InjectionMaxFraction, 0f, 1f),
                InjectionNeighborhoodRadiusVoxels = MathF.Max(0.25f, options.InjectionNeighborhoodRadiusVoxels),
                InjectionDeficitThreshold = MathF.Max(0f, options.InjectionDeficitThreshold),
                InjectionAlphaScale = Math.Clamp(options.InjectionAlphaScale, 0f, 4f),
                InjectionScaleFactor = MathF.Max(0.01f, options.InjectionScaleFactor),
            },
            Counts = new SplatPackBuildReportCounts
            {
                InputSplats = rawOpacities.Length + prunedInvalid,
                EmittedSplats = package.Splats.Length,
                PrunedInvalidSplats = prunedInvalid,
                PrunedLowOpacitySplats = prunedLowOpacity,
                PrunedLowContributionSplats = prunedLowContribution,
                PrunedSpatialOutlierSplats = prunedSpatialOutliers,
                PrunedBudgetSplats = prunedBudget,
                AxisClampedSplats = axisClampedSplats,
                AxisRatioClampedSplats = axisRatioClampedSplats,
            },
            Bounds = new SplatPackBuildReportBounds
            {
                Min = ToDto(package.Bounds.Min),
                Max = ToDto(package.Bounds.Max),
                Extents = ToDto(extents),
            },
            RawOpacity = BuildStats(rawOpacities),
            RawMaxAxisLength = BuildStats(rawMaxAxisLengths),
            RawAxisRatio = BuildStats(rawAxisRatios),
            EmittedOpacity = BuildStats(emittedOpacities),
            EmittedMaxAxisLength = BuildStats(emittedMaxAxisLengths),
            EmittedAxisRatio = BuildStats(emittedAxisRatios),
            Chunks = BuildChunkStats(package.Chunks),
            Optimization = new SplatPackCompilerOptimizationStats
            {
                InputImportanceMass = optimization.InputImportanceMass,
                EmittedImportanceMass = optimization.EmittedImportanceMass,
                RetainedImportanceFraction = optimization.InputImportanceMass > 1e-8f
                    ? Math.Clamp(optimization.EmittedImportanceMass / optimization.InputImportanceMass, 0f, 1f)
                    : 1f,
                EstimatedDroppedImportanceFraction = optimization.InputImportanceMass > 1e-8f
                    ? Math.Clamp(1f - optimization.EmittedImportanceMass / optimization.InputImportanceMass, 0f, 1f)
                    : 0f,
                MeanCoverageBoost = optimization.MeanCoverageBoost,
                MaxCoverageBoost = optimization.MaxCoverageBoost,
                LodTier0Splats = optimization.LodTier0Splats,
                LodTier1Splats = optimization.LodTier1Splats,
                LodTier2Splats = optimization.LodTier2Splats,
                QuantizedSplats = optimization.QuantizedSplats,
                StreamingChunks = optimization.StreamingChunks,
            },
            RenderHints = BuildRenderHintStats(package.Splats),
            LayerStats = BuildLayerStats(package.Splats),
            ArtifactRepair = new SplatPackArtifactRepairStats
            {
                Mode = artifactRepair.Mode,
                ProbeResolution = artifactRepair.ProbeResolution,
                ProbeViewCount = artifactRepair.ProbeViewCount,
                InitialBudgetSplats = artifactRepair.InitialBudgetSplats,
                FinalBudgetSplats = artifactRepair.FinalBudgetSplats,
                ResidualCells = artifactRepair.ResidualCells,
                CoverageResidualCells = artifactRepair.CoverageResidualCells,
                EdgeResidualCells = artifactRepair.EdgeResidualCells,
                TextureResidualCells = artifactRepair.TextureResidualCells,
                LayerResidualCells = artifactRepair.LayerResidualCells,
                StereoResidualCells = artifactRepair.StereoResidualCells,
                ReinsertedCoverageSplats = artifactRepair.ReinsertedCoverageSplats,
                ReinsertedEdgeSplats = artifactRepair.ReinsertedEdgeSplats,
                ReinsertedTextureSplats = artifactRepair.ReinsertedTextureSplats,
                ReinsertedLayerSplats = artifactRepair.ReinsertedLayerSplats,
                ReinsertedStereoSplats = artifactRepair.ReinsertedStereoSplats,
                BoostedSplats = artifactRepair.BoostedSplats,
                BoostSaturatedCells = artifactRepair.BoostSaturatedCells,
                LodUpgradedSplats = artifactRepair.LodUpgradedSplats,
                ShPreservedSplats = artifactRepair.ShPreservedSplats,
                QuantizationRelaxedSplats = artifactRepair.QuantizationRelaxedSplats,
                CoverageResidualMean = artifactRepair.CoverageResidualMean,
                EdgeResidualMean = artifactRepair.EdgeResidualMean,
                TextureResidualMean = artifactRepair.TextureResidualMean,
                LayerResidualMean = artifactRepair.LayerResidualMean,
                StereoResidualMean = artifactRepair.StereoResidualMean,
                MaxAppliedBoost = artifactRepair.MaxAppliedBoost,
                ResidualBefore = artifactRepair.ResidualBefore,
                ResidualAfter = artifactRepair.ResidualAfter,
                ResidualReductionRatio = artifactRepair.ResidualReductionRatio,
                RejectedRepairs = artifactRepair.RejectedRepairs,
                StereoMismatchBefore = artifactRepair.StereoMismatchBefore,
                StereoMismatchAfter = artifactRepair.StereoMismatchAfter,
                LayerCrossBoostRejected = artifactRepair.LayerCrossBoostRejected,
            },
            ShellSurfaceRepair = new SplatPackShellSurfaceRepairStats
            {
                Enabled = shellSurfaceRepair.Enabled,
                Mode = shellSurfaceRepair.Mode,
                InitialSelectedSplats = shellSurfaceRepair.InitialSelectedSplats,
                FinalSelectedSplats = shellSurfaceRepair.FinalSelectedSplats,
                ReinsertedSplats = shellSurfaceRepair.ReinsertedSplats,
                ResidualBefore = shellSurfaceRepair.ResidualBefore,
                ResidualAfter = shellSurfaceRepair.ResidualAfter,
                ResidualReductionRatio = shellSurfaceRepair.ResidualReductionRatio,
                SurfaceResidualBefore = shellSurfaceRepair.SurfaceResidualBefore,
                SurfaceResidualAfter = shellSurfaceRepair.SurfaceResidualAfter,
                MicroResidualBefore = shellSurfaceRepair.MicroResidualBefore,
                MicroResidualAfter = shellSurfaceRepair.MicroResidualAfter,
                HighBandResidualBefore = shellSurfaceRepair.HighBandResidualBefore,
                HighBandResidualAfter = shellSurfaceRepair.HighBandResidualAfter,
                LowBandExcessBefore = shellSurfaceRepair.LowBandExcessBefore,
                LowBandExcessAfter = shellSurfaceRepair.LowBandExcessAfter,
                VoxelHoleResidualBefore = shellSurfaceRepair.VoxelHoleResidualBefore,
                VoxelHoleResidualAfter = shellSurfaceRepair.VoxelHoleResidualAfter,
                WeakShellCellsBefore = shellSurfaceRepair.WeakShellCellsBefore,
                WeakShellCellsAfter = shellSurfaceRepair.WeakShellCellsAfter,
            },
            Injection = injection,
            SphericalInformation = sphericalInformation,
            Memory = new SplatPackMemoryStats
            {
                SplatStrideBytes = splatStride,
                ChunkStrideBytes = SplatPackWriter.ChunkStride,
                SplatBytes = (long)package.Splats.Length * splatStride,
                ChunkBytes = (long)package.Chunks.Length * SplatPackWriter.ChunkStride,
                SupportMetadataBytes = (long)package.SupportMetadata.Length * SplatPackWriter.SupportMetadataStride,
                SphericalSupportFieldBytes = (long)package.SphericalSupportField.Length * SplatPackWriter.SphericalSupportFieldStride,
                TotalUncompressedBytes = (long)package.Splats.Length * splatStride
                                         + (long)package.Chunks.Length * SplatPackWriter.ChunkStride
                                         + (long)package.SupportMetadata.Length * SplatPackWriter.SupportMetadataStride
                                         + (long)package.SphericalSupportField.Length * SplatPackWriter.SphericalSupportFieldStride,
                RuntimeVertices = package.Splats.Length * 6,
            },
        };
    }

    private static SplatPackLayerErrorStats BuildLayerStats(SplatPackSplat[] splats)
    {
        float totalMass = 0f;
        float macroMass = 0f;
        float microMass = 0f;
        float fillerMass = 0f;
        float freeSpaceMass = 0f;

        foreach (SplatPackSplat splat in splats)
        {
            int renderClass = DecodeRenderHintClass(splat);
            float opacity = Math.Clamp(splat.Color.W, 0f, 1f);
            float importance = Math.Clamp(splat.Meta.Z, 0f, 1f);
            float mass = MathF.Max(0.0001f, opacity) * (0.45f + importance * 1.75f);
            totalMass += mass;

            if (renderClass == RenderHintBroadSurface || renderClass == RenderHintSurface || renderClass == RenderHintForeground)
            {
                macroMass += mass;
            }

            if (renderClass == RenderHintMicroDetail || renderClass == RenderHintForeground)
            {
                microMass += mass;
            }

            if (renderClass == RenderHintSurface || renderClass == RenderHintBroadSurface)
            {
                fillerMass += mass * (renderClass == RenderHintBroadSurface ? 0.7f : 0.45f);
            }

            if (renderClass == RenderHintFreeSpace || renderClass == RenderHintFloater)
            {
                freeSpaceMass += mass;
            }
        }

        float total = MathF.Max(1e-6f, totalMass);
        float usefulMass = MathF.Max(1e-6f, totalMass - freeSpaceMass);
        return new SplatPackLayerErrorStats
        {
            MacroCoverageMass = macroMass,
            MicroDetailMass = microMass,
            FillerMass = fillerMass,
            FreeSpaceMass = freeSpaceMass,
            MacroErrorProxy = Math.Clamp(1f - macroMass / usefulMass, 0f, 1f),
            MicroErrorProxy = Math.Clamp(1f - microMass / usefulMass, 0f, 1f),
            FreeSpaceFraction = Math.Clamp(freeSpaceMass / total, 0f, 1f),
        };
    }

    private static SplatPackRenderHintStats BuildRenderHintStats(SplatPackSplat[] splats)
    {
        int surface = 0;
        int microDetail = 0;
        int broadSurface = 0;
        int freeSpace = 0;
        int floater = 0;
        int foreground = 0;
        int layerRisk = 0;

        foreach (SplatPackSplat splat in splats)
        {
            int renderClass = DecodeRenderHintClass(splat);
            if (renderClass == RenderHintMicroDetail)
            {
                microDetail++;
            }
            else if (renderClass == RenderHintBroadSurface)
            {
                broadSurface++;
            }
            else if (renderClass == RenderHintFreeSpace)
            {
                freeSpace++;
            }
            else if (renderClass == RenderHintFloater)
            {
                floater++;
            }
            else if (renderClass == RenderHintForeground)
            {
                foreground++;
            }
            else if (renderClass == RenderHintLayerRisk)
            {
                layerRisk++;
            }
            else
            {
                surface++;
            }
        }

        return new SplatPackRenderHintStats
        {
            SurfaceSplats = surface,
            MicroDetailSplats = microDetail,
            BroadSurfaceSplats = broadSurface,
            FreeSpaceSplats = freeSpace,
            FloaterSplats = floater,
            ForegroundSplats = foreground,
            LayerRiskSplats = layerRisk,
        };
    }

    private static int DecodeRenderHintClass(SplatPackSplat splat)
    {
        int packedFlags = Math.Clamp((int)MathF.Round(splat.Meta.Y), 0, 255);
        return (packedFlags >> 1) & 0x7;
    }

    private static SplatPackVector3Dto ToDto(Vector3 value)
    {
        return new SplatPackVector3Dto { X = value.X, Y = value.Y, Z = value.Z };
    }

    private static SplatPackChunkStats BuildChunkStats(SplatPackChunk[] chunks)
    {
        if (chunks.Length == 0)
        {
            return new SplatPackChunkStats { Count = 0, MinSplats = 0, MaxSplats = 0, MeanSplats = 0f };
        }

        int min = int.MaxValue;
        int max = 0;
        long sum = 0;
        foreach (SplatPackChunk chunk in chunks)
        {
            min = Math.Min(min, chunk.SplatCount);
            max = Math.Max(max, chunk.SplatCount);
            sum += chunk.SplatCount;
        }

        return new SplatPackChunkStats
        {
            Count = chunks.Length,
            MinSplats = min,
            MaxSplats = max,
            MeanSplats = sum / (float)chunks.Length,
        };
    }

    private static SplatPackScalarStats BuildStats(float[] values)
    {
        if (values.Length == 0)
        {
            return new SplatPackScalarStats { Count = 0, Min = 0f, P50 = 0f, P90 = 0f, P95 = 0f, P99 = 0f, Max = 0f, Mean = 0f };
        }

        var sorted = (float[])values.Clone();
        Array.Sort(sorted);
        double sum = 0.0;
        foreach (float value in sorted)
        {
            sum += value;
        }

        return new SplatPackScalarStats
        {
            Count = sorted.Length,
            Min = sorted[0],
            P50 = PercentileSorted(sorted, 50f),
            P90 = PercentileSorted(sorted, 90f),
            P95 = PercentileSorted(sorted, 95f),
            P99 = PercentileSorted(sorted, 99f),
            Max = sorted[^1],
            Mean = (float)(sum / sorted.Length),
        };
    }

    private static float Percentile(float[] values, float percentile)
    {
        if (values.Length == 0)
        {
            return 0f;
        }

        var sorted = (float[])values.Clone();
        Array.Sort(sorted);
        return PercentileSorted(sorted, percentile);
    }

    private static float PercentileSorted(float[] sorted, float percentile)
    {
        if (sorted.Length == 0)
        {
            return 0f;
        }

        float clamped = Math.Clamp(percentile, 0f, 100f);
        int index = Math.Clamp((int)MathF.Round((sorted.Length - 1) * clamped / 100f), 0, sorted.Length - 1);
        return sorted[index];
    }

    // Direction C MVP: post-pass class-aware floater drop.
    //
    // Walks the assembled package, removes splats whose render-hint class is in
    // the configured drop set, and rebuilds all per-splat metadata + chunk
    // offsets to match. This is a destructive change to the package file: once
    // dropped, those splats no longer exist at runtime so no profile tuning is
    // required to suppress them.
    //
    // ClassDropSupportProtect lets SSPF-supported splats survive even when they
    // would otherwise be dropped by class. Zero disables the protection.
    private static ClassDropSummary ApplyClassDrop(ref SplatPackPackage package, SplatPackBuildOptions options)
    {
        if (package.Splats.Length == 0)
        {
            return ClassDropSummary.Empty;
        }

        bool dropFree = options.DropFreeSpaceClass;
        bool dropFloater = options.DropFloaterClass;
        bool dropLayer = options.DropLayerRiskClass;
        if (!dropFree && !dropFloater && !dropLayer)
        {
            return ClassDropSummary.Empty;
        }

        float supportProtect = MathF.Max(0f, options.ClassDropSupportProtect);
        bool useSupport = supportProtect > 0f && package.SupportMetadata.Length > 0;

        int originalCount = package.Splats.Length;
        bool[] keep = new bool[originalCount];
        int[] oldToNew = new int[originalCount];
        int droppedFree = 0;
        int droppedFloater = 0;
        int droppedLayer = 0;
        int protectedBySupport = 0;

        for (int i = 0; i < originalCount; i++)
        {
            int flags = Math.Clamp((int)MathF.Round(package.Splats[i].Meta.Y), 0, 255);
            int renderClass = (flags >> 1) & 0x7;
            bool drop = renderClass switch
            {
                RenderHintFreeSpace when dropFree => true,
                RenderHintFloater when dropFloater => true,
                RenderHintLayerRisk when dropLayer => true,
                _ => false,
            };

            if (drop && useSupport && i < package.SupportMetadata.Length)
            {
                float supportScore = DecodeSupportConfidence(package.SupportMetadata[i]);
                if (supportScore >= supportProtect)
                {
                    drop = false;
                    protectedBySupport++;
                }
            }

            keep[i] = !drop;
            if (drop)
            {
                switch (renderClass)
                {
                    case RenderHintFreeSpace: droppedFree++; break;
                    case RenderHintFloater: droppedFloater++; break;
                    case RenderHintLayerRisk: droppedLayer++; break;
                }
            }
        }

        int newCount = 0;
        for (int i = 0; i < originalCount; i++)
        {
            if (!keep[i])
            {
                oldToNew[i] = -1;
                continue;
            }
            oldToNew[i] = newCount;
            newCount++;
        }

        if (newCount == originalCount)
        {
            return new ClassDropSummary(droppedFree, droppedFloater, droppedLayer, protectedBySupport, supportProtect);
        }

        SplatPackSplat[] newSplats = new SplatPackSplat[newCount];
        int sh3PerSplat = package.Sh3Coefficients.Length > 0 && originalCount > 0
            ? package.Sh3Coefficients.Length / originalCount
            : 0;
        Vector4[] newSh3 = sh3PerSplat > 0
            ? new Vector4[newCount * sh3PerSplat]
            : Array.Empty<Vector4>();
        Vector4[] newResearch = package.ResearchMetadata.Length > 0
            ? new Vector4[newCount]
            : Array.Empty<Vector4>();
        Vector4[] newArtifact = package.ArtifactRepairMetadata.Length > 0
            ? new Vector4[newCount]
            : Array.Empty<Vector4>();
        SplatPackSupportMetadata[] newSupport = package.SupportMetadata.Length > 0
            ? new SplatPackSupportMetadata[newCount]
            : Array.Empty<SplatPackSupportMetadata>();

        int writeIndex = 0;
        for (int i = 0; i < originalCount; i++)
        {
            if (!keep[i])
            {
                continue;
            }
            newSplats[writeIndex] = package.Splats[i];
            if (sh3PerSplat > 0)
            {
                Array.Copy(
                    package.Sh3Coefficients,
                    i * sh3PerSplat,
                    newSh3,
                    writeIndex * sh3PerSplat,
                    sh3PerSplat);
            }
            if (newResearch.Length > 0 && i < package.ResearchMetadata.Length)
            {
                newResearch[writeIndex] = package.ResearchMetadata[i];
            }
            if (newArtifact.Length > 0 && i < package.ArtifactRepairMetadata.Length)
            {
                newArtifact[writeIndex] = package.ArtifactRepairMetadata[i];
            }
            if (newSupport.Length > 0 && i < package.SupportMetadata.Length)
            {
                newSupport[writeIndex] = package.SupportMetadata[i];
            }
            writeIndex++;
        }

        var newChunks = new List<SplatPackChunk>(package.Chunks.Length);
        foreach (SplatPackChunk chunk in package.Chunks)
        {
            int newOffset = -1;
            int kept = 0;
            int end = chunk.SplatOffset + chunk.SplatCount;
            for (int i = chunk.SplatOffset; i < end; i++)
            {
                if (!keep[i])
                {
                    continue;
                }
                if (newOffset < 0)
                {
                    newOffset = oldToNew[i];
                }
                kept++;
            }

            if (kept > 0)
            {
                newChunks.Add(new SplatPackChunk(chunk.BoundsMin, chunk.BoundsMax, newOffset, kept, chunk.LodLevel));
            }
        }

        package = new SplatPackPackage
        {
            Bounds = package.Bounds,
            Chunks = newChunks.ToArray(),
            Splats = newSplats,
            Sh3Coefficients = newSh3,
            ResearchMetadata = newResearch,
            ArtifactRepairMetadata = newArtifact,
            CellArtifactMetadata = package.CellArtifactMetadata,
            CellArtifactMetadataStride = package.CellArtifactMetadataStride,
            SupportMetadata = newSupport,
            SphericalSupportField = package.SphericalSupportField,
            SphericalSupportFieldStride = package.SphericalSupportFieldStride,
        };

        return new ClassDropSummary(droppedFree, droppedFloater, droppedLayer, protectedBySupport, supportProtect);
    }

    // Direction C: field-aware pruning post-pass.
    //
    // Voxelizes each splat's contribution into a 3D density grid (weighted by
    // alpha and a Gaussian falloff over its extent), then scores each splat by
    // the Gaussian-weighted density of its neighborhood (minus its own central
    // contribution). Splats with the lowest field support are isolated floaters
    // and get dropped, except for splats whose alpha exceeds FieldProtectAlpha
    // (e.g. lone bright foreground points).
    private static FieldPruneSummary ApplyFieldAwarePruning(ref SplatPackPackage package, SplatPackBuildOptions options)
    {
        int originalCount = package.Splats.Length;
        if (originalCount == 0)
        {
            return FieldPruneSummary.Empty;
        }

        int grid = Math.Max(4, options.FieldVoxelGrid);
        float pruneFraction = Math.Clamp(options.FieldPruneFraction, 0f, 0.95f);
        float neighborhoodRadius = MathF.Max(0.25f, options.FieldNeighborhoodRadiusVoxels);
        float protectAlpha = Math.Clamp(options.FieldProtectAlpha, 0f, 1f);

        Vector3 boundsMin = package.Bounds.Min;
        Vector3 boundsMax = package.Bounds.Max;
        Vector3 boundsSize = boundsMax - boundsMin;
        Vector3 voxelSize = new(
            MathF.Max(MinimumScale, boundsSize.X / grid),
            MathF.Max(MinimumScale, boundsSize.Y / grid),
            MathF.Max(MinimumScale, boundsSize.Z / grid));

        // Center-based density: each splat contributes alpha to its CENTER voxel only.
        // This avoids the large-isolated-splat "self-protection" problem where a single
        // splat spreads density across many voxels and inflates its own neighborhood
        // score above legitimate dense clusters of small splats.
        float[] density = new float[grid * grid * grid];
        int[] centerVoxelIndex = new int[originalCount];
        Vector3[] positions = new Vector3[originalCount];
        float[] alphas = new float[originalCount];
        float[] extents = new float[originalCount];

        for (int i = 0; i < originalCount; i++)
        {
            SplatPackSplat splat = package.Splats[i];
            Vector3 pos = new(splat.CenterWS.X, splat.CenterWS.Y, splat.CenterWS.Z);
            float alpha = Math.Clamp(splat.Color.W, 0f, 1f);
            float ax0 = new Vector3(splat.Axis0WS.X, splat.Axis0WS.Y, splat.Axis0WS.Z).Length();
            float ax1 = new Vector3(splat.Axis1WS.X, splat.Axis1WS.Y, splat.Axis1WS.Z).Length();
            float ax2 = new Vector3(splat.Axis2WS.X, splat.Axis2WS.Y, splat.Axis2WS.Z).Length();
            float extent = MathF.Max(MinimumScale, MathF.Max(ax0, MathF.Max(ax1, ax2)));
            positions[i] = pos;
            alphas[i] = alpha;
            extents[i] = extent;

            int vx = Math.Clamp((int)MathF.Floor((pos.X - boundsMin.X) / voxelSize.X), 0, grid - 1);
            int vy = Math.Clamp((int)MathF.Floor((pos.Y - boundsMin.Y) / voxelSize.Y), 0, grid - 1);
            int vz = Math.Clamp((int)MathF.Floor((pos.Z - boundsMin.Z) / voxelSize.Z), 0, grid - 1);
            int centerIdx = (vz * grid + vy) * grid + vx;
            centerVoxelIndex[i] = centerIdx;
            density[centerIdx] += alpha;
        }

        float[] scores = new float[originalCount];
        int radiusVoxels = Math.Max(1, (int)MathF.Ceiling(neighborhoodRadius));
        float invRadiusSq = 1f / (neighborhoodRadius * neighborhoodRadius);
        double meanAccumulator = 0.0;

        for (int i = 0; i < originalCount; i++)
        {
            Vector3 pos = positions[i];
            int vx = Math.Clamp((int)MathF.Floor((pos.X - boundsMin.X) / voxelSize.X), 0, grid - 1);
            int vy = Math.Clamp((int)MathF.Floor((pos.Y - boundsMin.Y) / voxelSize.Y), 0, grid - 1);
            int vz = Math.Clamp((int)MathF.Floor((pos.Z - boundsMin.Z) / voxelSize.Z), 0, grid - 1);
            float score = 0f;

            for (int dz = -radiusVoxels; dz <= radiusVoxels; dz++)
            {
                int z = vz + dz;
                if (z < 0 || z >= grid)
                {
                    continue;
                }
                for (int dy = -radiusVoxels; dy <= radiusVoxels; dy++)
                {
                    int y = vy + dy;
                    if (y < 0 || y >= grid)
                    {
                        continue;
                    }
                    for (int dx = -radiusVoxels; dx <= radiusVoxels; dx++)
                    {
                        int x = vx + dx;
                        if (x < 0 || x >= grid)
                        {
                            continue;
                        }
                        float distSq = dx * dx + dy * dy + dz * dz;
                        if (distSq > neighborhoodRadius * neighborhoodRadius)
                        {
                            continue;
                        }
                        float w = MathF.Exp(-0.5f * distSq * invRadiusSq);
                        score += density[(z * grid + y) * grid + x] * w;
                    }
                }
            }

            score -= alphas[i];
            if (score < 0f)
            {
                score = 0f;
            }
            scores[i] = score;
            meanAccumulator += score;
        }

        float meanScore = (float)(meanAccumulator / originalCount);

        int targetDropCount = (int)MathF.Floor(originalCount * pruneFraction);
        if (targetDropCount <= 0)
        {
            return new FieldPruneSummary(grid, pruneFraction, 0, originalCount, scores.Min(), 0f, meanScore, 0);
        }

        int[] order = new int[originalCount];
        for (int i = 0; i < originalCount; i++)
        {
            order[i] = i;
        }
        Array.Sort(order, (a, b) => scores[a].CompareTo(scores[b]));

        bool[] keep = new bool[originalCount];
        for (int i = 0; i < originalCount; i++)
        {
            keep[i] = true;
        }

        int droppedCount = 0;
        int protectedByAlpha = 0;
        float highestDroppedScore = 0f;
        for (int rank = 0; rank < originalCount && droppedCount < targetDropCount; rank++)
        {
            int idx = order[rank];
            if (alphas[idx] > protectAlpha)
            {
                protectedByAlpha++;
                continue;
            }
            keep[idx] = false;
            droppedCount++;
            if (scores[idx] > highestDroppedScore)
            {
                highestDroppedScore = scores[idx];
            }
        }

        float lowestRetainedScore = float.PositiveInfinity;
        for (int i = 0; i < originalCount; i++)
        {
            if (keep[i] && scores[i] < lowestRetainedScore)
            {
                lowestRetainedScore = scores[i];
            }
        }
        if (float.IsPositiveInfinity(lowestRetainedScore))
        {
            lowestRetainedScore = 0f;
        }

        if (droppedCount == 0)
        {
            return new FieldPruneSummary(grid, pruneFraction, 0, originalCount, lowestRetainedScore, 0f, meanScore, protectedByAlpha);
        }

        int newCount = originalCount - droppedCount;
        int[] oldToNew = new int[originalCount];
        int writeIndex = 0;
        for (int i = 0; i < originalCount; i++)
        {
            if (!keep[i])
            {
                oldToNew[i] = -1;
                continue;
            }
            oldToNew[i] = writeIndex++;
        }

        SplatPackSplat[] newSplats = new SplatPackSplat[newCount];
        int sh3PerSplat = package.Sh3Coefficients.Length > 0 && originalCount > 0
            ? package.Sh3Coefficients.Length / originalCount
            : 0;
        Vector4[] newSh3 = sh3PerSplat > 0
            ? new Vector4[newCount * sh3PerSplat]
            : Array.Empty<Vector4>();
        Vector4[] newResearch = package.ResearchMetadata.Length > 0
            ? new Vector4[newCount]
            : Array.Empty<Vector4>();
        Vector4[] newArtifact = package.ArtifactRepairMetadata.Length > 0
            ? new Vector4[newCount]
            : Array.Empty<Vector4>();
        SplatPackSupportMetadata[] newSupport = package.SupportMetadata.Length > 0
            ? new SplatPackSupportMetadata[newCount]
            : Array.Empty<SplatPackSupportMetadata>();

        writeIndex = 0;
        for (int i = 0; i < originalCount; i++)
        {
            if (!keep[i])
            {
                continue;
            }
            newSplats[writeIndex] = package.Splats[i];
            if (sh3PerSplat > 0)
            {
                Array.Copy(
                    package.Sh3Coefficients,
                    i * sh3PerSplat,
                    newSh3,
                    writeIndex * sh3PerSplat,
                    sh3PerSplat);
            }
            if (newResearch.Length > 0 && i < package.ResearchMetadata.Length)
            {
                newResearch[writeIndex] = package.ResearchMetadata[i];
            }
            if (newArtifact.Length > 0 && i < package.ArtifactRepairMetadata.Length)
            {
                newArtifact[writeIndex] = package.ArtifactRepairMetadata[i];
            }
            if (newSupport.Length > 0 && i < package.SupportMetadata.Length)
            {
                newSupport[writeIndex] = package.SupportMetadata[i];
            }
            writeIndex++;
        }

        var newChunks = new List<SplatPackChunk>(package.Chunks.Length);
        foreach (SplatPackChunk chunk in package.Chunks)
        {
            int newOffset = -1;
            int kept = 0;
            int end = chunk.SplatOffset + chunk.SplatCount;
            for (int i = chunk.SplatOffset; i < end; i++)
            {
                if (!keep[i])
                {
                    continue;
                }
                if (newOffset < 0)
                {
                    newOffset = oldToNew[i];
                }
                kept++;
            }

            if (kept > 0)
            {
                newChunks.Add(new SplatPackChunk(chunk.BoundsMin, chunk.BoundsMax, newOffset, kept, chunk.LodLevel));
            }
        }

        package = new SplatPackPackage
        {
            Bounds = package.Bounds,
            Chunks = newChunks.ToArray(),
            Splats = newSplats,
            Sh3Coefficients = newSh3,
            ResearchMetadata = newResearch,
            ArtifactRepairMetadata = newArtifact,
            CellArtifactMetadata = package.CellArtifactMetadata,
            CellArtifactMetadataStride = package.CellArtifactMetadataStride,
            SupportMetadata = newSupport,
            SphericalSupportField = package.SphericalSupportField,
            SphericalSupportFieldStride = package.SphericalSupportFieldStride,
        };

        return new FieldPruneSummary(grid, pruneFraction, droppedCount, newCount, lowestRetainedScore, highestDroppedScore, meanScore, protectedByAlpha);
    }

    private static float DecodeSupportConfidence(SplatPackSupportMetadata metadata)
    {
        // SupportMetadata layout (see BuildSupportMetadata): Packed1 holds the
        // conservative SSPF support confidence in its lowest byte (0..255).
        uint confidenceByte = metadata.Packed1 & 0xFFu;
        return confidenceByte / 255f;
    }

    // Surface-plane estimator. Given a set of weighted 3D points, returns the
    // weighted centroid and the unit normal of the best-fit plane (smallest
    // principal direction of the weighted covariance). Used by the surface-
    // aware injection path to project the candidate position onto a local
    // surface tangent plane rather than the geometric voxel centre.
    private static (Vector3 Centroid, Vector3 Normal) EstimateSurfacePlane(
        Vector3[] points, float[] weights, int count)
    {
        if (count < 3)
        {
            return (points.Length > 0 ? points[0] : Vector3.Zero, Vector3.UnitZ);
        }

        float wSum = 0f;
        Vector3 centroid = Vector3.Zero;
        for (int i = 0; i < count; i++)
        {
            float w = MathF.Max(1e-4f, weights[i]);
            centroid += points[i] * w;
            wSum += w;
        }
        centroid /= wSum;

        // Weighted 3x3 covariance (symmetric).
        float c00 = 0f, c01 = 0f, c02 = 0f;
        float c11 = 0f, c12 = 0f, c22 = 0f;
        for (int i = 0; i < count; i++)
        {
            Vector3 d = points[i] - centroid;
            float w = MathF.Max(1e-4f, weights[i]);
            c00 += w * d.X * d.X;
            c01 += w * d.X * d.Y;
            c02 += w * d.X * d.Z;
            c11 += w * d.Y * d.Y;
            c12 += w * d.Y * d.Z;
            c22 += w * d.Z * d.Z;
        }
        float invW = 1f / wSum;
        c00 *= invW; c01 *= invW; c02 *= invW;
        c11 *= invW; c12 *= invW; c22 *= invW;

        // Closed-form eigenvalues of a symmetric 3x3 (Smith 1961).
        float trace = c00 + c11 + c22;
        float q = trace / 3f;
        float p1 = c01 * c01 + c02 * c02 + c12 * c12;
        float p2 = (c00 - q) * (c00 - q) + (c11 - q) * (c11 - q) + (c22 - q) * (c22 - q) + 2f * p1;
        if (p2 <= 1e-20f)
        {
            // Matrix is (numerically) isotropic; pick canonical normal.
            return (centroid, Vector3.UnitZ);
        }
        float p = MathF.Sqrt(p2 / 6f);
        float b00 = (c00 - q) / p;
        float b01 = c01 / p;
        float b02 = c02 / p;
        float b11 = (c11 - q) / p;
        float b12 = c12 / p;
        float b22 = (c22 - q) / p;
        float detB =
              b00 * (b11 * b22 - b12 * b12)
            - b01 * (b01 * b22 - b12 * b02)
            + b02 * (b01 * b12 - b11 * b02);
        float r = Math.Clamp(detB / 2f, -1f, 1f);
        float phi = MathF.Acos(r) / 3f;
        float eig1 = q + 2f * p * MathF.Cos(phi);
        float eig3 = q + 2f * p * MathF.Cos(phi + 2f * MathF.PI / 3f);
        // eig2 implicit from trace, not needed: we want the smallest eigenvector.

        // Smallest eigenvalue is eig3. Solve (C - eig3*I) n = 0 by taking the
        // cross product of two columns of (C - eig3*I); the row with the
        // largest magnitude product gives the most numerically stable normal.
        float m00 = c00 - eig3, m11 = c11 - eig3, m22 = c22 - eig3;
        Vector3 row0 = new(m00, c01, c02);
        Vector3 row1 = new(c01, m11, c12);
        Vector3 row2 = new(c02, c12, m22);
        Vector3 candidate = Vector3.Cross(row0, row1);
        float bestLen2 = candidate.LengthSquared();
        Vector3 c02v = Vector3.Cross(row0, row2);
        if (c02v.LengthSquared() > bestLen2) { candidate = c02v; bestLen2 = c02v.LengthSquared(); }
        Vector3 c12v = Vector3.Cross(row1, row2);
        if (c12v.LengthSquared() > bestLen2) { candidate = c12v; bestLen2 = c12v.LengthSquared(); }
        if (bestLen2 <= 1e-20f)
        {
            // Degenerate — points colinear or coplanar with no clear normal.
            return (centroid, Vector3.UnitZ);
        }
        Vector3 normal = candidate / MathF.Sqrt(bestLen2);
        // Eig1 (largest variance) is not needed downstream; this method only
        // exposes centroid + normal for the surface-aware projection.
        _ = eig1;
        return (centroid, normal);
    }

    private static (Vector3 InPlane0, Vector3 InPlane1) OrthonormalInPlaneBasis(Vector3 normal)
    {
        Vector3 abs = new(MathF.Abs(normal.X), MathF.Abs(normal.Y), MathF.Abs(normal.Z));
        Vector3 seed = abs.X <= abs.Y && abs.X <= abs.Z
            ? Vector3.UnitX
            : (abs.Y <= abs.Z ? Vector3.UnitY : Vector3.UnitZ);
        Vector3 u = Vector3.Cross(normal, seed);
        float uLen2 = u.LengthSquared();
        if (uLen2 < 1e-12f)
        {
            return (Vector3.UnitX, Vector3.UnitY);
        }
        u /= MathF.Sqrt(uLen2);
        Vector3 v = Vector3.Cross(normal, u);
        return (u, v);
    }

    // Scene-centricity classifier. Returns true iff the splat distribution
    // looks object-centric: opacity-weighted 5%--95% inter-percentile span is
    // small relative to the bounding box (single concentrated object) on at
    // least two of three axes. Scene-centric inputs (heavy background
    // contamination, e.g. j0n45) spread their opacity mass across the full
    // bbox on all three axes and fail this test.
    //
    // Used as a precondition for synthetic splat injection: applying the
    // injection synthesis operator to a scene-centric capture mistakes
    // background contamination clusters for object neighbourhoods and seeds
    // new Gaussians inside background voxels, fragmenting the spectral
    // fidelity score. See Section "Failure mode: background contamination"
    // in the PG 2026 paper for the motivating evaluation on j0n45.
    private static bool IsObjectCentric(in SplatPackPackage package, float coreFractionThreshold)
    {
        int n = package.Splats.Length;
        if (n < 1000) return true; // not enough samples; default to object-centric
        float[] x = new float[n];
        float[] y = new float[n];
        float[] z = new float[n];
        float[] w = new float[n];
        float wSum = 0f;
        for (int i = 0; i < n; i++)
        {
            SplatPackSplat s = package.Splats[i];
            x[i] = s.CenterWS.X;
            y[i] = s.CenterWS.Y;
            z[i] = s.CenterWS.Z;
            // Opacity-weighted percentile: heavier splats count more, so a
            // bbox-spanning cloud of weak background contamination still
            // dominates the spread metric. Clamp to avoid zero weights.
            w[i] = MathF.Max(1e-4f, Math.Clamp(s.Color.W, 0f, 1f));
            wSum += w[i];
        }
        Vector3 bboxMin = package.Bounds.Min;
        Vector3 bboxMax = package.Bounds.Max;
        Vector3 bboxSpan = bboxMax - bboxMin;
        float scoreX = WeightedInterPercentileSpan(x, w, wSum, 0.05f, 0.95f) / MathF.Max(MinimumScale, bboxSpan.X);
        float scoreY = WeightedInterPercentileSpan(y, w, wSum, 0.05f, 0.95f) / MathF.Max(MinimumScale, bboxSpan.Y);
        float scoreZ = WeightedInterPercentileSpan(z, w, wSum, 0.05f, 0.95f) / MathF.Max(MinimumScale, bboxSpan.Z);
        // Object-centric: at least two of three axes have core-span well
        // below the bbox span. Equivalent to: at most one axis can be
        // bbox-spanning (object captures often elongate along one axis,
        // but rarely all three).
        int axesWithinThreshold = 0;
        if (scoreX <= coreFractionThreshold) axesWithinThreshold++;
        if (scoreY <= coreFractionThreshold) axesWithinThreshold++;
        if (scoreZ <= coreFractionThreshold) axesWithinThreshold++;
        return axesWithinThreshold >= 2;
    }

    private static float WeightedInterPercentileSpan(float[] values, float[] weights, float weightSum, float lowQ, float highQ)
    {
        int n = values.Length;
        int[] indices = new int[n];
        for (int i = 0; i < n; i++) indices[i] = i;
        Array.Sort(indices, (a, b) => values[a].CompareTo(values[b]));
        float lowMass = lowQ * weightSum;
        float highMass = highQ * weightSum;
        float cumWeight = 0f;
        float lowValue = values[indices[0]];
        float highValue = values[indices[n - 1]];
        bool lowAssigned = false;
        for (int k = 0; k < n; k++)
        {
            int idx = indices[k];
            cumWeight += weights[idx];
            if (!lowAssigned && cumWeight >= lowMass)
            {
                lowValue = values[idx];
                lowAssigned = true;
            }
            if (cumWeight >= highMass)
            {
                highValue = values[idx];
                break;
            }
        }
        return highValue - lowValue;
    }

    // Direction C: synthetic splat injection post-pass.
    //
    // Voxelizes existing splat density into a 3D coverage field, computes an
    // expected coverage value at each voxel from its 3x3x3 neighborhood mean,
    // and identifies under-served voxels (deficit relative to expectation) and
    // dead voxels (zero coverage where neighbors are populated). For each
    // selected voxel we synthesize a new splat from inverse-distance-weighted
    // neighbor properties and append the entire batch to the package as a new
    // trailing chunk. All per-splat metadata arrays (SH3, research, artifact,
    // support) are resized in lockstep so the .splatpack remains self-consistent.
    //
    // Unlike pruning passes this *expands* the package, so the new splats are
    // collected into a single trailing chunk to avoid touching the streaming
    // layout established earlier in the pipeline.
    private static SplatInjectionSummary ApplySplatInjection(ref SplatPackPackage package, SplatPackBuildOptions options, int hardBudgetOverride = 0)
    {
        int originalCount = package.Splats.Length;
        if (originalCount == 0)
        {
            return SplatInjectionSummary.Empty;
        }

        int grid = Math.Clamp(options.InjectionVoxelGrid, 4, 256);
        float neighborhoodRadius = MathF.Max(0.25f, options.InjectionNeighborhoodRadiusVoxels);
        float deficitThreshold = MathF.Max(0f, options.InjectionDeficitThreshold);
        float alphaScale = Math.Clamp(options.InjectionAlphaScale, 0f, 4f);
        float scaleFactor = MathF.Max(0.01f, options.InjectionScaleFactor);

        int desiredCount;
        if (hardBudgetOverride > 0)
        {
            desiredCount = hardBudgetOverride;
        }
        else if (options.InjectionTargetCount > 0)
        {
            desiredCount = options.InjectionTargetCount;
        }
        else
        {
            float maxFraction = Math.Clamp(options.InjectionMaxFraction, 0f, 1f);
            desiredCount = (int)MathF.Floor(originalCount * maxFraction);
        }
        if (desiredCount <= 0)
        {
            return new SplatInjectionSummary(grid, 0, 0, 0, 0f, deficitThreshold);
        }

        Vector3 boundsMin = package.Bounds.Min;
        Vector3 boundsMax = package.Bounds.Max;
        Vector3 boundsSize = boundsMax - boundsMin;
        Vector3 voxelSize = new(
            MathF.Max(MinimumScale, boundsSize.X / grid),
            MathF.Max(MinimumScale, boundsSize.Y / grid),
            MathF.Max(MinimumScale, boundsSize.Z / grid));

        // Step 1: voxel coverage grid. Each splat contributes alpha * area_proxy
        // to its center voxel, mirroring ApplyFieldAwarePruning's center-based
        // density (so isolated splats can't self-protect via large extent).
        float[] density = new float[grid * grid * grid];
        Vector3[] positions = new Vector3[originalCount];
        float[] alphas = new float[originalCount];
        float[] extents = new float[originalCount];
        int[] centerVoxelIndex = new int[originalCount];

        for (int i = 0; i < originalCount; i++)
        {
            SplatPackSplat splat = package.Splats[i];
            Vector3 pos = new(splat.CenterWS.X, splat.CenterWS.Y, splat.CenterWS.Z);
            float alpha = Math.Clamp(splat.Color.W, 0f, 1f);
            float ax0 = new Vector3(splat.Axis0WS.X, splat.Axis0WS.Y, splat.Axis0WS.Z).Length();
            float ax1 = new Vector3(splat.Axis1WS.X, splat.Axis1WS.Y, splat.Axis1WS.Z).Length();
            float ax2 = new Vector3(splat.Axis2WS.X, splat.Axis2WS.Y, splat.Axis2WS.Z).Length();
            float extent = MathF.Max(MinimumScale, MathF.Max(ax0, MathF.Max(ax1, ax2)));
            float area = MathF.Max(MinimumScale, ax0 * ax1 + ax1 * ax2 + ax0 * ax2);
            positions[i] = pos;
            alphas[i] = alpha;
            extents[i] = extent;

            int vx = Math.Clamp((int)MathF.Floor((pos.X - boundsMin.X) / voxelSize.X), 0, grid - 1);
            int vy = Math.Clamp((int)MathF.Floor((pos.Y - boundsMin.Y) / voxelSize.Y), 0, grid - 1);
            int vz = Math.Clamp((int)MathF.Floor((pos.Z - boundsMin.Z) / voxelSize.Z), 0, grid - 1);
            int centerIdx = (vz * grid + vy) * grid + vx;
            centerVoxelIndex[i] = centerIdx;
            density[centerIdx] += alpha * area;
        }

        // Step 2: expected coverage per voxel = mean of 3x3x3 neighborhood.
        float[] expected = new float[grid * grid * grid];
        for (int z = 0; z < grid; z++)
        {
            for (int y = 0; y < grid; y++)
            {
                for (int x = 0; x < grid; x++)
                {
                    float sum = 0f;
                    int count = 0;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int zz = z + dz;
                        if (zz < 0 || zz >= grid)
                        {
                            continue;
                        }
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int yy = y + dy;
                            if (yy < 0 || yy >= grid)
                            {
                                continue;
                            }
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = x + dx;
                                if (xx < 0 || xx >= grid)
                                {
                                    continue;
                                }
                                sum += density[(zz * grid + yy) * grid + xx];
                                count++;
                            }
                        }
                    }
                    expected[(z * grid + y) * grid + x] = count > 0 ? sum / count : 0f;
                }
            }
        }

        // Step 3: identify under-served and dead voxels. Dead = no density at
        // all but expectation says there should be coverage; under-served =
        // density is below threshold * expectation.
        //
        // CRITICAL: also require the voxel to be TOPOLOGICALLY ENCLOSED — at
        // least MinEnclosingNeighbors of its 26 neighbours must be populated.
        // Without this guard, the naive expected-coverage heuristic flags the
        // empty air around an object as "dead" (since neighbours of populated
        // voxels exist on every side of a surface fragment) and we end up
        // injecting splats into the void. The enclosing test restricts holes
        // to genuine cavities/voids INSIDE the captured surface.
        const int MinEnclosingNeighbors = 6;
        float deadExpectationFloor = MinimumScale;
        var candidates = new List<(int VoxelIndex, float Deficit, bool IsDead)>(grid * grid);
        int underServedCount = 0;
        int deadCount = 0;
        for (int idx = 0; idx < expected.Length; idx++)
        {
            float exp = expected[idx];
            if (exp <= deadExpectationFloor)
            {
                continue;
            }
            float dens = density[idx];
            float deficit = exp - dens;
            bool isDead = dens <= 0f;
            bool isUnderServed = deficit > deficitThreshold * exp;
            if (!isUnderServed && !isDead)
            {
                continue;
            }

            // Count populated neighbours in 26-connected cube.
            int z = idx / (grid * grid);
            int y = (idx / grid) % grid;
            int x = idx % grid;
            int populatedNeighbors = 0;
            for (int dz = -1; dz <= 1 && populatedNeighbors < MinEnclosingNeighbors; dz++)
            {
                int nz = z + dz;
                if (nz < 0 || nz >= grid) continue;
                for (int dy = -1; dy <= 1 && populatedNeighbors < MinEnclosingNeighbors; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= grid) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;
                        int nx = x + dx;
                        if (nx < 0 || nx >= grid) continue;
                        if (density[(nz * grid + ny) * grid + nx] > 0f)
                        {
                            populatedNeighbors++;
                            if (populatedNeighbors >= MinEnclosingNeighbors) break;
                        }
                    }
                }
            }
            if (populatedNeighbors < MinEnclosingNeighbors)
            {
                continue;
            }

            if (isDead)
            {
                deadCount++;
            }
            else
            {
                underServedCount++;
            }
            candidates.Add((idx, deficit, isDead));
        }

        if (candidates.Count == 0)
        {
            return new SplatInjectionSummary(grid, 0, underServedCount, deadCount, 0f, deficitThreshold);
        }

        // Step 4: sort by deficit descending (dead voxels also prioritized by
        // their nominal expectation since dead means deficit == expected).
        candidates.Sort((a, b) => b.Deficit.CompareTo(a.Deficit));

        // Step 5: For each top voxel, find K nearest existing splats inside the
        // neighborhood radius and interpolate properties via inverse-distance
        // weighting.
        int sh3PerSplat = package.Sh3Coefficients.Length > 0 && originalCount > 0
            ? package.Sh3Coefficients.Length / originalCount
            : 0;

        // Group existing splats by voxel for fast neighbor lookup.
        var voxelToSplats = new Dictionary<int, List<int>>(originalCount / 8 + 1);
        for (int i = 0; i < originalCount; i++)
        {
            int key = centerVoxelIndex[i];
            if (!voxelToSplats.TryGetValue(key, out List<int>? bucket))
            {
                bucket = new List<int>();
                voxelToSplats.Add(key, bucket);
            }
            bucket.Add(i);
        }

        int radiusVoxels = Math.Max(1, (int)MathF.Ceiling(neighborhoodRadius));
        int maxToInject = Math.Min(desiredCount, candidates.Count);
        var injectedSplats = new List<SplatPackSplat>(maxToInject);
        var injectedSh3 = sh3PerSplat > 0 ? new List<Vector4>(maxToInject * sh3PerSplat) : null;
        var injectedResearch = package.ResearchMetadata.Length > 0 ? new List<Vector4>(maxToInject) : null;
        var injectedArtifact = package.ArtifactRepairMetadata.Length > 0 ? new List<Vector4>(maxToInject) : null;
        var injectedSupport = package.SupportMetadata.Length > 0 ? new List<SplatPackSupportMetadata>(maxToInject) : null;

        long neighborCountAccumulator = 0;
        int injectedCount = 0;
        for (int rank = 0; rank < candidates.Count && injectedCount < desiredCount; rank++)
        {
            int voxelIdx = candidates[rank].VoxelIndex;
            int vz = voxelIdx / (grid * grid);
            int vy = (voxelIdx / grid) % grid;
            int vx = voxelIdx % grid;
            Vector3 voxelCenter = new(
                boundsMin.X + (vx + 0.5f) * voxelSize.X,
                boundsMin.Y + (vy + 0.5f) * voxelSize.Y,
                boundsMin.Z + (vz + 0.5f) * voxelSize.Z);

            // Collect candidate neighbor splats from voxels within radiusVoxels.
            var neighborIndices = new List<int>(32);
            for (int dz = -radiusVoxels; dz <= radiusVoxels; dz++)
            {
                int z = vz + dz;
                if (z < 0 || z >= grid)
                {
                    continue;
                }
                for (int dy = -radiusVoxels; dy <= radiusVoxels; dy++)
                {
                    int y = vy + dy;
                    if (y < 0 || y >= grid)
                    {
                        continue;
                    }
                    for (int dx = -radiusVoxels; dx <= radiusVoxels; dx++)
                    {
                        int x = vx + dx;
                        if (x < 0 || x >= grid)
                        {
                            continue;
                        }
                        float distSq = dx * dx + dy * dy + dz * dz;
                        if (distSq > radiusVoxels * radiusVoxels)
                        {
                            continue;
                        }
                        int key = (z * grid + y) * grid + x;
                        if (voxelToSplats.TryGetValue(key, out List<int>? bucket))
                        {
                            neighborIndices.AddRange(bucket);
                        }
                    }
                }
            }

            if (neighborIndices.Count == 0)
            {
                continue;
            }

            // Rank by world-space distance and take the closest K=8.
            const int kNearest = 8;
            neighborIndices.Sort((a, b) =>
            {
                float da = Vector3.DistanceSquared(positions[a], voxelCenter);
                float db = Vector3.DistanceSquared(positions[b], voxelCenter);
                return da.CompareTo(db);
            });
            int useCount = Math.Min(kNearest, neighborIndices.Count);
            neighborCountAccumulator += useCount;

            // Inverse-distance weighted average of color, axes, SH3, plus
            // dominant render-class flag bits among neighbors.
            float weightSum = 0f;
            Vector4 colorAccum = Vector4.Zero;
            Vector4 axis0Accum = Vector4.Zero;
            Vector4 axis1Accum = Vector4.Zero;
            Vector4 axis2Accum = Vector4.Zero;
            float axis0LenAccum = 0f;
            float axis1LenAccum = 0f;
            float axis2LenAccum = 0f;
            Vector4 sh1RAccum = Vector4.Zero;
            Vector4 sh1GAccum = Vector4.Zero;
            Vector4 sh1BAccum = Vector4.Zero;
            Vector4[]? sh3Accum = sh3PerSplat > 0 ? new Vector4[sh3PerSplat] : null;
            var flagCounts = new Dictionary<int, float>(useCount);

            for (int k = 0; k < useCount; k++)
            {
                int neighborIdx = neighborIndices[k];
                SplatPackSplat neighbor = package.Splats[neighborIdx];
                float dist = MathF.Sqrt(Vector3.DistanceSquared(positions[neighborIdx], voxelCenter));
                float w = 1f / MathF.Max(1e-4f, dist);
                weightSum += w;

                colorAccum += neighbor.Color * w;
                axis0Accum += neighbor.Axis0WS * w;
                axis1Accum += neighbor.Axis1WS * w;
                axis2Accum += neighbor.Axis2WS * w;
                axis0LenAccum += new Vector3(neighbor.Axis0WS.X, neighbor.Axis0WS.Y, neighbor.Axis0WS.Z).Length() * w;
                axis1LenAccum += new Vector3(neighbor.Axis1WS.X, neighbor.Axis1WS.Y, neighbor.Axis1WS.Z).Length() * w;
                axis2LenAccum += new Vector3(neighbor.Axis2WS.X, neighbor.Axis2WS.Y, neighbor.Axis2WS.Z).Length() * w;
                sh1RAccum += neighbor.Sh1R * w;
                sh1GAccum += neighbor.Sh1G * w;
                sh1BAccum += neighbor.Sh1B * w;

                if (sh3Accum != null && sh3PerSplat > 0)
                {
                    int srcOffset = neighborIdx * sh3PerSplat;
                    for (int s = 0; s < sh3PerSplat; s++)
                    {
                        sh3Accum[s] += package.Sh3Coefficients[srcOffset + s] * w;
                    }
                }

                int flags = Math.Clamp((int)MathF.Round(neighbor.Meta.Y), 0, 255);
                flagCounts[flags] = flagCounts.GetValueOrDefault(flags) + w;
            }

            if (weightSum <= 0f)
            {
                continue;
            }

            float invWeight = 1f / weightSum;
            Vector4 color = colorAccum * invWeight;
            // Apply alpha scale; injected splats are synthetic so they receive a
            // dampened alpha so they fill holes without overwhelming nearby
            // trained splats.
            color.W = Math.Clamp(color.W * alphaScale, 0f, 0.999f);

            // Normalize accumulated axes back to a representative magnitude.
            Vector3 axis0Dir = new(axis0Accum.X * invWeight, axis0Accum.Y * invWeight, axis0Accum.Z * invWeight);
            Vector3 axis1Dir = new(axis1Accum.X * invWeight, axis1Accum.Y * invWeight, axis1Accum.Z * invWeight);
            Vector3 axis2Dir = new(axis2Accum.X * invWeight, axis2Accum.Y * invWeight, axis2Accum.Z * invWeight);
            float axis0Target = (axis0LenAccum * invWeight) * scaleFactor;
            float axis1Target = (axis1LenAccum * invWeight) * scaleFactor;
            float axis2Target = (axis2LenAccum * invWeight) * scaleFactor;
            Vector3 axis0 = RescaleAxis(axis0Dir, axis0Target);
            Vector3 axis1 = RescaleAxis(axis1Dir, axis1Target);
            Vector3 axis2 = RescaleAxis(axis2Dir, axis2Target);

            // Surface-aware placement: replace voxel-centre position and
            // reorient axes so the injected splat lies along the locally-fit
            // surface plane. Done after the IDW pass so neighbour-aggregated
            // axis magnitudes still control the splat scale.
            Vector3 injectedPosition = voxelCenter;
            if (options.EnableSurfaceAwareInjection)
            {
                var neighborPositions = new Vector3[useCount];
                var neighborWeights = new float[useCount];
                for (int k = 0; k < useCount; k++)
                {
                    int neighborIdx = neighborIndices[k];
                    neighborPositions[k] = positions[neighborIdx];
                    neighborWeights[k] = alphas[neighborIdx];
                }
                var (planeCentroid, planeNormal) = EstimateSurfacePlane(neighborPositions, neighborWeights, useCount);
                Vector3 toVoxel = voxelCenter - planeCentroid;
                float signedDist = Vector3.Dot(toVoxel, planeNormal);
                injectedPosition = voxelCenter - signedDist * planeNormal;
                // Reorient axes: thin axis along normal, the other two in-plane.
                // Use the smallest of the IDW-aggregated axis magnitudes as the
                // normal-direction scale (further dampened by the user-set
                // thickness scale) and the larger two as in-plane spread.
                var (uDir, vDir) = OrthonormalInPlaneBasis(planeNormal);
                float aMin = MathF.Min(axis0Target, MathF.Min(axis1Target, axis2Target));
                float aMid = axis0Target + axis1Target + axis2Target - aMin - MathF.Max(axis0Target, MathF.Max(axis1Target, axis2Target));
                float aMax = MathF.Max(axis0Target, MathF.Max(axis1Target, axis2Target));
                float normalScale = MathF.Max(MinimumScale, aMin * options.SurfaceAwareThicknessScale);
                axis0 = uDir * aMax;
                axis1 = vDir * aMid;
                axis2 = planeNormal * normalScale;
            }

            // Choose the most common neighbor flag byte (highest weighted
            // count) so the injected splat inherits a coherent render class.
            int chosenFlags = 0;
            float bestFlagWeight = -1f;
            foreach (KeyValuePair<int, float> kv in flagCounts)
            {
                if (kv.Value > bestFlagWeight)
                {
                    bestFlagWeight = kv.Value;
                    chosenFlags = kv.Key;
                }
            }

            // Force render class bits to RenderHintSurface (0) so synthetic
            // splats are never accidentally re-classified as floaters or
            // free-space by downstream passes that read Meta.Y.
            int flagsOut = chosenFlags & ~(0x7 << 1);
            float metaImportance = 0.3f;
            float metaCoverageBoost = 1f;
            Vector4 meta = new(
                0f,                  // source index; -1 conceptually, but stored as 0 since synthetic splats have no PLY row.
                flagsOut,
                metaImportance,
                metaCoverageBoost);

            Vector4 sh1R = sh1RAccum * invWeight;
            Vector4 sh1G = sh1GAccum * invWeight;
            Vector4 sh1B = sh1BAccum * invWeight;

            var newSplat = new SplatPackSplat(
                new Vector4(injectedPosition.X, injectedPosition.Y, injectedPosition.Z, 1f),
                new Vector4(axis0.X, axis0.Y, axis0.Z, 0f),
                new Vector4(axis1.X, axis1.Y, axis1.Z, 0f),
                new Vector4(axis2.X, axis2.Y, axis2.Z, 0f),
                color,
                meta,
                sh1R,
                sh1G,
                sh1B);
            injectedSplats.Add(newSplat);

            if (injectedSh3 != null && sh3Accum != null)
            {
                for (int s = 0; s < sh3PerSplat; s++)
                {
                    injectedSh3.Add(sh3Accum[s] * invWeight);
                }
            }

            if (injectedResearch != null)
            {
                int renderClass = (flagsOut >> 1) & 0x7;
                int coverageHint = (flagsOut >> 4) & 0xF;
                float injectedMaxAxis = MathF.Max(axis0Target, MathF.Max(axis1Target, axis2Target));
                float minAxis = MathF.Min(axis0Target, MathF.Min(axis1Target, axis2Target));
                float injectedAxisRatio = minAxis > MinimumScale ? injectedMaxAxis / minAxis : 1f;
                injectedResearch.Add(new Vector4(
                    renderClass,
                    coverageHint / 15f,
                    injectedMaxAxis,
                    MathF.Max(1f, injectedAxisRatio)));
            }

            if (injectedArtifact != null)
            {
                // Synthetic splats carry no artifact lineage; coverage boost
                // defaults to 1.0 (neutral) and class/layer/action are zero.
                injectedArtifact.Add(new Vector4(0f, 0f, 1f, 0f));
            }

            if (injectedSupport != null)
            {
                // Pack a medium support confidence (0.5) so the injected splats
                // are visible to support-protect downstream gates but not so
                // strong as to dominate trained splats.
                Vector3 supportDirection = voxelCenter - (package.Bounds.Min + package.Bounds.Max) * 0.5f;
                if (supportDirection.LengthSquared() <= MinimumScale)
                {
                    supportDirection = Vector3.UnitZ;
                }
                else
                {
                    supportDirection = Vector3.Normalize(supportDirection);
                }
                EncodeOctahedral8(supportDirection, out int ex, out int ey);
                int renderClassBits = (flagsOut >> 1) & 0x7;
                uint packed0 =
                    (uint)(ex & 0xFF)
                    | ((uint)(ey & 0xFF) << 8)
                    | ((uint)QuantizeByte(0.25f) << 16)   // moderate support cone
                    | (0u << 24);                          // etaBucket = 0
                uint packed1 =
                    (uint)QuantizeByte(0.5f)               // supportConfidence
                    | ((uint)QuantizeByte(0f) << 8)        // freeFloaterConfidence
                    | ((uint)QuantizeByte(0f) << 16)       // layerRisk
                    | ((uint)(renderClassBits & 0x7) << 24);
                injectedSupport.Add(new SplatPackSupportMetadata(packed0, packed1));
            }

            injectedCount++;
        }

        if (injectedCount == 0)
        {
            return new SplatInjectionSummary(grid, 0, underServedCount, deadCount, 0f, deficitThreshold);
        }

        // Step 6: concatenate originals + injected splats and rebuild metadata
        // arrays in lockstep. Per the design, all injected splats land in a
        // new trailing chunk so the existing streaming layout is untouched.
        int newCount = originalCount + injectedCount;
        SplatPackSplat[] newSplats = new SplatPackSplat[newCount];
        Array.Copy(package.Splats, newSplats, originalCount);
        for (int i = 0; i < injectedCount; i++)
        {
            newSplats[originalCount + i] = injectedSplats[i];
        }

        Vector4[] newSh3;
        if (sh3PerSplat > 0 && injectedSh3 != null)
        {
            newSh3 = new Vector4[newCount * sh3PerSplat];
            Array.Copy(package.Sh3Coefficients, newSh3, originalCount * sh3PerSplat);
            for (int i = 0; i < injectedSh3.Count; i++)
            {
                newSh3[originalCount * sh3PerSplat + i] = injectedSh3[i];
            }
        }
        else
        {
            newSh3 = package.Sh3Coefficients;
        }

        Vector4[] newResearch;
        if (injectedResearch != null)
        {
            newResearch = new Vector4[newCount];
            Array.Copy(package.ResearchMetadata, newResearch, originalCount);
            for (int i = 0; i < injectedCount; i++)
            {
                newResearch[originalCount + i] = injectedResearch[i];
            }
        }
        else
        {
            newResearch = package.ResearchMetadata;
        }

        Vector4[] newArtifact;
        if (injectedArtifact != null)
        {
            newArtifact = new Vector4[newCount];
            Array.Copy(package.ArtifactRepairMetadata, newArtifact, originalCount);
            for (int i = 0; i < injectedCount; i++)
            {
                newArtifact[originalCount + i] = injectedArtifact[i];
            }
        }
        else
        {
            newArtifact = package.ArtifactRepairMetadata;
        }

        SplatPackSupportMetadata[] newSupport;
        if (injectedSupport != null)
        {
            newSupport = new SplatPackSupportMetadata[newCount];
            Array.Copy(package.SupportMetadata, newSupport, originalCount);
            for (int i = 0; i < injectedCount; i++)
            {
                newSupport[originalCount + i] = injectedSupport[i];
            }
        }
        else
        {
            newSupport = package.SupportMetadata;
        }

        var newChunks = new List<SplatPackChunk>(package.Chunks.Length + 1);
        newChunks.AddRange(package.Chunks);
        Vector3 chunkMin = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 chunkMax = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        for (int i = 0; i < injectedCount; i++)
        {
            Vector4 c = injectedSplats[i].CenterWS;
            Vector3 p = new(c.X, c.Y, c.Z);
            chunkMin = Vector3.Min(chunkMin, p);
            chunkMax = Vector3.Max(chunkMax, p);
        }
        newChunks.Add(new SplatPackChunk(chunkMin, chunkMax, originalCount, injectedCount, 0));

        package = new SplatPackPackage
        {
            Bounds = package.Bounds,
            Chunks = newChunks.ToArray(),
            Splats = newSplats,
            Sh3Coefficients = newSh3,
            ResearchMetadata = newResearch,
            ArtifactRepairMetadata = newArtifact,
            CellArtifactMetadata = package.CellArtifactMetadata,
            CellArtifactMetadataStride = package.CellArtifactMetadataStride,
            SupportMetadata = newSupport,
            SphericalSupportField = package.SphericalSupportField,
            SphericalSupportFieldStride = package.SphericalSupportFieldStride,
        };

        float meanNeighbors = injectedCount > 0
            ? (float)((double)neighborCountAccumulator / injectedCount)
            : 0f;
        return new SplatInjectionSummary(grid, injectedCount, underServedCount, deadCount, meanNeighbors, deficitThreshold);
    }

    private static Vector3 RescaleAxis(Vector3 direction, float targetLength)
    {
        float len = direction.Length();
        if (len <= MinimumScale || targetLength <= MinimumScale)
        {
            // Avoid emitting a degenerate axis; produce a small axis aligned with
            // the closest cardinal direction to keep the splat well-formed.
            return new Vector3(MathF.Max(MinimumScale, targetLength), 0f, 0f);
        }
        float scale = targetLength / len;
        return direction * scale;
    }
}
