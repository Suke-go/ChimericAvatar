using System.Numerics;

namespace SplatPack.Compiler;

public static partial class SplatPackBuilder
{
    private const float SphericalProbeEpsilon = 1e-6f;
    private const float SphericalProbeMinDistanceMeters = 0.05f;

    private sealed class SphericalProbeCell
    {
        public double Tau;
        public double SurfaceMass;
        public double MicroMass;
        public double FreeMass;
        public double LowBand;
        public double MidBand;
        public double HighBand;
        public float Alpha;
    }

    private sealed class SphericalProbeFrame
    {
        public required SphericalProbeCell[] Cells { get; init; }
        public required int LongitudeCells { get; init; }
        public required int LatitudeCells { get; init; }
        public required float CellSolidAngle { get; init; }
        public required int NonEmptyCells { get; init; }
        public required float SolidAngleMass { get; init; }
        public required float EtaMean { get; init; }
        public required float EtaUnderRate { get; init; }
        public required float EtaOverRate { get; init; }
    }

    private readonly record struct AngularSupportCandidate(
        Candidate Candidate,
        int CellIndex,
        float SupportScore,
        float RemovalScore);

    private sealed class ShellSurfaceCellResidual
    {
        public double SurfaceDeficit;
        public double MicroDeficit;
        public double HighBandDeficit;
        public double LowBandExcess;
        public double HoleDeficit;
    }

    private readonly record struct ShellSurfaceResidualStats(
        float Residual,
        float SurfaceResidual,
        float MicroResidual,
        float HighBandResidual,
        float LowBandExcess,
        float VoxelHoleResidual,
        int WeakShellCells);

    private readonly record struct ShellSurfaceRepairCandidate(
        Candidate Candidate,
        int CellIndex,
        float Score,
        int ArtifactClass,
        float SurfaceGain,
        float MicroGain,
        float HighBandGain,
        float HoleGain);

    private const int SphericalSupportFieldVector4PerRecord = SplatPackWriter.SphericalSupportFieldV1Stride / SplatPackWriter.SphericalSupportFieldStride;

    private static SplatPackSphericalInformationStats BuildSphericalInformationStats(
        GaussianSplatStream stream,
        Candidate[] sourceCandidates,
        SplatPackPackage package,
        SplatPackBuildOptions options,
        SplatPackRotationOrder rotationOrder,
        float axisLengthCap)
    {
        if (sourceCandidates.Length == 0 || package.Splats.Length == 0)
        {
            return SplatPackSphericalInformationStats.Empty;
        }

        int longitudeCells = ResolveSphericalSupportLongitudeCells(options);
        int latitudeCells = ResolveSphericalSupportLatitudeCells(longitudeCells);
        SplatPackBounds sourceBounds = ComputeBounds(stream, sourceCandidates);
        Vector3 center = (sourceBounds.Min + sourceBounds.Max) * 0.5f;
        SphericalProbeFrame source = RenderSphericalSupportSourceProbe(
            stream,
            sourceCandidates,
            rotationOrder,
            longitudeCells,
            latitudeCells,
            center,
            axisLengthCap,
            options);
        SphericalProbeFrame emitted = RenderSphericalPackageProbe(package, longitudeCells, latitudeCells, center);

        float coverageResidual = SphericalResidual(source, emitted, static cell => cell.Alpha, source.SolidAngleMass);
        float surfaceResidual = SphericalResidual(source, emitted, static cell => (float)cell.SurfaceMass, SphericalMass(source, static cell => cell.SurfaceMass));
        float microResidual = SphericalResidual(source, emitted, static cell => (float)cell.MicroMass, SphericalMass(source, static cell => cell.MicroMass));
        float freeResidual = SphericalResidual(source, emitted, static cell => (float)cell.FreeMass, SphericalMass(source, static cell => cell.FreeMass));
        float lowBandResidual = SphericalResidual(source, emitted, static cell => (float)cell.LowBand, SphericalMass(source, static cell => cell.LowBand));
        float midBandResidual = SphericalResidual(source, emitted, static cell => (float)cell.MidBand, SphericalMass(source, static cell => cell.MidBand));
        float highBandResidual = SphericalResidual(source, emitted, static cell => (float)cell.HighBand, SphericalMass(source, static cell => cell.HighBand));
        float emittedFreeMass = SphericalRawMass(emitted, static cell => cell.FreeMass);
        float emittedSupportMass = SphericalRawMass(emitted, static cell => cell.SurfaceMass + cell.MicroMass);
        float emittedFreeRatio = Math.Clamp(emittedFreeMass / MathF.Max(1e-6f, emittedFreeMass + emittedSupportMass), 0f, 1f);
        float shellMismatch = Math.Clamp(
            0.25f
            + coverageResidual * 0.5f
            + surfaceResidual * 0.12f
            + microResidual * 0.16f
            + highBandResidual * 0.52f,
            0f,
            1f);
        float particleRisk = Math.Clamp(
            emittedFreeRatio * 0.55f
            + emitted.EtaUnderRate * shellMismatch * 0.45f
            + highBandResidual * 0.22f
            + coverageResidual * 0.08f,
            0f,
            1f);
        float blurRisk = Math.Clamp(emitted.EtaOverRate * 0.65f + highBandResidual * 0.2f + microResidual * 0.15f, 0f, 1f);

        return new SplatPackSphericalInformationStats
        {
            Mode = "equal-area-spherical-probe-v1",
            LongitudeCells = longitudeCells,
            LatitudeCells = latitudeCells,
            CellCount = longitudeCells * latitudeCells,
            SourceNonEmptyCells = source.NonEmptyCells,
            EmittedNonEmptyCells = emitted.NonEmptyCells,
            SourceSolidAngleMass = source.SolidAngleMass,
            EmittedSolidAngleMass = emitted.SolidAngleMass,
            CoverageResidual = coverageResidual,
            SurfaceResidual = surfaceResidual,
            MicroDetailResidual = microResidual,
            FreeSpaceResidual = freeResidual,
            LowBandResidual = lowBandResidual,
            MidBandResidual = midBandResidual,
            HighBandResidual = highBandResidual,
            AngularFootprintMean = emitted.EtaMean,
            AngularFootprintUnderRate = emitted.EtaUnderRate,
            AngularFootprintOverRate = emitted.EtaOverRate,
            SupportFieldRecords = ResolveSphericalSupportFieldRecordCount(package),
            SupportFieldSupportedCells = ResolveSphericalSupportFieldSupportedCellCount(package),
            AngularDeficit = source.NonEmptyCells > 0
                ? Math.Clamp(1f - emitted.NonEmptyCells / (float)source.NonEmptyCells, 0f, 1f)
                : 0f,
            UnsupportedAngularMass = freeResidual,
            ParticleRisk = particleRisk,
            BlurRisk = blurRisk,
        };
    }

    private static Vector4[] BuildSphericalSupportField(
        GaussianSplatStream stream,
        Candidate[] sourceCandidates,
        SplatPackPackage package,
        SplatPackBuildOptions options,
        SplatPackRotationOrder rotationOrder,
        float axisLengthCap)
    {
        if (!ShouldBuildSphericalSupportField(options) || sourceCandidates.Length == 0 || package.Splats.Length == 0)
        {
            return Array.Empty<Vector4>();
        }

        int longitudeCells = ResolveSphericalSupportLongitudeCells(options);
        int latitudeCells = ResolveSphericalSupportLatitudeCells(longitudeCells);
        SplatPackBounds sourceBounds = ComputeBounds(stream, sourceCandidates);
        Vector3 center = (sourceBounds.Min + sourceBounds.Max) * 0.5f;
        SphericalProbeFrame source = RenderSphericalSupportSourceProbe(
            stream,
            sourceCandidates,
            rotationOrder,
            longitudeCells,
            latitudeCells,
            center,
            axisLengthCap,
            options);
        SphericalProbeFrame emitted = RenderSphericalPackageProbe(package, longitudeCells, latitudeCells, center);

        var records = new List<Vector4>(source.Cells.Length * SphericalSupportFieldVector4PerRecord);
        for (int i = 0; i < source.Cells.Length; i++)
        {
            SphericalProbeCell sourceCell = source.Cells[i];
            float supportMass = (float)(sourceCell.SurfaceMass + sourceCell.MicroMass * 1.15);
            float freeMass = (float)sourceCell.FreeMass;
            float totalMass = supportMass + freeMass;
            float freeRatio = totalMass > 1e-6f ? Math.Clamp(freeMass / totalMass, 0f, 1f) : 1f;
            bool supported = sourceCell.Alpha > 0.01f && supportMass > 1e-4f && freeRatio <= 0.42f;
            if (!supported)
            {
                Vector3 unsupportedDirection = SphericalCellDirection(i, longitudeCells, latitudeCells);
                records.Add(new Vector4(unsupportedDirection.X, unsupportedDirection.Y, unsupportedDirection.Z, 0f));
                records.Add(new Vector4(0f, 0f, Math.Clamp(freeMass, 0f, 65535f), 1f));
                continue;
            }

            SphericalProbeCell emittedCell = emitted.Cells[i];
            float emittedSupportMass = (float)(emittedCell.SurfaceMass + emittedCell.MicroMass * 1.15);
            float supportRatio = Math.Clamp(emittedSupportMass / Math.Max(1e-4f, supportMass), 0f, 1.5f);
            float confidence = Math.Clamp(0.46f + supportRatio * 0.36f + sourceCell.Alpha * 0.16f - freeRatio * 0.42f, 0f, 1f);
            Vector3 direction = SphericalCellDirection(i, longitudeCells, latitudeCells);
            float layerRisk = Math.Clamp(freeRatio + Math.Max(0f, 1f - supportRatio) * 0.28f, 0f, 1f);

            records.Add(new Vector4(direction.X, direction.Y, direction.Z, confidence));
            records.Add(new Vector4(
                Math.Clamp((float)sourceCell.SurfaceMass, 0f, 65535f),
                Math.Clamp((float)sourceCell.MicroMass, 0f, 65535f),
                Math.Clamp(freeMass, 0f, 65535f),
                layerRisk));
        }

        return records.ToArray();
    }

    private static bool ShouldBuildSphericalSupportField(SplatPackBuildOptions options)
    {
        return SplatPackWriter.FormatForOptions(options) == SplatPackWriter.WriteFormat.ResearchSectioned;
    }

    private static int ResolveSphericalSupportLongitudeCells(SplatPackBuildOptions options)
    {
        int baseCells = options.ProbeResolution / Math.Max(1, options.ProbeCellSize);
        return Math.Clamp(baseCells * 2, 64, 128);
    }

    private static int ResolveSphericalSupportLatitudeCells(int longitudeCells)
    {
        return Math.Clamp(longitudeCells / 2, 32, 64);
    }

    private static List<Candidate> ApplyShellSurfaceRepair(
        GaussianSplatStream stream,
        Candidate[] supportSourceCandidates,
        List<Candidate> initialSelected,
        SplatPackBounds bounds,
        float axisLengthCap,
        SplatPackRotationOrder rotationOrder,
        SplatPackBuildOptions options,
        int maxSplats,
        out ShellSurfaceRepairSummary summary)
    {
        summary = ShellSurfaceRepairSummary.Empty;
        if (!IsShellSurfaceRepairEnabled(options)
            || supportSourceCandidates.Length == 0
            || initialSelected.Count == 0
            || initialSelected.Count >= maxSplats)
        {
            return initialSelected;
        }

        int longitudeCells = ResolveSphericalSupportLongitudeCells(options);
        int latitudeCells = ResolveSphericalSupportLatitudeCells(longitudeCells);
        Vector3 center = (bounds.Min + bounds.Max) * 0.5f;
        SphericalProbeFrame source = RenderSphericalSupportSourceProbe(
            stream,
            supportSourceCandidates,
            rotationOrder,
            longitudeCells,
            latitudeCells,
            center,
            axisLengthCap,
            options);
        SphericalProbeFrame selectedFrame = RenderSphericalSupportSourceProbe(
            stream,
            initialSelected.ToArray(),
            rotationOrder,
            longitudeCells,
            latitudeCells,
            center,
            axisLengthCap,
            options);
        ShellSurfaceCellResidual[] residuals = BuildShellSurfaceCellResiduals(source, selectedFrame, options, out ShellSurfaceResidualStats before);
        if (before.Residual <= SphericalProbeEpsilon)
        {
            summary = new ShellSurfaceRepairSummary
            {
                Enabled = true,
                Mode = SplatPackArtifactRepairMode.ShellSurfaceV1.ToString(),
                InitialSelectedSplats = initialSelected.Count,
                FinalSelectedSplats = initialSelected.Count,
                ReinsertedSplats = 0,
                ResidualBefore = before.Residual,
                ResidualAfter = before.Residual,
                ResidualReductionRatio = 0f,
                SurfaceResidualBefore = before.SurfaceResidual,
                SurfaceResidualAfter = before.SurfaceResidual,
                MicroResidualBefore = before.MicroResidual,
                MicroResidualAfter = before.MicroResidual,
                HighBandResidualBefore = before.HighBandResidual,
                HighBandResidualAfter = before.HighBandResidual,
                LowBandExcessBefore = before.LowBandExcess,
                LowBandExcessAfter = before.LowBandExcess,
                VoxelHoleResidualBefore = before.VoxelHoleResidual,
                VoxelHoleResidualAfter = before.VoxelHoleResidual,
                WeakShellCellsBefore = before.WeakShellCells,
                WeakShellCellsAfter = before.WeakShellCells,
            };
            return initialSelected;
        }

        var selected = new List<Candidate>(initialSelected);
        var selectedSources = new HashSet<int>(selected.Select(static candidate => candidate.SourceIndex));
        var repairCandidates = new List<ShellSurfaceRepairCandidate>();
        foreach (Candidate candidate in supportSourceCandidates)
        {
            if (selectedSources.Contains(candidate.SourceIndex))
            {
                continue;
            }

            if (TryBuildShellSurfaceRepairCandidate(
                    stream,
                    candidate,
                    center,
                    longitudeCells,
                    latitudeCells,
                    residuals,
                    axisLengthCap,
                    rotationOrder,
                    options,
                    out ShellSurfaceRepairCandidate repairCandidate))
            {
                repairCandidates.Add(repairCandidate);
            }
        }

        repairCandidates.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.Candidate.SourceIndex.CompareTo(b.Candidate.SourceIndex);
        });

        int reinserted = 0;
        float repairScoreFloor = ShellSurfaceScoreFloor(repairCandidates, maxSplats - selected.Count, options);
        foreach (ShellSurfaceRepairCandidate candidate in repairCandidates)
        {
            if (selected.Count >= maxSplats)
            {
                break;
            }

            if (candidate.Score < repairScoreFloor)
            {
                break;
            }

            if (!selectedSources.Add(candidate.Candidate.SourceIndex))
            {
                continue;
            }

            if (!TryBuildShellSurfaceRepairCandidate(
                    stream,
                    candidate.Candidate,
                    center,
                    longitudeCells,
                    latitudeCells,
                    residuals,
                    axisLengthCap,
                    rotationOrder,
                    options,
                    out ShellSurfaceRepairCandidate refreshed)
                || refreshed.Score <= SphericalProbeEpsilon)
            {
                selectedSources.Remove(candidate.Candidate.SourceIndex);
                continue;
            }

            if (refreshed.Score < repairScoreFloor)
            {
                selectedSources.Remove(candidate.Candidate.SourceIndex);
                continue;
            }

            selected.Add(refreshed.Candidate with
            {
                CoverageBoost = 1f,
                ArtifactClass = refreshed.ArtifactClass,
                LayerId = LayerNone,
                RepairAction = RepairActionReinsert,
            });
            ApplyShellSurfaceRepairEstimate(residuals[refreshed.CellIndex], refreshed);
            reinserted++;
        }

        if (selected.Count < maxSplats)
        {
            SphericalProbeFrame intermediateFrame = RenderSphericalSupportSourceProbe(
                stream,
                selected.ToArray(),
                rotationOrder,
                longitudeCells,
                latitudeCells,
                center,
                axisLengthCap,
                options);
            residuals = BuildShellSurfaceCellResiduals(source, intermediateFrame, options, out _);
            reinserted += FillRemainingShellSurfaceBudget(
                stream,
                supportSourceCandidates,
                selected,
                selectedSources,
                center,
                longitudeCells,
                latitudeCells,
                residuals,
                axisLengthCap,
                rotationOrder,
                options,
                maxSplats);
        }

        SphericalProbeFrame repairedFrame = RenderSphericalSupportSourceProbe(
            stream,
            selected.ToArray(),
            rotationOrder,
            longitudeCells,
            latitudeCells,
            center,
            axisLengthCap,
            options);
        BuildShellSurfaceCellResiduals(source, repairedFrame, options, out ShellSurfaceResidualStats after);
        summary = new ShellSurfaceRepairSummary
        {
            Enabled = true,
            Mode = SplatPackArtifactRepairMode.ShellSurfaceV1.ToString(),
            InitialSelectedSplats = initialSelected.Count,
            FinalSelectedSplats = selected.Count,
            ReinsertedSplats = reinserted,
            ResidualBefore = before.Residual,
            ResidualAfter = after.Residual,
            ResidualReductionRatio = before.Residual > SphericalProbeEpsilon
                ? Math.Clamp((before.Residual - after.Residual) / before.Residual, 0f, 1f)
                : 0f,
            SurfaceResidualBefore = before.SurfaceResidual,
            SurfaceResidualAfter = after.SurfaceResidual,
            MicroResidualBefore = before.MicroResidual,
            MicroResidualAfter = after.MicroResidual,
            HighBandResidualBefore = before.HighBandResidual,
            HighBandResidualAfter = after.HighBandResidual,
            LowBandExcessBefore = before.LowBandExcess,
            LowBandExcessAfter = after.LowBandExcess,
            VoxelHoleResidualBefore = before.VoxelHoleResidual,
            VoxelHoleResidualAfter = after.VoxelHoleResidual,
            WeakShellCellsBefore = before.WeakShellCells,
            WeakShellCellsAfter = after.WeakShellCells,
        };
        return selected;
    }

    private static int FillRemainingShellSurfaceBudget(
        GaussianSplatStream stream,
        Candidate[] supportSourceCandidates,
        List<Candidate> selected,
        HashSet<int> selectedSources,
        Vector3 center,
        int longitudeCells,
        int latitudeCells,
        ShellSurfaceCellResidual[] residuals,
        float axisLengthCap,
        SplatPackRotationOrder rotationOrder,
        SplatPackBuildOptions options,
        int maxSplats)
    {
        if (selected.Count >= maxSplats)
        {
            return 0;
        }

        float axisPower = Math.Clamp(options.ContributionAxisPower, 0f, 2f);
        var fallbackCandidates = new List<ShellSurfaceRepairCandidate>();
        foreach (Candidate candidate in supportSourceCandidates)
        {
            if (selectedSources.Contains(candidate.SourceIndex)
                || !TryBuildShellSurfaceFallbackCandidate(
                    stream,
                    candidate,
                    center,
                    longitudeCells,
                    latitudeCells,
                    residuals,
                    axisLengthCap,
                    axisPower,
                    rotationOrder,
                    options,
                    out ShellSurfaceRepairCandidate fallback))
            {
                continue;
            }

            fallbackCandidates.Add(fallback);
        }

        fallbackCandidates.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.Candidate.SourceIndex.CompareTo(b.Candidate.SourceIndex);
        });

        int inserted = 0;
        float fallbackScoreFloor = ShellSurfaceScoreFloor(fallbackCandidates, maxSplats - selected.Count, options);
        foreach (ShellSurfaceRepairCandidate candidate in fallbackCandidates)
        {
            if (selected.Count >= maxSplats)
            {
                break;
            }

            if (candidate.Score < fallbackScoreFloor)
            {
                break;
            }

            if (!selectedSources.Add(candidate.Candidate.SourceIndex))
            {
                continue;
            }

            if (!TryBuildShellSurfaceFallbackCandidate(
                    stream,
                    candidate.Candidate,
                    center,
                    longitudeCells,
                    latitudeCells,
                    residuals,
                    axisLengthCap,
                    axisPower,
                    rotationOrder,
                    options,
                    out ShellSurfaceRepairCandidate refreshed)
                || refreshed.Score <= SphericalProbeEpsilon)
            {
                selectedSources.Remove(candidate.Candidate.SourceIndex);
                continue;
            }

            if (refreshed.Score < fallbackScoreFloor)
            {
                selectedSources.Remove(candidate.Candidate.SourceIndex);
                continue;
            }

            selected.Add(refreshed.Candidate with
            {
                CoverageBoost = 1f,
                ArtifactClass = refreshed.ArtifactClass,
                LayerId = LayerNone,
                RepairAction = RepairActionReinsert,
            });
            ApplyShellSurfaceRepairEstimate(residuals[refreshed.CellIndex], refreshed);
            inserted++;
        }

        return inserted;
    }

    private static ShellSurfaceCellResidual[] BuildShellSurfaceCellResiduals(
        SphericalProbeFrame source,
        SphericalProbeFrame selected,
        SplatPackBuildOptions options,
        out ShellSurfaceResidualStats stats)
    {
        int count = Math.Min(source.Cells.Length, selected.Cells.Length);
        var residuals = new ShellSurfaceCellResidual[count];
        double surface = 0.0;
        double micro = 0.0;
        double high = 0.0;
        double lowExcess = 0.0;
        double hole = 0.0;
        int weakCells = 0;
        for (int i = 0; i < count; i++)
        {
            SphericalProbeCell sourceCell = source.Cells[i];
            SphericalProbeCell selectedCell = selected.Cells[i];
            double surfaceDeficit = Math.Max(0.0, sourceCell.SurfaceMass - selectedCell.SurfaceMass);
            double microDeficit = Math.Max(0.0, sourceCell.MicroMass - selectedCell.MicroMass);
            double highDeficit = Math.Max(0.0, sourceCell.HighBand - selectedCell.HighBand);
            double lowBandExcess = Math.Max(0.0, selectedCell.LowBand - sourceCell.LowBand);
            double holeDeficit = Math.Max(0.0, sourceCell.Alpha - selectedCell.Alpha);
            residuals[i] = new ShellSurfaceCellResidual
            {
                SurfaceDeficit = surfaceDeficit,
                MicroDeficit = microDeficit,
                HighBandDeficit = highDeficit,
                LowBandExcess = lowBandExcess,
                HoleDeficit = holeDeficit,
            };

            surface += surfaceDeficit * source.CellSolidAngle;
            micro += microDeficit * source.CellSolidAngle;
            high += highDeficit * source.CellSolidAngle;
            lowExcess += lowBandExcess * source.CellSolidAngle;
            hole += holeDeficit * source.CellSolidAngle;

            double supportMass = sourceCell.SurfaceMass + sourceCell.MicroMass + sourceCell.HighBand + sourceCell.Alpha;
            if (supportMass > SphericalProbeEpsilon
                && surfaceDeficit + microDeficit + highDeficit + holeDeficit > SphericalProbeEpsilon)
            {
                weakCells++;
            }
        }

        float surfaceResidual = (float)Math.Clamp(surface / Math.Max(1e-6, SphericalMass(source, static cell => cell.SurfaceMass)), 0.0, 4.0);
        float microResidual = (float)Math.Clamp(micro / Math.Max(1e-6, SphericalMass(source, static cell => cell.MicroMass)), 0.0, 4.0);
        float highResidual = (float)Math.Clamp(high / Math.Max(1e-6, SphericalMass(source, static cell => cell.HighBand)), 0.0, 4.0);
        float lowExcessResidual = (float)Math.Clamp(lowExcess / Math.Max(1e-6, SphericalMass(source, static cell => cell.LowBand)), 0.0, 4.0);
        float holeResidual = (float)Math.Clamp(hole / Math.Max(1e-6, source.SolidAngleMass), 0.0, 4.0);
        float coverageWeight = MathF.Max(0f, options.PullbackWeights.Coverage);
        float textureWeight = MathF.Max(0f, options.PullbackWeights.Texture);
        float layerWeight = MathF.Max(0f, options.PullbackWeights.Layer);
        float weightSum = MathF.Max(
            SphericalProbeEpsilon,
            coverageWeight * 2f + textureWeight * 2f + layerWeight);
        float residual = Math.Clamp(
            (coverageWeight * (surfaceResidual + holeResidual)
             + textureWeight * (microResidual + highResidual)
             + layerWeight * lowExcessResidual) / weightSum,
            0f,
            4f);
        stats = new ShellSurfaceResidualStats(
            residual,
            surfaceResidual,
            microResidual,
            highResidual,
            lowExcessResidual,
            holeResidual,
            weakCells);
        return residuals;
    }

    private static bool TryBuildShellSurfaceRepairCandidate(
        GaussianSplatStream stream,
        Candidate candidate,
        Vector3 center,
        int longitudeCells,
        int latitudeCells,
        ShellSurfaceCellResidual[] residuals,
        float axisLengthCap,
        SplatPackRotationOrder rotationOrder,
        SplatPackBuildOptions options,
        out ShellSurfaceRepairCandidate repairCandidate)
    {
        repairCandidate = default;
        if (IsUnsupportedGiantAxisCandidate(candidate, axisLengthCap, options))
        {
            return false;
        }

        Vector3 position = Position(stream, candidate.SourceIndex);
        Vector3 delta = position - center;
        Vector3 direction = delta.LengthSquared() > SphericalProbeEpsilon
            ? Vector3.Normalize(delta)
            : Vector3.UnitZ;
        int cellIndex = SphericalCellIndex(direction, longitudeCells, latitudeCells);
        if (cellIndex < 0 || cellIndex >= residuals.Length)
        {
            return false;
        }

        float supportScore = AngularSupportScore(stream, candidate, axisLengthCap, rotationOrder, out int renderClass);
        if (supportScore <= SphericalProbeEpsilon
            || renderClass == RenderHintFreeSpace
            || renderClass == RenderHintFloater
            || renderClass == RenderHintLayerRisk)
        {
            return false;
        }

        ShellSurfaceCellResidual residual = residuals[cellIndex];
        if (renderClass == RenderHintBroadSurface
            && residual.LowBandExcess > residual.SurfaceDeficit + residual.HoleDeficit)
        {
            return false;
        }

        float surfaceClass = SurfaceClassWeight(renderClass);
        float microClass = MicroClassWeight(renderClass);
        if (surfaceClass <= 0f && microClass <= 0f)
        {
            return false;
        }

        float saturation = Saturation(ColorForCandidate(stream, candidate.SourceIndex));
        float surfaceGain = (float)Math.Min(residual.SurfaceDeficit, supportScore * surfaceClass);
        float microGain = (float)Math.Min(residual.MicroDeficit, supportScore * (microClass + saturation * microClass));
        float highGain = (float)Math.Min(residual.HighBandDeficit, supportScore * (microClass + saturation * 0.5f));
        float holeGain = (float)Math.Min(residual.HoleDeficit, supportScore * MathF.Max(surfaceClass, microClass));
        float lowPenalty = residual.LowBandExcess > 0.0
            ? (float)Math.Min(residual.LowBandExcess, supportScore * MathF.Max(0f, surfaceClass - saturation * 0.5f))
            : 0f;
        float cellConfidence = ShellSurfaceResidualConfidence(residual);
        float classAlignment = ShellSurfaceClassAlignment(renderClass, residual, saturation);
        float footprintQuality = ShellSurfaceFootprintQuality(
            stream,
            candidate,
            center,
            axisLengthCap,
            rotationOrder,
            4f * MathF.PI / (longitudeCells * latitudeCells));
        float coverageWeight = MathF.Max(0f, options.PullbackWeights.Coverage);
        float textureWeight = MathF.Max(0f, options.PullbackWeights.Texture);
        float layerWeight = MathF.Max(0f, options.PullbackWeights.Layer);
        float score = (coverageWeight * (surfaceGain + holeGain)
                       + textureWeight * (microGain + highGain)
                       - layerWeight * lowPenalty)
                      * cellConfidence
                      * classAlignment
                      * footprintQuality;
        if (score <= SphericalProbeEpsilon)
        {
            return false;
        }

        int artifactClass = microGain + highGain >= surfaceGain + holeGain
            ? ArtifactTexture
            : ArtifactCoverage;
        repairCandidate = new ShellSurfaceRepairCandidate(
            candidate,
            cellIndex,
            score,
            artifactClass,
            surfaceGain,
            microGain,
            highGain,
            holeGain);
        return true;
    }

    private static bool TryBuildShellSurfaceFallbackCandidate(
        GaussianSplatStream stream,
        Candidate candidate,
        Vector3 center,
        int longitudeCells,
        int latitudeCells,
        ShellSurfaceCellResidual[] residuals,
        float axisLengthCap,
        float axisPower,
        SplatPackRotationOrder rotationOrder,
        SplatPackBuildOptions options,
        out ShellSurfaceRepairCandidate repairCandidate)
    {
        repairCandidate = default;
        if (IsUnsupportedGiantAxisCandidate(candidate, axisLengthCap, options))
        {
            return false;
        }

        Vector3 position = Position(stream, candidate.SourceIndex);
        Vector3 delta = position - center;
        Vector3 direction = delta.LengthSquared() > SphericalProbeEpsilon
            ? Vector3.Normalize(delta)
            : Vector3.UnitZ;
        int cellIndex = SphericalCellIndex(direction, longitudeCells, latitudeCells);
        if (cellIndex < 0 || cellIndex >= residuals.Length)
        {
            return false;
        }

        float supportScore = AngularSupportScore(stream, candidate, axisLengthCap, rotationOrder, out int renderClass);
        if (supportScore <= SphericalProbeEpsilon
            || renderClass == RenderHintFreeSpace
            || renderClass == RenderHintFloater
            || renderClass == RenderHintLayerRisk)
        {
            return false;
        }

        ShellSurfaceCellResidual residual = residuals[cellIndex];
        double residualNeed = residual.SurfaceDeficit
                              + residual.MicroDeficit
                              + residual.HighBandDeficit
                              + residual.HoleDeficit;
        if (residualNeed <= SphericalProbeEpsilon
            || (renderClass == RenderHintBroadSurface && residual.LowBandExcess > residualNeed))
        {
            return false;
        }

        float surfaceClass = SurfaceClassWeight(renderClass);
        float microClass = MicroClassWeight(renderClass);
        if (surfaceClass <= 0f && microClass <= 0f)
        {
            return false;
        }

        float saturation = Saturation(ColorForCandidate(stream, candidate.SourceIndex));
        float surfaceGain = (float)Math.Min(residual.SurfaceDeficit, supportScore * surfaceClass);
        float microGain = (float)Math.Min(residual.MicroDeficit, supportScore * (microClass + saturation * microClass));
        float highGain = (float)Math.Min(residual.HighBandDeficit, supportScore * (microClass + saturation * 0.5f));
        float holeGain = (float)Math.Min(residual.HoleDeficit, supportScore * MathF.Max(surfaceClass, microClass));
        float residualConfidence = ShellSurfaceResidualConfidence(residual);
        float classAlignment = ShellSurfaceClassAlignment(renderClass, residual, saturation);
        float footprintQuality = ShellSurfaceFootprintQuality(
            stream,
            candidate,
            center,
            axisLengthCap,
            rotationOrder,
            4f * MathF.PI / (longitudeCells * latitudeCells));
        float priority = BudgetPriorityScore(candidate, axisLengthCap, axisPower, options.PruningMode);
        float classWeight = MathF.Max(surfaceClass, microClass + saturation * microClass);
        float score = residualConfidence
                      * classAlignment
                      * footprintQuality
                      * MathF.Max(SphericalProbeEpsilon, priority)
                      * MathF.Max(SphericalProbeEpsilon, classWeight);
        if (score <= SphericalProbeEpsilon)
        {
            return false;
        }

        int artifactClass = microGain + highGain >= surfaceGain + holeGain
            ? ArtifactTexture
            : ArtifactCoverage;
        repairCandidate = new ShellSurfaceRepairCandidate(
            candidate,
            cellIndex,
            score,
            artifactClass,
            surfaceGain,
            microGain,
            highGain,
            holeGain);
        return true;
    }

    private static void ApplyShellSurfaceRepairEstimate(
        ShellSurfaceCellResidual residual,
        ShellSurfaceRepairCandidate candidate)
    {
        residual.SurfaceDeficit = Math.Max(0.0, residual.SurfaceDeficit - candidate.SurfaceGain);
        residual.MicroDeficit = Math.Max(0.0, residual.MicroDeficit - candidate.MicroGain);
        residual.HighBandDeficit = Math.Max(0.0, residual.HighBandDeficit - candidate.HighBandGain);
        residual.HoleDeficit = Math.Max(0.0, residual.HoleDeficit - candidate.HoleGain);
    }

    private static float ShellSurfaceScoreFloor(
        List<ShellSurfaceRepairCandidate> candidates,
        int budget,
        SplatPackBuildOptions options)
    {
        if (candidates.Count == 0 || budget <= 0)
        {
            return float.PositiveInfinity;
        }

        // Shell-surface repair is a budget reallocation pass, not a confidence
        // gate. The previous average-of-top-budget floor often spent only a
        // small fraction of the reserved repair budget on large indoor scenes,
        // then filled the rest with generic importance-ranked splats. That left
        // visible surface holes even though source splats were available. Keep
        // v1 source-only, but spend the reserved budget on every residual-positive
        // surface/micro candidate before falling back to the ordinary ranking.
        if (options.ArtifactRepairMode == SplatPackArtifactRepairMode.ShellSurfaceV1)
        {
            return SphericalProbeEpsilon;
        }

        int count = Math.Min(candidates.Count, budget);
        double sum = 0.0;
        for (int i = 0; i < count; i++)
        {
            sum += Math.Max(0f, candidates[i].Score);
        }

        return (float)(sum / Math.Max(1, count));
    }

    private static float ShellSurfaceResidualConfidence(ShellSurfaceCellResidual residual)
    {
        double residualNeed = ShellSurfaceResidualNeed(residual);
        return (float)Math.Clamp(
            residualNeed / Math.Max(SphericalProbeEpsilon, residualNeed + residual.LowBandExcess),
            0.0,
            1.0);
    }

    private static double ShellSurfaceResidualNeed(ShellSurfaceCellResidual residual)
    {
        return residual.SurfaceDeficit
               + residual.MicroDeficit
               + residual.HighBandDeficit
               + residual.HoleDeficit;
    }

    private static float ShellSurfaceClassAlignment(
        int renderClass,
        ShellSurfaceCellResidual residual,
        float saturation)
    {
        double surfaceNeed = residual.SurfaceDeficit + residual.HoleDeficit;
        double microNeed = residual.MicroDeficit + residual.HighBandDeficit;
        double lowExcess = residual.LowBandExcess;
        double desired;
        double competing;

        if (renderClass == RenderHintMicroDetail)
        {
            desired = microNeed;
            competing = surfaceNeed * 0.65 + lowExcess * Math.Clamp(1f - saturation, 0f, 1f);
        }
        else if (renderClass == RenderHintForeground)
        {
            desired = Math.Max(surfaceNeed, microNeed) + Math.Min(surfaceNeed, microNeed) * 0.5;
            competing = lowExcess * 0.35;
        }
        else
        {
            desired = surfaceNeed;
            competing = microNeed * (renderClass == RenderHintBroadSurface ? 0.85 : 0.55)
                        + lowExcess * Math.Clamp(1.25f - saturation, 0.35f, 1.25f);
        }

        return (float)Math.Clamp(
            desired / Math.Max(SphericalProbeEpsilon, desired + competing),
            0.0,
            1.0);
    }

    private static float ShellSurfaceFootprintQuality(
        GaussianSplatStream stream,
        Candidate candidate,
        Vector3 center,
        float axisLengthCap,
        SplatPackRotationOrder rotationOrder,
        float cellSolidAngle)
    {
        Vector3 position = Position(stream, candidate.SourceIndex);
        float distance = MathF.Max(SphericalProbeMinDistanceMeters, (position - center).Length());
        GetCandidateAxes(stream, candidate.SourceIndex, rotationOrder, axisLengthCap, 8f, out Vector3 axis0, out Vector3 axis1, out Vector3 axis2);
        AxisPair(axis0, axis1, axis2, out float majorAxis, out float minorAxis);
        float majorAngle = Math.Clamp(majorAxis / distance, 1e-5f, MathF.PI * 0.5f);
        float minorAngle = Math.Clamp(minorAxis / distance, 1e-5f, MathF.PI * 0.5f);
        float eta = MathF.PI * majorAngle * minorAngle / MathF.Max(1e-8f, cellSolidAngle);
        float midFit = 1f - Math.Clamp(MathF.Abs(MathF.Log2(MathF.Max(1e-4f, eta))) / 4f, 0f, 1f);
        float underFit = Math.Clamp(eta / 0.55f, 0f, 1f);
        float overFit = eta > 2.5f ? Math.Clamp(2.5f / eta, 0.35f, 1f) : 1f;
        return Math.Clamp(0.35f + midFit * 0.45f + underFit * 0.2f, 0.25f, 1.0f) * overFit;
    }

    private static List<Candidate> ApplyAngularSupportBudgetRepair(
        GaussianSplatStream stream,
        Candidate[] supportSourceCandidates,
        List<Candidate> selected,
        SplatPackBounds bounds,
        float axisLengthCap,
        SplatPackRotationOrder rotationOrder,
        SplatPackBuildOptions options,
        int maxSplats)
    {
        if (!ShouldBuildSphericalSupportField(options)
            || supportSourceCandidates.Length == 0
            || selected.Count == 0
            || maxSplats <= 0)
        {
            return selected;
        }

        int longitudeCells = ResolveSphericalSupportLongitudeCells(options);
        int latitudeCells = ResolveSphericalSupportLatitudeCells(longitudeCells);
        int cellCount = longitudeCells * latitudeCells;
        Vector3 center = (bounds.Min + bounds.Max) * 0.5f;
        var selectedIndices = new HashSet<int>(selected.Select(static candidate => candidate.SourceIndex));
        var sourceMass = new double[cellCount];
        var selectedMass = new double[cellCount];
        var reinsertionPools = new Dictionary<int, List<AngularSupportCandidate>>();

        foreach (Candidate candidate in supportSourceCandidates)
        {
            if (!TryBuildAngularSupportCandidate(
                    stream,
                    candidate,
                    center,
                    longitudeCells,
                    latitudeCells,
                    axisLengthCap,
                    rotationOrder,
                    options,
                    out AngularSupportCandidate supportCandidate))
            {
                continue;
            }

            sourceMass[supportCandidate.CellIndex] += supportCandidate.SupportScore;
            if (selectedIndices.Contains(candidate.SourceIndex))
            {
                selectedMass[supportCandidate.CellIndex] += supportCandidate.SupportScore;
                continue;
            }

            if (!reinsertionPools.TryGetValue(supportCandidate.CellIndex, out List<AngularSupportCandidate>? pool))
            {
                pool = new List<AngularSupportCandidate>();
                reinsertionPools.Add(supportCandidate.CellIndex, pool);
            }

            pool.Add(supportCandidate);
        }

        int sourceSupportedCells = 0;
        int selectedSupportedCells = 0;
        int underfilledSupportedCells = 0;
        double sourceSupportMass = 0.0;
        double selectedSupportMass = 0.0;
        const double TargetSelectedSupportMassRatio = 0.58;
        for (int i = 0; i < cellCount; i++)
        {
            if (sourceMass[i] > 1e-5)
            {
                sourceSupportedCells++;
                sourceSupportMass += sourceMass[i];
                selectedSupportMass += selectedMass[i];
                if (selectedMass[i] < sourceMass[i] * TargetSelectedSupportMassRatio)
                {
                    underfilledSupportedCells++;
                }
            }

            if (selectedMass[i] > 1e-5)
            {
                selectedSupportedCells++;
            }
        }

        int targetSupportedCells = Math.Min(sourceSupportedCells, Math.Max(0, (int)MathF.Ceiling(sourceSupportedCells * 0.73f)));
        bool hasCellDeficit = selectedSupportedCells < targetSupportedCells;
        bool hasMassDeficit = sourceSupportMass > 1e-5 && selectedSupportMass < sourceSupportMass * TargetSelectedSupportMassRatio;
        if (!hasCellDeficit && !hasMassDeficit)
        {
            return selected;
        }

        var insertions = new List<AngularSupportCandidate>();
        foreach ((int cellIndex, List<AngularSupportCandidate> pool) in reinsertionPools)
        {
            double source = sourceMass[cellIndex];
            if (source <= 1e-5)
            {
                continue;
            }

            double selectedCellMass = selectedMass[cellIndex];
            double required = Math.Max(source * TargetSelectedSupportMassRatio, 1e-5);
            if (selectedCellMass >= required)
            {
                continue;
            }

            pool.Sort(static (a, b) =>
            {
                int byScore = b.SupportScore.CompareTo(a.SupportScore);
                return byScore != 0 ? byScore : a.Candidate.SourceIndex.CompareTo(b.Candidate.SourceIndex);
            });

            double selectedRatio = selectedCellMass / Math.Max(1e-5, source);
            int perCellLimit = selectedRatio <= 1e-5
                ? 6
                : selectedRatio < 0.18
                    ? 5
                    : selectedRatio < 0.38
                        ? 3
                        : 1;
            for (int i = 0; i < Math.Min(perCellLimit, pool.Count); i++)
            {
                insertions.Add(pool[i]);
            }
        }

        if (insertions.Count == 0)
        {
            return selected;
        }

        insertions.Sort((a, b) =>
        {
            double deficitA = sourceMass[a.CellIndex] / Math.Max(1e-5, selectedMass[a.CellIndex] + a.SupportScore);
            double deficitB = sourceMass[b.CellIndex] / Math.Max(1e-5, selectedMass[b.CellIndex] + b.SupportScore);
            int byDeficit = deficitB.CompareTo(deficitA);
            if (byDeficit != 0)
            {
                return byDeficit;
            }

            int byScore = b.SupportScore.CompareTo(a.SupportScore);
            return byScore != 0 ? byScore : a.Candidate.SourceIndex.CompareTo(b.Candidate.SourceIndex);
        });

        int requestedReplacements = Math.Max(
            Math.Max(512, (targetSupportedCells - selectedSupportedCells) * 6),
            underfilledSupportedCells * 3);
        int replacementLimit = Math.Min(
            Math.Max(0, selected.Count),
            Math.Min(requestedReplacements, Math.Max(1, (int)MathF.Ceiling(selected.Count * 0.45f))));
        if (replacementLimit <= 0)
        {
            return selected;
        }

        var removalCandidates = new List<(int Index, Candidate Candidate, float Score, int CellIndex, float SupportScore)>(selected.Count);
        for (int i = 0; i < selected.Count; i++)
        {
            Candidate candidate = selected[i];
            float score = CalculateAngularRemovalScore(
                stream,
                candidate,
                center,
                longitudeCells,
                latitudeCells,
                sourceMass,
                selectedMass,
                axisLengthCap,
                rotationOrder,
                options);
            int cellIndex = -1;
            float supportScore = 0f;
            if (TryBuildAngularSupportCandidate(
                    stream,
                    candidate,
                    center,
                    longitudeCells,
                    latitudeCells,
                    axisLengthCap,
                    rotationOrder,
                    options,
                    out AngularSupportCandidate removalSupport))
            {
                cellIndex = removalSupport.CellIndex;
                supportScore = removalSupport.SupportScore;
            }

            removalCandidates.Add((i, candidate, score, cellIndex, supportScore));
        }

        removalCandidates.Sort(static (a, b) =>
        {
            int byScore = a.Score.CompareTo(b.Score);
            return byScore != 0 ? byScore : a.Candidate.SourceIndex.CompareTo(b.Candidate.SourceIndex);
        });

        Candidate[] repaired = selected.ToArray();
        var usedSources = new HashSet<int>(selectedIndices);
        var removedSlots = new HashSet<int>();
        int removeCursor = 0;
        int inserted = 0;
        foreach (AngularSupportCandidate insertion in insertions)
        {
            if (inserted >= replacementLimit || !usedSources.Add(insertion.Candidate.SourceIndex))
            {
                continue;
            }

            double insertionSourceMass = sourceMass[insertion.CellIndex];
            double insertionRequiredMass = Math.Max(insertionSourceMass * TargetSelectedSupportMassRatio, 1e-5);
            if (insertionSourceMass > 1e-5 && selectedMass[insertion.CellIndex] >= insertionRequiredMass)
            {
                usedSources.Remove(insertion.Candidate.SourceIndex);
                continue;
            }

            bool removedSlotFound = false;
            (int Index, Candidate Candidate, float Score, int CellIndex, float SupportScore) removal = default;
            while (removeCursor < removalCandidates.Count && removedSlots.Contains(removalCandidates[removeCursor].Index))
            {
                removeCursor++;
            }

            while (removeCursor < removalCandidates.Count)
            {
                removal = removalCandidates[removeCursor++];
                if (removedSlots.Contains(removal.Index))
                {
                    continue;
                }

                if (removal.CellIndex >= 0 && sourceMass[removal.CellIndex] > 1e-5 && removal.Score > 0f)
                {
                    double retainedMass = Math.Max(0.0, selectedMass[removal.CellIndex] - removal.SupportScore);
                    if (retainedMass < sourceMass[removal.CellIndex] * 0.42)
                    {
                        continue;
                    }
                }

                removedSlotFound = true;
                break;
            }

            if (!removedSlotFound)
            {
                usedSources.Remove(insertion.Candidate.SourceIndex);
                break;
            }

            removedSlots.Add(removal.Index);
            usedSources.Remove(removal.Candidate.SourceIndex);
            if (removal.CellIndex >= 0)
            {
                selectedMass[removal.CellIndex] = Math.Max(0.0, selectedMass[removal.CellIndex] - removal.SupportScore);
            }

            selectedMass[insertion.CellIndex] += insertion.SupportScore;
            repaired[removal.Index] = insertion.Candidate;
            inserted++;
        }

        return inserted > 0 ? repaired.ToList() : selected;
    }

    private static bool TryBuildAngularSupportCandidate(
        GaussianSplatStream stream,
        Candidate candidate,
        Vector3 center,
        int longitudeCells,
        int latitudeCells,
        float axisLengthCap,
        SplatPackRotationOrder rotationOrder,
        SplatPackBuildOptions options,
        out AngularSupportCandidate supportCandidate)
    {
        supportCandidate = default;
        if (IsUnsupportedGiantAxisCandidate(candidate, axisLengthCap, options))
        {
            return false;
        }

        Vector3 position = Position(stream, candidate.SourceIndex);
        Vector3 delta = position - center;
        Vector3 direction = delta.LengthSquared() > SphericalProbeEpsilon
            ? Vector3.Normalize(delta)
            : Vector3.UnitZ;
        int cellIndex = SphericalCellIndex(direction, longitudeCells, latitudeCells);
        float supportScore = AngularSupportScore(stream, candidate, axisLengthCap, rotationOrder, out int renderClass);
        if (supportScore <= 1e-5f)
        {
            return false;
        }

        float removalScore = supportScore;
        if (renderClass == RenderHintMicroDetail || renderClass == RenderHintForeground)
        {
            removalScore += 0.35f;
        }

        supportCandidate = new AngularSupportCandidate(candidate, cellIndex, supportScore, removalScore);
        return true;
    }

    private static float CalculateAngularRemovalScore(
        GaussianSplatStream stream,
        Candidate candidate,
        Vector3 center,
        int longitudeCells,
        int latitudeCells,
        double[] sourceMass,
        double[] selectedMass,
        float axisLengthCap,
        SplatPackRotationOrder rotationOrder,
        SplatPackBuildOptions options)
    {
        if (!TryBuildAngularSupportCandidate(
                stream,
                candidate,
                center,
                longitudeCells,
                latitudeCells,
                axisLengthCap,
                rotationOrder,
                options,
                out AngularSupportCandidate supportCandidate))
        {
            return -1f;
        }

        float protection = supportCandidate.RemovalScore;
        double source = sourceMass[supportCandidate.CellIndex];
        double selected = selectedMass[supportCandidate.CellIndex];
        if (source > 1e-5)
        {
            float selectedRatio = (float)(selected / source);
            protection += Math.Clamp(0.62f - selectedRatio, 0f, 0.62f) * 1.35f;
        }

        return protection;
    }

    private static float AngularSupportScore(
        GaussianSplatStream stream,
        Candidate candidate,
        float axisLengthCap,
        SplatPackRotationOrder rotationOrder,
        out int renderClass)
    {
        Vector3 rgb = ColorForCandidate(stream, candidate.SourceIndex);
        float opacity = Math.Clamp(Sigmoid(stream.OpacitiesRaw[candidate.SourceIndex]), 0f, 1f);
        renderClass = ClassifyRenderHint(
            rgb,
            opacity,
            candidate.MaxAxisLength,
            candidate.AxisRatio,
            axisLengthCap,
            candidate.Importance,
            candidate.CoverageBoost,
            0,
            candidate.ArtifactClass,
            candidate.RepairAction);
        float classWeight = renderClass switch
        {
            RenderHintMicroDetail => 1.22f,
            RenderHintForeground => 1.18f,
            RenderHintSurface => 0.92f,
            RenderHintBroadSurface => 0.46f,
            RenderHintLayerRisk => 0.18f,
            RenderHintFreeSpace => 0f,
            RenderHintFloater => 0f,
            _ => 0.65f,
        };
        if (classWeight <= 0f)
        {
            return 0f;
        }

        GetCandidateAxes(stream, candidate.SourceIndex, rotationOrder, axisLengthCap, 8f, out Vector3 axis0, out Vector3 axis1, out Vector3 axis2);
        AxisPair(axis0, axis1, axis2, out float majorAxis, out float minorAxis);
        float axisReference = MathF.Max(MinimumScale, axisLengthCap > 0f ? axisLengthCap : majorAxis);
        float rawAxisRatio = candidate.MaxAxisLength / axisReference;
        float giantPenalty = rawAxisRatio > 8f
            ? MathF.Pow(8f / MathF.Max(8f, rawAxisRatio), 1.1f)
            : 1f;
        float eccentricPenalty = Math.Clamp(8f / MathF.Max(8f, candidate.AxisRatio), 0.2f, 1f);
        float saturation = Saturation(rgb);
        float opacitySupport = MathF.Sqrt(Math.Clamp(opacity, 0f, 1f));
        float detailSignal = saturation * (renderClass == RenderHintMicroDetail || renderClass == RenderHintForeground ? 0.32f : 0.12f);
        float areaSignal = MathF.Sqrt(MathF.Max(MinimumScale, majorAxis * MathF.Max(MinimumScale, minorAxis)));
        float axisSignal = Math.Clamp(areaSignal / MathF.Max(MinimumScale, axisReference), 0.06f, 1.4f);
        float score = classWeight
                      * giantPenalty
                      * eccentricPenalty
                      * (opacitySupport * 0.44f + Math.Clamp(candidate.Importance, 0f, 1f) * 0.48f + detailSignal + 0.04f)
                      * axisSignal;
        return MathF.Max(0f, score);
    }

    private static SphericalProbeFrame RenderSphericalSourceProbe(
        GaussianSplatStream stream,
        Candidate[] candidates,
        SplatPackRotationOrder rotationOrder,
        int longitudeCells,
        int latitudeCells,
        Vector3 center)
    {
        SphericalProbeCell[] cells = CreateSphericalCells(longitudeCells * latitudeCells);
        double etaWeighted = 0.0;
        double underWeighted = 0.0;
        double overWeighted = 0.0;
        double totalWeight = 0.0;
        float cellSolidAngle = 4f * MathF.PI / cells.Length;

        foreach (Candidate candidate in candidates)
        {
            int index = candidate.SourceIndex;
            Vector3 position = Position(stream, index);
            GetCandidateAxes(stream, index, rotationOrder, 0f, 0f, out Vector3 axis0, out Vector3 axis1, out Vector3 axis2);
            Vector3 rgb = ColorForCandidate(stream, index);
            float opacity = Math.Clamp(Sigmoid(stream.OpacitiesRaw[index]), 0f, 1f);
            int renderClass = ClassifyRenderHint(
                rgb,
                opacity,
                candidate.MaxAxisLength,
                candidate.AxisRatio,
                0f,
                candidate.Importance,
                candidate.CoverageBoost,
                0,
                candidate.ArtifactClass,
                candidate.RepairAction);
            AddSphericalSplat(
                cells,
                longitudeCells,
                latitudeCells,
                center,
                position,
                axis0,
                axis1,
                axis2,
                rgb,
                opacity,
                renderClass,
                cellSolidAngle,
                ref etaWeighted,
                ref underWeighted,
                ref overWeighted,
                ref totalWeight);
        }

        return FinalizeSphericalFrame(cells, longitudeCells, latitudeCells, cellSolidAngle, etaWeighted, underWeighted, overWeighted, totalWeight);
    }

    private static SphericalProbeFrame RenderSphericalSupportSourceProbe(
        GaussianSplatStream stream,
        Candidate[] candidates,
        SplatPackRotationOrder rotationOrder,
        int longitudeCells,
        int latitudeCells,
        Vector3 center,
        float axisLengthCap,
        SplatPackBuildOptions options)
    {
        SphericalProbeCell[] cells = CreateSphericalCells(longitudeCells * latitudeCells);
        double etaWeighted = 0.0;
        double underWeighted = 0.0;
        double overWeighted = 0.0;
        double totalWeight = 0.0;
        float cellSolidAngle = 4f * MathF.PI / cells.Length;

        foreach (Candidate candidate in candidates)
        {
            if (IsUnsupportedGiantAxisCandidate(candidate, axisLengthCap, options))
            {
                continue;
            }

            float supportScore = AngularSupportScore(stream, candidate, axisLengthCap, rotationOrder, out int renderClass);
            if (supportScore <= 1e-5f || renderClass == RenderHintLayerRisk)
            {
                continue;
            }

            int index = candidate.SourceIndex;
            Vector3 position = Position(stream, index);
            GetCandidateAxes(stream, index, rotationOrder, axisLengthCap, 8f, out Vector3 axis0, out Vector3 axis1, out Vector3 axis2);
            Vector3 rgb = ColorForCandidate(stream, index);
            float opacity = Math.Clamp(Sigmoid(stream.OpacitiesRaw[index]), 0f, 1f);
            AddSphericalSplat(
                cells,
                longitudeCells,
                latitudeCells,
                center,
                position,
                axis0,
                axis1,
                axis2,
                rgb,
                opacity,
                renderClass,
                cellSolidAngle,
                ref etaWeighted,
                ref underWeighted,
                ref overWeighted,
                ref totalWeight);
        }

        return FinalizeSphericalFrame(cells, longitudeCells, latitudeCells, cellSolidAngle, etaWeighted, underWeighted, overWeighted, totalWeight);
    }

    private static SphericalProbeFrame RenderSphericalPackageProbe(
        SplatPackPackage package,
        int longitudeCells,
        int latitudeCells,
        Vector3 center)
    {
        SphericalProbeCell[] cells = CreateSphericalCells(longitudeCells * latitudeCells);
        double etaWeighted = 0.0;
        double underWeighted = 0.0;
        double overWeighted = 0.0;
        double totalWeight = 0.0;
        float cellSolidAngle = 4f * MathF.PI / cells.Length;

        foreach (SplatPackSplat splat in package.Splats)
        {
            AddSphericalSplat(
                cells,
                longitudeCells,
                latitudeCells,
                center,
                new Vector3(splat.CenterWS.X, splat.CenterWS.Y, splat.CenterWS.Z),
                new Vector3(splat.Axis0WS.X, splat.Axis0WS.Y, splat.Axis0WS.Z),
                new Vector3(splat.Axis1WS.X, splat.Axis1WS.Y, splat.Axis1WS.Z),
                new Vector3(splat.Axis2WS.X, splat.Axis2WS.Y, splat.Axis2WS.Z),
                new Vector3(splat.Color.X, splat.Color.Y, splat.Color.Z),
                Math.Clamp(splat.Color.W, 0f, 1f),
                DecodeRenderHintClass(splat),
                cellSolidAngle,
                ref etaWeighted,
                ref underWeighted,
                ref overWeighted,
                ref totalWeight);
        }

        return FinalizeSphericalFrame(cells, longitudeCells, latitudeCells, cellSolidAngle, etaWeighted, underWeighted, overWeighted, totalWeight);
    }

    private static SphericalProbeCell[] CreateSphericalCells(int count)
    {
        var cells = new SphericalProbeCell[count];
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i] = new SphericalProbeCell();
        }

        return cells;
    }

    private static void AddSphericalSplat(
        SphericalProbeCell[] cells,
        int longitudeCells,
        int latitudeCells,
        Vector3 center,
        Vector3 position,
        Vector3 axis0,
        Vector3 axis1,
        Vector3 axis2,
        Vector3 rgb,
        float opacity,
        int renderClass,
        float cellSolidAngle,
        ref double etaWeighted,
        ref double underWeighted,
        ref double overWeighted,
        ref double totalWeight)
    {
        if (opacity <= SphericalProbeEpsilon)
        {
            return;
        }

        Vector3 delta = position - center;
        float distance = MathF.Max(SphericalProbeMinDistanceMeters, delta.Length());
        Vector3 direction = distance > SphericalProbeMinDistanceMeters ? Vector3.Normalize(delta) : Vector3.UnitZ;
        int cellIndex = SphericalCellIndex(direction, longitudeCells, latitudeCells);
        AxisPair(axis0, axis1, axis2, out float majorAxis, out float minorAxis);
        float majorAngle = Math.Clamp(majorAxis / distance, 1e-5f, MathF.PI * 0.5f);
        float minorAngle = Math.Clamp(minorAxis / distance, 1e-5f, MathF.PI * 0.5f);
        float footprintSolidAngle = MathF.PI * majorAngle * minorAngle;
        float eta = footprintSolidAngle / MathF.Max(1e-8f, cellSolidAngle);
        float tau = -MathF.Log(MathF.Max(1e-5f, 1f - Math.Clamp(opacity, 0f, 0.9999f)))
                    * Math.Clamp(eta, 0.02f, 6f);
        float saturation = Saturation(rgb);
        float surfaceWeight = SurfaceClassWeight(renderClass);
        float microWeight = MicroClassWeight(renderClass);
        float freeWeight = FreeClassWeight(renderClass);
        float etaMid = 1f - Math.Clamp(MathF.Abs(MathF.Log2(MathF.Max(1e-4f, eta))) / 4f, 0f, 1f);
        float under = eta < 0.55f ? 1f - eta / 0.55f : 0f;
        float over = eta > 2.5f ? Math.Clamp((eta - 2.5f) / 8f, 0f, 1f) : 0f;
        float weight = tau * MathF.Max(0.1f, 1f - freeWeight * 0.6f);

        SphericalProbeCell cell = cells[cellIndex];
        cell.Tau += tau;
        cell.SurfaceMass += tau * surfaceWeight;
        cell.MicroMass += tau * microWeight;
        cell.FreeMass += tau * freeWeight;
        cell.LowBand += tau * surfaceWeight * (1f - saturation * 0.45f);
        cell.MidBand += tau * (0.25f + saturation * 0.75f) * etaMid;
        cell.HighBand += tau * (microWeight * 0.75f + saturation * 0.35f + under * 0.3f);

        etaWeighted += eta * weight;
        underWeighted += under * weight;
        overWeighted += over * weight;
        totalWeight += weight;
    }

    private static SphericalProbeFrame FinalizeSphericalFrame(
        SphericalProbeCell[] cells,
        int longitudeCells,
        int latitudeCells,
        float cellSolidAngle,
        double etaWeighted,
        double underWeighted,
        double overWeighted,
        double totalWeight)
    {
        int nonEmpty = 0;
        double solidAngleMass = 0.0;
        foreach (SphericalProbeCell cell in cells)
        {
            cell.Alpha = 1f - MathF.Exp(-(float)Math.Min(60.0, cell.Tau));
            if (cell.Alpha > 0.01f)
            {
                nonEmpty++;
            }

            solidAngleMass += cell.Alpha * cellSolidAngle;
        }

        double denominator = Math.Max(1e-8, totalWeight);
        return new SphericalProbeFrame
        {
            Cells = cells,
            LongitudeCells = longitudeCells,
            LatitudeCells = latitudeCells,
            CellSolidAngle = cellSolidAngle,
            NonEmptyCells = nonEmpty,
            SolidAngleMass = (float)solidAngleMass,
            EtaMean = (float)(etaWeighted / denominator),
            EtaUnderRate = (float)(underWeighted / denominator),
            EtaOverRate = (float)(overWeighted / denominator),
        };
    }

    private static int SphericalCellIndex(Vector3 direction, int longitudeCells, int latitudeCells)
    {
        float y = Math.Clamp(direction.Y * 0.5f + 0.5f, 0f, 0.999999f);
        float phi = MathF.Atan2(direction.X, direction.Z);
        float u = (phi + MathF.PI) / (2f * MathF.PI);
        int x = Math.Clamp((int)MathF.Floor(u * longitudeCells), 0, longitudeCells - 1);
        int row = Math.Clamp((int)MathF.Floor(y * latitudeCells), 0, latitudeCells - 1);
        return row * longitudeCells + x;
    }

    private static Vector3 SphericalCellDirection(int cellIndex, int longitudeCells, int latitudeCells)
    {
        int x = Math.Clamp(cellIndex % longitudeCells, 0, longitudeCells - 1);
        int row = Math.Clamp(cellIndex / longitudeCells, 0, latitudeCells - 1);
        float y = ((row + 0.5f) / MathF.Max(1f, latitudeCells)) * 2f - 1f;
        float phi = ((x + 0.5f) / MathF.Max(1f, longitudeCells)) * (2f * MathF.PI) - MathF.PI;
        float xz = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
        return Vector3.Normalize(new Vector3(MathF.Sin(phi) * xz, y, MathF.Cos(phi) * xz));
    }

    private static int ResolveSphericalSupportFieldRecordCount(SplatPackPackage package)
    {
        int stride = package.SphericalSupportFieldStride > 0
                     && package.SphericalSupportFieldStride % SplatPackWriter.SphericalSupportFieldStride == 0
            ? package.SphericalSupportFieldStride
            : SplatPackWriter.SphericalSupportFieldStride;
        int vector4PerRecord = Math.Max(1, stride / SplatPackWriter.SphericalSupportFieldStride);
        return package.SphericalSupportField.Length / vector4PerRecord;
    }

    private static int ResolveSphericalSupportFieldSupportedCellCount(SplatPackPackage package)
    {
        int recordCount = ResolveSphericalSupportFieldRecordCount(package);
        if (recordCount <= 0)
        {
            return 0;
        }

        int stride = package.SphericalSupportFieldStride > 0
                     && package.SphericalSupportFieldStride % SplatPackWriter.SphericalSupportFieldStride == 0
            ? package.SphericalSupportFieldStride
            : SplatPackWriter.SphericalSupportFieldStride;
        int vector4PerRecord = Math.Max(1, stride / SplatPackWriter.SphericalSupportFieldStride);
        int supported = 0;
        for (int record = 0; record < recordCount; record++)
        {
            int offset = record * vector4PerRecord;
            if (offset < package.SphericalSupportField.Length && package.SphericalSupportField[offset].W > 0.01f)
            {
                supported++;
            }
        }

        return supported;
    }

    private static void AxisPair(Vector3 axis0, Vector3 axis1, Vector3 axis2, out float major, out float minor)
    {
        float a = axis0.Length();
        float b = axis1.Length();
        float c = axis2.Length();
        major = MathF.Max(a, MathF.Max(b, c));
        minor = MathF.Max(MinimumScale, Middle(a, b, c));
    }

    private static float SphericalResidual(
        SphericalProbeFrame source,
        SphericalProbeFrame emitted,
        Func<SphericalProbeCell, float> selector,
        float sourceMass)
    {
        double sum = 0.0;
        int count = Math.Min(source.Cells.Length, emitted.Cells.Length);
        for (int i = 0; i < count; i++)
        {
            sum += Math.Abs(selector(source.Cells[i]) - selector(emitted.Cells[i])) * source.CellSolidAngle;
        }

        return (float)Math.Clamp(sum / Math.Max(1e-6, sourceMass), 0.0, 4.0);
    }

    private static float SphericalMass(SphericalProbeFrame frame, Func<SphericalProbeCell, double> selector)
    {
        return MathF.Max(1e-6f, SphericalRawMass(frame, selector));
    }

    private static float SphericalRawMass(SphericalProbeFrame frame, Func<SphericalProbeCell, double> selector)
    {
        double sum = 0.0;
        foreach (SphericalProbeCell cell in frame.Cells)
        {
            sum += selector(cell) * frame.CellSolidAngle;
        }

        return (float)Math.Max(0.0, sum);
    }

    private static float SurfaceClassWeight(int renderClass)
    {
        return renderClass switch
        {
            RenderHintBroadSurface => 1f,
            RenderHintSurface => 0.85f,
            RenderHintForeground => 0.65f,
            RenderHintLayerRisk => 0.55f,
            _ => 0f,
        };
    }

    private static float MicroClassWeight(int renderClass)
    {
        return renderClass switch
        {
            RenderHintMicroDetail => 1f,
            RenderHintForeground => 0.55f,
            RenderHintLayerRisk => 0.3f,
            _ => 0f,
        };
    }

    private static float FreeClassWeight(int renderClass)
    {
        return renderClass switch
        {
            RenderHintFreeSpace => 1f,
            RenderHintFloater => 1f,
            _ => 0f,
        };
    }

    private static float Saturation(Vector3 rgb)
    {
        float max = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));
        float min = MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z));
        return max > 1e-4f ? Math.Clamp((max - min) / max, 0f, 1f) : 0f;
    }
}
