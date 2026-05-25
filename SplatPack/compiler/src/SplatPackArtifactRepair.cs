using System.Numerics;

namespace SplatPack.Compiler;

public static partial class SplatPackBuilder
{
    private const float ProbeGaussianExtent = 2.8284271247461903f;
    private const float ProbeAlphaClip = 1e-4f;
    private const float ProbeMinDepth = 0.05f;
    private const float ProbeNumericalEpsilon = 1e-7f;

    private sealed class ProbeCell
    {
        public int ViewIndex;
        public int X;
        public int Y;
        public double AlphaMass;
        public double ColorX;
        public double ColorY;
        public double ColorZ;
        public double ColorSq;
        public double DepthMass;
        public double DepthSqMass;
        public double DepthMassWeight;
        public double NearLayerMass;
        public double FarLayerMass;
        public double Transmittance = 1.0;
        public float AlphaGradient;
        public float ColorGradient;
        public float DepthGradient;
        public float EdgeMagnitude;
        public float EdgeCoherence;
        public float TextureEnergy;
        public float DepthVariance;
        public float NearFarSeparation;
        public float CoverageResidual;
        public float EdgeResidual;
        public float TextureResidual;
        public float LayerResidual;
        public float StereoResidual;
        public float CoverageResidualNormalized;
        public float EdgeResidualNormalized;
        public float TextureResidualNormalized;
        public float LayerResidualNormalized;
        public float StereoResidualNormalized;
        public int ArtifactClass;
        public int LayerId;
        public bool BoostSaturated;

        public float Luma => AlphaMass > 1e-8
            ? (float)((0.2126 * ColorX + 0.7152 * ColorY + 0.0722 * ColorZ) / AlphaMass)
            : 0f;

        public float MeanDepth => DepthMassWeight > 1e-8 ? (float)(DepthMass / DepthMassWeight) : 0f;
    }

    private sealed class ProbeView
    {
        public required int Index { get; init; }
        public required int PairIndex { get; init; }
        public required SplatPackProbeEye Eye { get; init; }
        public required Vector3 Position { get; init; }
        public required Vector3 Forward { get; init; }
        public required Vector3 Right { get; init; }
        public required Vector3 Up { get; init; }
        public required float Weight { get; init; }
        public required float FocalX { get; init; }
        public required float FocalY { get; init; }
        public required float Width { get; init; }
        public required float Height { get; init; }
    }

    private sealed class ProbeFrame
    {
        public required ProbeCell[] Cells { get; init; }
        public required List<ProbeSample>[] SamplesByCell { get; init; }
        public required ProbeView[] Views { get; init; }
        public required int CellsPerAxis { get; init; }
        public required int ProbeResolution { get; init; }
        public required int ProbeCellSize { get; init; }
    }

    private sealed class ProbeSample
    {
        public required int SourceIndex { get; init; }
        public required int CellIndex { get; init; }
        public required int ViewIndex { get; init; }
        public required float AlphaKernel { get; init; }
        public required float Depth { get; init; }
        public required Vector3 Color { get; init; }
        public float Contribution { get; set; }
    }

    private readonly record struct RepairCandidate(
        Candidate Candidate,
        float Score,
        int ArtifactClass,
        int LayerId,
        int RepairAction);

    private readonly record struct ProbeContribution(
        int CellIndex,
        int ViewIndex,
        float AlphaKernel,
        float Depth,
        float Kernel,
        Vector3 Color);

    private readonly record struct ProbeProjection(
        float CenterX,
        float CenterY,
        float Depth,
        float Sigma0,
        float Sigma1,
        Vector2 Dir0,
        Vector2 Dir1,
        float TailExtent,
        float Opacity);

    private static bool IsGaussianPullbackRepairEnabled(SplatPackBuildOptions options)
    {
        return (options.ArtifactRepairMode == SplatPackArtifactRepairMode.GaussianPullbackV1
                || options.ArtifactRepairMode == SplatPackArtifactRepairMode.GaussianPullbackV2)
               && options.TargetProfile != SplatPackTargetProfile.Reference
               && options.MaxSplats > 0;
    }

    private static bool IsShellSurfaceRepairEnabled(SplatPackBuildOptions options)
    {
        return options.ArtifactRepairMode == SplatPackArtifactRepairMode.ShellSurfaceV1
               && options.TargetProfile != SplatPackTargetProfile.Reference
               && options.MaxSplats > 0
               && ShouldBuildSphericalSupportField(options);
    }

    private static List<Candidate> ApplyGaussianPullbackRepair(
        GaussianSplatStream stream,
        Candidate[] allCandidates,
        List<Candidate> initialSelected,
        SplatPackBounds bounds,
        float axisLengthCap,
        SplatPackRotationOrder rotationOrder,
        SplatPackBuildOptions options,
        int finalBudget,
        out ArtifactRepairSummary summary)
    {
        int probeResolution = Math.Clamp(options.ProbeResolution, 16, 1024);
        int probeCellSize = Math.Clamp(options.ProbeCellSize, 1, 32);
        int cellsPerAxis = Math.Clamp(probeResolution / probeCellSize, 4, 256);
        ProbeView[] views = ResolveProbeViews(bounds, options, probeResolution);
        ProbeFrame sourceFrame = RenderProbeFrame(stream, allCandidates, bounds, axisLengthCap, rotationOrder, options, views, cellsPerAxis, probeResolution, probeCellSize);

        var selected = new List<Candidate>(initialSelected.Count);
        var selectedSources = new HashSet<int>();
        foreach (Candidate candidate in initialSelected)
        {
            selected.Add(candidate with
            {
                CoverageBoost = 1f,
                ArtifactClass = ArtifactNone,
                LayerId = LayerNone,
                RepairAction = RepairActionNone,
            });
            selectedSources.Add(candidate.SourceIndex);
        }

        ProbeFrame selectedFrame = RenderProbeFrame(stream, selected, bounds, axisLengthCap, rotationOrder, options, views, cellsPerAxis, probeResolution, probeCellSize);
        ProbeCell[] initialResidual = BuildResidualCells(sourceFrame, selectedFrame);
        float residualBefore = WeightedResidualEnergy(initialResidual, options.PullbackWeights);
        float stereoBefore = StereoMismatch(initialResidual);
        int rejectedRepairs = 0;
        int layerCrossBoostRejected = 0;
        int repairIterations = Math.Clamp(options.RepairIterations, 1, 8);

        for (int iteration = 0; iteration < repairIterations; iteration++)
        {
            selectedFrame = RenderProbeFrame(stream, selected, bounds, axisLengthCap, rotationOrder, options, views, cellsPerAxis, probeResolution, probeCellSize);
            ProbeCell[] residualCells = BuildResidualCells(sourceFrame, selectedFrame);
            layerCrossBoostRejected += ApplyStrictCoverageBoost(stream, selected, selectedFrame, residualCells, bounds, axisLengthCap, rotationOrder, options, views, cellsPerAxis);
            if (layerCrossBoostRejected > 0)
            {
                selectedFrame = RenderProbeFrame(stream, selected, bounds, axisLengthCap, rotationOrder, options, views, cellsPerAxis, probeResolution, probeCellSize);
                residualCells = BuildResidualCells(sourceFrame, selectedFrame);
            }

            int remainingBudget = Math.Max(0, finalBudget - selected.Count);
            if (remainingBudget == 0)
            {
                continue;
            }

            int batchBudget = Math.Max(1, (int)MathF.Ceiling(remainingBudget / (float)(repairIterations - iteration)));
            var scored = new List<RepairCandidate>(Math.Min(allCandidates.Length, Math.Max(16, batchBudget * 24)));
            foreach (Candidate candidate in allCandidates)
            {
                if (selectedSources.Contains(candidate.SourceIndex))
                {
                    continue;
                }

                RepairCandidate repair = ScoreGaussianPullbackCandidate(
                    stream,
                    candidate,
                    sourceFrame,
                    selectedFrame,
                    residualCells,
                    bounds,
                    axisLengthCap,
                    rotationOrder,
                    options,
                    views,
                    cellsPerAxis);
                if (repair.Score > ProbeNumericalEpsilon && repair.ArtifactClass != ArtifactNone)
                {
                    scored.Add(repair);
                }
                else
                {
                    rejectedRepairs++;
                }
            }

            scored.Sort(CompareRepairCandidates);
            int acceptedThisIteration = 0;
            foreach (RepairCandidate repair in scored)
            {
                if (selected.Count >= finalBudget || acceptedThisIteration >= batchBudget)
                {
                    break;
                }

                if (!selectedSources.Add(repair.Candidate.SourceIndex))
                {
                    continue;
                }

                selected.Add(repair.Candidate with
                {
                    CoverageBoost = 1f,
                    ArtifactClass = repair.ArtifactClass,
                    LayerId = repair.LayerId,
                    RepairAction = repair.RepairAction,
                });
                acceptedThisIteration++;
            }
        }

        ProbeFrame finalSelectedFrame = RenderProbeFrame(stream, selected, bounds, axisLengthCap, rotationOrder, options, views, cellsPerAxis, probeResolution, probeCellSize);
        ProbeCell[] finalResidual = BuildResidualCells(sourceFrame, finalSelectedFrame);
        summary = BuildArtifactRepairSummary(
            options,
            views,
            initialSelected.Count,
            finalBudget,
            selected,
            initialResidual,
            finalResidual,
            residualBefore,
            WeightedResidualEnergy(finalResidual, options.PullbackWeights),
            stereoBefore,
            StereoMismatch(finalResidual),
            rejectedRepairs,
            layerCrossBoostRejected);
        return selected;
    }

    private static ProbeView[] ResolveProbeViews(SplatPackBounds bounds, SplatPackBuildOptions options, int probeResolution)
    {
        SplatPackViewSample[] samples = options.ProbeViewSamples.Length > 0
            ? options.ProbeViewSamples.Take(Math.Clamp(options.ProbeViewCount, 1, options.ProbeViewSamples.Length)).ToArray()
            : options.ViewSamples.Length > 0
                ? options.ViewSamples.Take(Math.Clamp(options.ProbeViewCount, 1, options.ViewSamples.Length)).ToArray()
                : ResolveDefaultProbeSamples(bounds, options);

        var views = new List<ProbeView>(samples.Length * 2);
        foreach (SplatPackViewSample sample in samples)
        {
            AddProbeViewsForSample(views, sample, options, probeResolution);
        }

        return views.Count > 0 ? views.ToArray() : ResolveFallbackProbeViews(bounds, options, probeResolution);
    }

    private static SplatPackViewSample[] ResolveDefaultProbeSamples(SplatPackBounds bounds, SplatPackBuildOptions options)
    {
        Vector3 center = (bounds.Min + bounds.Max) * 0.5f;
        float radius = MathF.Max(0.01f, (bounds.Max - bounds.Min).Length() * 0.5f);
        SplatPackViewSample[] defaults = CreateDefaultViewSamples(center, radius);
        int count = Math.Clamp(options.ProbeViewCount, 1, Math.Max(1, options.ProbeViewCount));
        if (count <= defaults.Length)
        {
            return defaults.Take(count).ToArray();
        }

        var views = new List<SplatPackViewSample>(count);
        views.AddRange(defaults);
        float cameraDistance = MathF.Max(0.25f, radius * 2.0f);
        for (int i = views.Count; i < count; i++)
        {
            float t = i * 2.3999632f;
            float z = 1f - 2f * ((i + 0.5f) / count);
            float r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
            Vector3 direction = Vector3.Normalize(new Vector3(MathF.Cos(t) * r, z, MathF.Sin(t) * r));
            views.Add(new SplatPackViewSample(center + direction * cameraDistance, -direction, 1f));
        }

        return views.ToArray();
    }

    private static ProbeView[] ResolveFallbackProbeViews(SplatPackBounds bounds, SplatPackBuildOptions options, int probeResolution)
    {
        return ResolveDefaultProbeSamples(bounds, options)
            .SelectMany(sample =>
            {
                var list = new List<ProbeView>(2);
                AddProbeViewsForSample(list, sample, options, probeResolution);
                return list;
            })
            .ToArray();
    }

    private static void AddProbeViewsForSample(List<ProbeView> views, SplatPackViewSample sample, SplatPackBuildOptions options, int probeResolution)
    {
        Vector3 forward = sample.Forward.LengthSquared() > 1e-12f ? Vector3.Normalize(sample.Forward) : Vector3.UnitZ;
        Vector3 upGuess = MathF.Abs(forward.Y) > 0.92f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 right = Vector3.Normalize(Vector3.Cross(upGuess, forward));
        Vector3 up = Vector3.Normalize(Vector3.Cross(forward, right));
        float ipd = sample.IpdMeters > 0f ? sample.IpdMeters : options.ProbeIpdMeters;
        float fovY = sample.FovYDegrees > 0f ? sample.FovYDegrees : options.ProbeFovYDegrees;
        float aspect = sample.Aspect > 0f ? sample.Aspect : options.ProbeAspect;

        if (sample.Eye == SplatPackProbeEye.Left || sample.Eye == SplatPackProbeEye.Right)
        {
            int index = views.Count;
            views.Add(CreateProbeView(index, -1, sample.Eye, sample.Position, forward, right, up, sample.Weight, fovY, aspect, probeResolution));
            return;
        }

        int leftIndex = views.Count;
        int rightIndex = views.Count + 1;
        views.Add(CreateProbeView(leftIndex, rightIndex, SplatPackProbeEye.Left, sample.Position - right * (ipd * 0.5f), forward, right, up, sample.Weight, fovY, aspect, probeResolution));
        views.Add(CreateProbeView(rightIndex, leftIndex, SplatPackProbeEye.Right, sample.Position + right * (ipd * 0.5f), forward, right, up, sample.Weight, fovY, aspect, probeResolution));
    }

    private static ProbeView CreateProbeView(
        int index,
        int pairIndex,
        SplatPackProbeEye eye,
        Vector3 position,
        Vector3 forward,
        Vector3 right,
        Vector3 up,
        float weight,
        float fovY,
        float aspect,
        int probeResolution)
    {
        float height = MathF.Max(1f, probeResolution);
        float width = MathF.Max(1f, height * Math.Clamp(aspect, 0.25f, 4f));
        float focalY = height / (2f * MathF.Tan(Math.Clamp(fovY, 20f, 140f) * MathF.PI / 360f));
        float focalX = focalY;
        return new ProbeView
        {
            Index = index,
            PairIndex = pairIndex,
            Eye = eye,
            Position = position,
            Forward = forward,
            Right = right,
            Up = up,
            Weight = MathF.Max(0f, weight),
            FocalX = focalX,
            FocalY = focalY,
            Width = width,
            Height = height,
        };
    }

    private static ProbeFrame RenderProbeFrame(
        GaussianSplatStream stream,
        IReadOnlyList<Candidate> candidates,
        SplatPackBounds bounds,
        float axisLengthCap,
        SplatPackRotationOrder rotationOrder,
        SplatPackBuildOptions options,
        ProbeView[] views,
        int cellsPerAxis,
        int probeResolution,
        int probeCellSize)
    {
        var cells = new ProbeCell[views.Length * cellsPerAxis * cellsPerAxis];
        var samplesByCell = new List<ProbeSample>[cells.Length];
        for (int view = 0; view < views.Length; view++)
        {
            for (int y = 0; y < cellsPerAxis; y++)
            {
                for (int x = 0; x < cellsPerAxis; x++)
                {
                    int index = CellIndex(view, x, y, cellsPerAxis);
                    cells[index] = new ProbeCell { ViewIndex = view, X = x, Y = y };
                    samplesByCell[index] = new List<ProbeSample>();
                }
            }
        }

        foreach (Candidate candidate in candidates)
        {
            foreach (ProbeContribution contribution in ProjectCandidateToProbeCells(stream, candidate, bounds, axisLengthCap, rotationOrder, options, views, cellsPerAxis, probeCellSize))
            {
                samplesByCell[contribution.CellIndex].Add(new ProbeSample
                {
                    SourceIndex = candidate.SourceIndex,
                    CellIndex = contribution.CellIndex,
                    ViewIndex = contribution.ViewIndex,
                    AlphaKernel = contribution.AlphaKernel,
                    Depth = contribution.Depth,
                    Color = contribution.Color,
                });
            }
        }

        for (int i = 0; i < samplesByCell.Length; i++)
        {
            CompositeCell(cells[i], samplesByCell[i], views[cells[i].ViewIndex], Math.Max(1, options.ProbeLayerBins));
        }

        DeriveProbeOperators(cells, cellsPerAxis);
        return new ProbeFrame
        {
            Cells = cells,
            SamplesByCell = samplesByCell,
            Views = views,
            CellsPerAxis = cellsPerAxis,
            ProbeResolution = probeResolution,
            ProbeCellSize = probeCellSize,
        };
    }

    private static void CompositeCell(ProbeCell cell, List<ProbeSample> samples, ProbeView view, int layerBins)
    {
        if (samples.Count == 0)
        {
            cell.Transmittance = 1.0;
            return;
        }

        samples.Sort((a, b) =>
        {
            int byDepth = a.Depth.CompareTo(b.Depth);
            return byDepth != 0 ? byDepth : a.SourceIndex.CompareTo(b.SourceIndex);
        });

        float splitDepth = layerBins > 1 ? LayerSplitDepth(samples) : float.PositiveInfinity;
        double transmittance = 1.0;
        foreach (ProbeSample sample in samples)
        {
            float alpha = Math.Clamp(sample.AlphaKernel, 0f, 0.999f);
            double contribution = transmittance * alpha * view.Weight;
            sample.Contribution = (float)contribution;
            cell.AlphaMass += contribution;
            cell.ColorX += contribution * sample.Color.X;
            cell.ColorY += contribution * sample.Color.Y;
            cell.ColorZ += contribution * sample.Color.Z;
            float luma = Luma(sample.Color);
            cell.ColorSq += contribution * luma * luma;
            cell.DepthMass += contribution * sample.Depth;
            cell.DepthSqMass += contribution * sample.Depth * sample.Depth;
            cell.DepthMassWeight += contribution;
            if (sample.Depth <= splitDepth)
            {
                cell.NearLayerMass += contribution;
            }
            else
            {
                cell.FarLayerMass += contribution;
            }

            transmittance *= 1.0 - alpha;
            if (transmittance <= 1e-6)
            {
                break;
            }
        }

        cell.Transmittance = transmittance;
    }

    private static float LayerSplitDepth(List<ProbeSample> samples)
    {
        if (samples.Count <= 1)
        {
            return float.PositiveInfinity;
        }

        float bestGap = 0f;
        float split = samples[0].Depth;
        for (int i = 1; i < samples.Count; i++)
        {
            float gap = samples[i].Depth - samples[i - 1].Depth;
            if (gap > bestGap)
            {
                bestGap = gap;
                split = (samples[i].Depth + samples[i - 1].Depth) * 0.5f;
            }
        }

        return bestGap > 0.02f ? split : samples[samples.Count / 2].Depth;
    }

    private static bool TryProjectCandidate(
        GaussianSplatStream stream,
        Candidate candidate,
        SplatPackBounds bounds,
        float axisLengthCap,
        SplatPackRotationOrder rotationOrder,
        SplatPackBuildOptions options,
        ProbeView view,
        out ProbeProjection projection)
    {
        projection = default;
        Vector3 centerWS = Position(stream, candidate.SourceIndex);
        Vector3 cameraToPoint = centerWS - view.Position;
        float centerX = Vector3.Dot(cameraToPoint, view.Right);
        float centerY = Vector3.Dot(cameraToPoint, view.Up);
        float depth = Vector3.Dot(cameraToPoint, view.Forward);
        if (depth <= ProbeMinDepth)
        {
            return false;
        }

        float ndcX = centerX * view.FocalX / MathF.Max(1e-6f, depth) / (view.Width * 0.5f);
        float ndcY = centerY * view.FocalY / MathF.Max(1e-6f, depth) / (view.Height * 0.5f);
        float pixelX = (ndcX * 0.5f + 0.5f) * view.Width;
        float pixelY = (ndcY * 0.5f + 0.5f) * view.Height;

        GetCandidateAxes(
            stream,
            candidate.SourceIndex,
            rotationOrder,
            axisLengthCap,
            options.MaxAxisRatio,
            candidate.ArtifactClass,
            candidate.RepairAction,
            out Vector3 axis0WS,
            out Vector3 axis1WS,
            out Vector3 axis2WS);
        Vector3 axis0VS = ToProbeView(axis0WS, view);
        Vector3 axis1VS = ToProbeView(axis1WS, view);
        Vector3 axis2VS = ToProbeView(axis2WS, view);

        float covXX = Dot3(axis0VS.X, axis1VS.X, axis2VS.X, axis0VS.X, axis1VS.X, axis2VS.X);
        float covXY = Dot3(axis0VS.X, axis1VS.X, axis2VS.X, axis0VS.Y, axis1VS.Y, axis2VS.Y);
        float covXZ = Dot3(axis0VS.X, axis1VS.X, axis2VS.X, axis0VS.Z, axis1VS.Z, axis2VS.Z);
        float covYY = Dot3(axis0VS.Y, axis1VS.Y, axis2VS.Y, axis0VS.Y, axis1VS.Y, axis2VS.Y);
        float covYZ = Dot3(axis0VS.Y, axis1VS.Y, axis2VS.Y, axis0VS.Z, axis1VS.Z, axis2VS.Z);
        float covZZ = Dot3(axis0VS.Z, axis1VS.Z, axis2VS.Z, axis0VS.Z, axis1VS.Z, axis2VS.Z);

        float invDepth = 1f / depth;
        float invDepthSq = invDepth * invDepth;
        float j00 = view.FocalX * invDepth;
        float j02 = -view.FocalX * centerX * invDepthSq;
        float j11 = view.FocalY * invDepth;
        float j12 = -view.FocalY * centerY * invDepthSq;
        float baseC00 = j00 * j00 * covXX + 2f * j00 * j02 * covXZ + j02 * j02 * covZZ;
        float baseC01 = j00 * j11 * covXY + j00 * j12 * covXZ + j02 * j11 * covYZ + j02 * j12 * covZZ;
        float baseC11 = j11 * j11 * covYY + 2f * j11 * j12 * covYZ + j12 * j12 * covZZ;
        float filterVariance = FilterVarianceForProbe(options);
        float c00 = baseC00 + filterVariance;
        float c01 = baseC01;
        float c11 = baseC11 + filterVariance;
        float baseDeterminant = MathF.Max(1e-8f, baseC00 * baseC11 - baseC01 * baseC01);
        float filteredDeterminant = MathF.Max(baseDeterminant, c00 * c11 - c01 * c01);
        float filterAlphaScale = MathF.Sqrt(Math.Clamp(baseDeterminant / filteredDeterminant, 0f, 1f));
        if (options.FilterMode != SplatPackFilterMode.None)
        {
            filterAlphaScale = MathF.Max(filterAlphaScale, options.FilterMode == SplatPackFilterMode.AnalyticPixel ? 0.38f : 0.55f);
        }

        SolveEigen2(c00, c01, c11, out float lambda0, out float lambda1, out Vector2 dir0);
        if (lambda1 <= 0f || !IsFinite(lambda0) || !IsFinite(lambda1))
        {
            return false;
        }

        Vector2 dir1 = new(dir0.Y, -dir0.X);
        float opacity = Math.Clamp(ApplyCoverageBoost(candidate.Opacity, candidate.CoverageBoost) * filterAlphaScale, 0f, 0.999f);
        if (opacity <= ProbeAlphaClip)
        {
            return false;
        }

        float tailExtent = MathF.Sqrt(MathF.Max(1e-4f, -2f * MathF.Log(Math.Clamp(ProbeAlphaClip / MathF.Max(opacity, 1e-6f), 1e-6f, 1f))));
        tailExtent = Math.Clamp(tailExtent, 0.5f, ProbeGaussianExtent);
        projection = new ProbeProjection(
            pixelX,
            pixelY,
            depth,
            MathF.Sqrt(MathF.Max(1e-8f, lambda0)),
            MathF.Sqrt(MathF.Max(1e-8f, lambda1)),
            dir0,
            dir1,
            tailExtent,
            opacity);
        return true;
    }

    private static IEnumerable<ProbeContribution> ProjectCandidateToProbeCells(
        GaussianSplatStream stream,
        Candidate candidate,
        SplatPackBounds bounds,
        float axisLengthCap,
        SplatPackRotationOrder rotationOrder,
        SplatPackBuildOptions options,
        ProbeView[] views,
        int cellsPerAxis,
        int probeCellSize)
    {
        Vector3 rgb = ColorForCandidate(stream, candidate.SourceIndex);
        for (int viewIndex = 0; viewIndex < views.Length; viewIndex++)
        {
            ProbeView view = views[viewIndex];
            if (!TryProjectCandidate(stream, candidate, bounds, axisLengthCap, rotationOrder, options, view, out ProbeProjection projection))
            {
                continue;
            }

            float radiusX = MathF.Abs(projection.Dir0.X) * projection.Sigma0 * projection.TailExtent
                            + MathF.Abs(projection.Dir1.X) * projection.Sigma1 * projection.TailExtent;
            float radiusY = MathF.Abs(projection.Dir0.Y) * projection.Sigma0 * projection.TailExtent
                            + MathF.Abs(projection.Dir1.Y) * projection.Sigma1 * projection.TailExtent;
            int minX = Math.Clamp((int)MathF.Floor((projection.CenterX - radiusX) / probeCellSize), 0, cellsPerAxis - 1);
            int maxX = Math.Clamp((int)MathF.Floor((projection.CenterX + radiusX) / probeCellSize), 0, cellsPerAxis - 1);
            int minY = Math.Clamp((int)MathF.Floor((projection.CenterY - radiusY) / probeCellSize), 0, cellsPerAxis - 1);
            int maxY = Math.Clamp((int)MathF.Floor((projection.CenterY + radiusY) / probeCellSize), 0, cellsPerAxis - 1);
            if (maxX < 0 || maxY < 0 || minX >= cellsPerAxis || minY >= cellsPerAxis)
            {
                continue;
            }

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float sampleX = (x + 0.5f) * probeCellSize;
                    float sampleY = (y + 0.5f) * probeCellSize;
                    Vector2 delta = new(sampleX - projection.CenterX, sampleY - projection.CenterY);
                    float u = Vector2.Dot(delta, projection.Dir0) / MathF.Max(1e-6f, projection.Sigma0);
                    float v = Vector2.Dot(delta, projection.Dir1) / MathF.Max(1e-6f, projection.Sigma1);
                    float r2 = u * u + v * v;
                    if (r2 > projection.TailExtent * projection.TailExtent)
                    {
                        continue;
                    }

                    float kernel = MathF.Exp(-0.5f * r2);
                    float alphaKernel = Math.Clamp(projection.Opacity * kernel, 0f, 0.999f);
                    if (alphaKernel <= ProbeNumericalEpsilon)
                    {
                        continue;
                    }

                    yield return new ProbeContribution(
                        CellIndex(viewIndex, x, y, cellsPerAxis),
                        viewIndex,
                        alphaKernel,
                        projection.Depth,
                        kernel,
                        rgb);
                }
            }
        }
    }

    private static void DeriveProbeOperators(ProbeCell[] cells, int cellsPerAxis)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            ProbeCell cell = cells[i];
            double mass = Math.Max(1e-8, cell.AlphaMass);
            float meanLuma = cell.Luma;
            float colorVariance = MathF.Max(0f, (float)(cell.ColorSq / mass) - meanLuma * meanLuma);
            float meanDepth = cell.DepthMassWeight > 1e-8 ? (float)(cell.DepthMass / cell.DepthMassWeight) : 0f;
            cell.DepthVariance = cell.DepthMassWeight > 1e-8
                ? MathF.Max(0f, (float)(cell.DepthSqMass / cell.DepthMassWeight) - meanDepth * meanDepth)
                : 0f;
            cell.NearFarSeparation = MathF.Sqrt(cell.DepthVariance);
            cell.TextureEnergy = colorVariance;
        }

        int viewCount = cells.Length / Math.Max(1, cellsPerAxis * cellsPerAxis);
        for (int view = 0; view < viewCount; view++)
        {
            for (int y = 0; y < cellsPerAxis; y++)
            {
                for (int x = 0; x < cellsPerAxis; x++)
                {
                    ProbeCell center = cells[CellIndex(view, x, y, cellsPerAxis)];
                    ProbeCell left = cells[CellIndex(view, Math.Max(0, x - 1), y, cellsPerAxis)];
                    ProbeCell right = cells[CellIndex(view, Math.Min(cellsPerAxis - 1, x + 1), y, cellsPerAxis)];
                    ProbeCell down = cells[CellIndex(view, x, Math.Max(0, y - 1), cellsPerAxis)];
                    ProbeCell up = cells[CellIndex(view, x, Math.Min(cellsPerAxis - 1, y + 1), cellsPerAxis)];
                    float alphaDx = (float)(right.AlphaMass - left.AlphaMass) * 0.5f;
                    float alphaDy = (float)(up.AlphaMass - down.AlphaMass) * 0.5f;
                    float colorDx = (right.Luma - left.Luma) * 0.5f;
                    float colorDy = (up.Luma - down.Luma) * 0.5f;
                    float depthDx = (right.MeanDepth - left.MeanDepth) * 0.5f;
                    float depthDy = (up.MeanDepth - down.MeanDepth) * 0.5f;
                    center.AlphaGradient = MathF.Sqrt(alphaDx * alphaDx + alphaDy * alphaDy);
                    center.ColorGradient = MathF.Sqrt(colorDx * colorDx + colorDy * colorDy);
                    center.DepthGradient = MathF.Sqrt(depthDx * depthDx + depthDy * depthDy);
                    center.EdgeMagnitude = center.AlphaGradient + center.ColorGradient + center.DepthGradient * 0.1f;
                    float anisotropy = MathF.Abs(alphaDx * alphaDx + colorDx * colorDx - alphaDy * alphaDy - colorDy * colorDy);
                    float energy = center.AlphaGradient + center.ColorGradient + 1e-6f;
                    center.EdgeCoherence = Math.Clamp(anisotropy / energy, 0f, 1f);
                    float laplacian = right.Luma + left.Luma + up.Luma + down.Luma - 4f * center.Luma;
                    center.TextureEnergy += MathF.Abs(laplacian) * MathF.Max(0.25f, (float)Math.Min(1.0, center.AlphaMass));
                }
            }
        }
    }

    private static ProbeCell[] BuildResidualCells(ProbeFrame sourceFrame, ProbeFrame selectedFrame)
    {
        ProbeCell[] sourceCells = sourceFrame.Cells;
        ProbeCell[] selectedCells = selectedFrame.Cells;
        var residual = new ProbeCell[sourceCells.Length];
        for (int i = 0; i < sourceCells.Length; i++)
        {
            ProbeCell source = sourceCells[i];
            ProbeCell selected = selectedCells[i];
            var cell = new ProbeCell { ViewIndex = source.ViewIndex, X = source.X, Y = source.Y };
            cell.CoverageResidual = MathF.Max(0f, (float)(source.AlphaMass - selected.AlphaMass));
            cell.EdgeResidual = MathF.Max(0f, source.EdgeMagnitude - selected.EdgeMagnitude);
            cell.TextureResidual = MathF.Max(0f, source.TextureEnergy - selected.TextureEnergy);
            float layerMassResidual = (float)(Math.Abs(source.NearLayerMass - selected.NearLayerMass) + Math.Abs(source.FarLayerMass - selected.FarLayerMass));
            cell.LayerResidual = MathF.Max(0f, layerMassResidual + MathF.Max(0f, source.DepthVariance - selected.DepthVariance));
            cell.LayerId = source.NearLayerMass >= source.FarLayerMass ? LayerNear : LayerFar;

            ProbeView view = sourceFrame.Views[source.ViewIndex];
            if (view.PairIndex >= 0)
            {
                int pairCell = CellIndex(view.PairIndex, source.X, source.Y, sourceFrame.CellsPerAxis);
                if (pairCell >= 0 && pairCell < sourceCells.Length)
                {
                    float pairCoverage = MathF.Max(0f, (float)(sourceCells[pairCell].AlphaMass - selectedCells[pairCell].AlphaMass));
                    float pairEdge = MathF.Max(0f, sourceCells[pairCell].EdgeMagnitude - selectedCells[pairCell].EdgeMagnitude);
                    cell.StereoResidual = MathF.Abs(cell.CoverageResidual - pairCoverage) + MathF.Abs(cell.EdgeResidual - pairEdge);
                }
            }

            cell.CoverageResidualNormalized = NormalizeResidual(cell.CoverageResidual, (float)source.AlphaMass);
            cell.EdgeResidualNormalized = NormalizeResidual(cell.EdgeResidual, source.EdgeMagnitude);
            cell.TextureResidualNormalized = NormalizeResidual(cell.TextureResidual, source.TextureEnergy);
            cell.LayerResidualNormalized = NormalizeResidual(cell.LayerResidual, (float)(source.NearLayerMass + source.FarLayerMass) + source.DepthVariance);
            cell.StereoResidualNormalized = NormalizeResidual(cell.StereoResidual, (float)source.AlphaMass + source.EdgeMagnitude);
            cell.ArtifactClass = ClassifyResidualCell(cell);
            residual[i] = cell;
        }

        return residual;
    }

    private static int ClassifyResidualCell(ProbeCell residual)
    {
        if (residual.CoverageResidual <= ProbeNumericalEpsilon
            && residual.EdgeResidual <= ProbeNumericalEpsilon
            && residual.TextureResidual <= ProbeNumericalEpsilon
            && residual.LayerResidual <= ProbeNumericalEpsilon
            && residual.StereoResidual <= ProbeNumericalEpsilon)
        {
            return ArtifactNone;
        }

        float best = residual.CoverageResidualNormalized;
        int artifact = ArtifactCoverage;
        if (residual.EdgeResidualNormalized > best)
        {
            best = residual.EdgeResidualNormalized;
            artifact = ArtifactEdge;
        }

        if (residual.TextureResidualNormalized > best)
        {
            best = residual.TextureResidualNormalized;
            artifact = ArtifactTexture;
        }

        if (residual.LayerResidualNormalized > best)
        {
            best = residual.LayerResidualNormalized;
            artifact = ArtifactLayer;
        }

        if (residual.StereoResidualNormalized > best)
        {
            artifact = ArtifactStereo;
        }

        return artifact;
    }

    private static int ApplyStrictCoverageBoost(
        GaussianSplatStream stream,
        List<Candidate> selected,
        ProbeFrame selectedFrame,
        ProbeCell[] residualCells,
        SplatPackBounds bounds,
        float axisLengthCap,
        SplatPackRotationOrder rotationOrder,
        SplatPackBuildOptions options,
        ProbeView[] views,
        int cellsPerAxis)
    {
        if (!options.EnableCoverageCompensation)
        {
            return 0;
        }

        int rejectedCrossBoundaryBoosts = 0;
        for (int i = 0; i < selected.Count; i++)
        {
            Candidate candidate = selected[i];
            float bestBoost = candidate.CoverageBoost;
            bool candidateHadRejectedBoundary = false;
            foreach (ProbeContribution contribution in ProjectCandidateToProbeCells(stream, candidate, bounds, axisLengthCap, rotationOrder, options, views, cellsPerAxis, selectedFrame.ProbeCellSize))
            {
                ProbeCell residual = residualCells[contribution.CellIndex];
                if (residual.ArtifactClass == ArtifactNone)
                {
                    continue;
                }

                if (residual.ArtifactClass != ArtifactCoverage)
                {
                    candidateHadRejectedBoundary = true;
                    residual.BoostSaturated = true;
                    continue;
                }

                float tBefore = TransmittanceBefore(selectedFrame, contribution.CellIndex, contribution.Depth) * views[contribution.ViewIndex].Weight;
                float boost = EstimateRequiredBoost(candidate, contribution, residual.CoverageResidual, tBefore, options.MaxCoverageBoost);
                if (boost > bestBoost)
                {
                    bestBoost = boost;
                }

                residual.BoostSaturated |= boost >= options.MaxCoverageBoost - 1e-5f;
            }

            if (candidateHadRejectedBoundary)
            {
                rejectedCrossBoundaryBoosts++;
            }

            if (bestBoost > candidate.CoverageBoost + 1e-4f)
            {
                selected[i] = candidate with
                {
                    CoverageBoost = bestBoost,
                    ArtifactClass = ArtifactCoverage,
                    LayerId = LayerNone,
                    RepairAction = RepairActionBoost,
                };
            }
        }

        return rejectedCrossBoundaryBoosts;
    }

    private static float EstimateRequiredBoost(Candidate candidate, ProbeContribution contribution, float coverageResidual, float transmittanceBefore, float maxCoverageBoost)
    {
        float weightedT = MathF.Max(1e-6f, transmittanceBefore);
        float currentCenterAlpha = ApplyCoverageBoost(candidate.Opacity, candidate.CoverageBoost);
        float kernel = MathF.Max(1e-6f, contribution.Kernel);
        float targetKernelAlpha = Math.Clamp(contribution.AlphaKernel + coverageResidual / weightedT, 0f, 0.999f);
        float targetCenterAlpha = Math.Clamp(targetKernelAlpha / kernel, 0f, 0.999f);
        if (targetCenterAlpha <= currentCenterAlpha + 1e-6f)
        {
            return candidate.CoverageBoost;
        }

        float denominator = MathF.Log(Math.Clamp(1f - candidate.Opacity, 1e-6f, 0.999999f));
        if (MathF.Abs(denominator) <= 1e-6f)
        {
            return candidate.CoverageBoost;
        }

        float boost = MathF.Log(Math.Clamp(1f - targetCenterAlpha, 1e-6f, 0.999999f)) / denominator;
        return Math.Clamp(boost, candidate.CoverageBoost, MathF.Max(1f, maxCoverageBoost));
    }

    private static RepairCandidate ScoreGaussianPullbackCandidate(
        GaussianSplatStream stream,
        Candidate candidate,
        ProbeFrame sourceFrame,
        ProbeFrame selectedFrame,
        ProbeCell[] residualCells,
        SplatPackBounds bounds,
        float axisLengthCap,
        SplatPackRotationOrder rotationOrder,
        SplatPackBuildOptions options,
        ProbeView[] views,
        int cellsPerAxis)
    {
        float score = 0f;
        float coverageComponent = 0f;
        float edgeComponent = 0f;
        float textureComponent = 0f;
        float layerComponent = 0f;
        float stereoComponent = 0f;
        int bestLayer = LayerNone;
        foreach (ProbeContribution contribution in ProjectCandidateToProbeCells(stream, candidate, bounds, axisLengthCap, rotationOrder, options, views, cellsPerAxis, selectedFrame.ProbeCellSize))
        {
            ProbeCell residual = residualCells[contribution.CellIndex];
            if (residual.ArtifactClass == ArtifactNone)
            {
                continue;
            }

            ProbeCell source = sourceFrame.Cells[contribution.CellIndex];
            ProbeView view = views[contribution.ViewIndex];
            float tBefore = TransmittanceBefore(selectedFrame, contribution.CellIndex, contribution.Depth) * view.Weight;
            float mass = MathF.Max(0f, tBefore * contribution.AlphaKernel);
            if (mass <= ProbeNumericalEpsilon)
            {
                continue;
            }

            float colorDistance = MathF.Abs(Luma(contribution.Color) - source.Luma);
            float layerContribution = contribution.Depth <= source.MeanDepth || source.MeanDepth <= 0f ? mass : -mass;
            float fCoverage = mass / Normalizer((float)source.AlphaMass);
            float fEdge = mass * (source.EdgeMagnitude + ProbeNumericalEpsilon) / Normalizer(source.EdgeMagnitude);
            float fTexture = mass * MathF.Max(colorDistance, 0.05f) / Normalizer(source.TextureEnergy + 0.05f);
            float fLayer = MathF.Abs(layerContribution) / Normalizer((float)(source.NearLayerMass + source.FarLayerMass) + source.DepthVariance);
            float fStereo = view.Eye == SplatPackProbeEye.Mono ? 0f : mass / Normalizer((float)source.AlphaMass + source.EdgeMagnitude);

            coverageComponent += WeightedImprovement(residual.CoverageResidualNormalized, fCoverage, options.PullbackWeights.Coverage);
            edgeComponent += WeightedImprovement(residual.EdgeResidualNormalized, fEdge, options.PullbackWeights.Edge);
            textureComponent += WeightedImprovement(residual.TextureResidualNormalized, fTexture, options.PullbackWeights.Texture);
            layerComponent += WeightedImprovement(residual.LayerResidualNormalized, fLayer, options.PullbackWeights.Layer);
            stereoComponent += WeightedImprovement(residual.StereoResidualNormalized, fStereo, options.PullbackWeights.Stereo);
            bestLayer = contribution.Depth <= source.MeanDepth || source.MeanDepth <= 0f ? LayerNear : LayerFar;
        }

        score = coverageComponent + edgeComponent + textureComponent + layerComponent + stereoComponent;
        score -= candidate.MaxAxisLength * 0.0005f;
        int artifact = ArtifactForLargestComponent(coverageComponent, edgeComponent, textureComponent, layerComponent, stereoComponent);
        return new RepairCandidate(candidate, score, artifact, bestLayer, ActionForArtifact(artifact, candidate));
    }

    private static int ArtifactForLargestComponent(float coverage, float edge, float texture, float layer, float stereo)
    {
        float best = coverage;
        int artifact = ArtifactCoverage;
        if (edge > best)
        {
            best = edge;
            artifact = ArtifactEdge;
        }

        if (texture > best)
        {
            best = texture;
            artifact = ArtifactTexture;
        }

        if (layer > best)
        {
            best = layer;
            artifact = ArtifactLayer;
        }

        if (stereo > best)
        {
            artifact = ArtifactStereo;
        }

        return best > ProbeNumericalEpsilon ? artifact : ArtifactNone;
    }

    private static float WeightedImprovement(float residual, float contribution, float weight)
    {
        float f = MathF.Max(0f, contribution);
        float r = MathF.Max(0f, residual);
        return MathF.Max(0f, weight) * (2f * r * f - f * f);
    }

    private static float TransmittanceBefore(ProbeFrame frame, int cellIndex, float depth)
    {
        if (cellIndex < 0 || cellIndex >= frame.SamplesByCell.Length)
        {
            return 1f;
        }

        double transmittance = 1.0;
        foreach (ProbeSample sample in frame.SamplesByCell[cellIndex])
        {
            if (sample.Depth >= depth)
            {
                break;
            }

            transmittance *= 1.0 - Math.Clamp(sample.AlphaKernel, 0f, 0.999f);
            if (transmittance <= 1e-6)
            {
                return 0f;
            }
        }

        return (float)transmittance;
    }

    private static int CompareRepairCandidates(RepairCandidate a, RepairCandidate b)
    {
        int byScore = b.Score.CompareTo(a.Score);
        return byScore != 0 ? byScore : a.Candidate.SourceIndex.CompareTo(b.Candidate.SourceIndex);
    }

    private static int ActionForArtifact(int artifactClass, Candidate candidate)
    {
        return artifactClass switch
        {
            ArtifactTexture => candidate.AxisRatio > 3f ? RepairActionShPreserve : RepairActionQuantRelax,
            ArtifactEdge => RepairActionLodUpgrade,
            ArtifactLayer => RepairActionReinsert,
            ArtifactStereo => RepairActionReinsert,
            ArtifactCoverage => RepairActionReinsert,
            _ => RepairActionNone,
        };
    }

    private static float ResidualMagnitudeForArtifact(ProbeCell residual, int artifactClass)
    {
        return artifactClass switch
        {
            ArtifactLayer => residual.LayerResidualNormalized,
            ArtifactStereo => residual.StereoResidualNormalized,
            ArtifactEdge => residual.EdgeResidualNormalized,
            ArtifactTexture => residual.TextureResidualNormalized,
            ArtifactCoverage => residual.CoverageResidualNormalized,
            _ => 0f,
        };
    }

    private static ArtifactRepairSummary BuildArtifactRepairSummary(
        SplatPackBuildOptions options,
        ProbeView[] views,
        int initialBudget,
        int finalBudget,
        IReadOnlyList<Candidate> selected,
        ProbeCell[] initialResidual,
        ProbeCell[] finalResidual,
        float residualBefore,
        float residualAfter,
        float stereoBefore,
        float stereoAfter,
        int rejectedRepairs,
        int layerCrossBoostRejected)
    {
        int residualCount = finalResidual.Count(cell => cell.ArtifactClass != ArtifactNone);
        Vector4[] cellMetadata = BuildCellMetadata(initialResidual, finalResidual);

        float Mean(Func<ProbeCell, float> selector)
        {
            return residualCount > 0 ? finalResidual.Where(cell => cell.ArtifactClass != ArtifactNone).Average(selector) : 0f;
        }

        return new ArtifactRepairSummary
        {
            Mode = options.ArtifactRepairMode.ToString(),
            ProbeResolution = Math.Clamp(options.ProbeResolution, 16, 1024),
            ProbeViewCount = views.Length,
            InitialBudgetSplats = initialBudget,
            FinalBudgetSplats = finalBudget,
            ResidualCells = residualCount,
            CoverageResidualCells = finalResidual.Count(cell => cell.ArtifactClass == ArtifactCoverage),
            EdgeResidualCells = finalResidual.Count(cell => cell.ArtifactClass == ArtifactEdge),
            TextureResidualCells = finalResidual.Count(cell => cell.ArtifactClass == ArtifactTexture),
            LayerResidualCells = finalResidual.Count(cell => cell.ArtifactClass == ArtifactLayer),
            StereoResidualCells = finalResidual.Count(cell => cell.ArtifactClass == ArtifactStereo),
            ReinsertedCoverageSplats = selected.Count(candidate => candidate.ArtifactClass == ArtifactCoverage && candidate.RepairAction == RepairActionReinsert),
            ReinsertedEdgeSplats = selected.Count(candidate => candidate.ArtifactClass == ArtifactEdge && candidate.RepairAction != RepairActionBoost),
            ReinsertedTextureSplats = selected.Count(candidate => candidate.ArtifactClass == ArtifactTexture && candidate.RepairAction != RepairActionBoost),
            ReinsertedLayerSplats = selected.Count(candidate => candidate.ArtifactClass == ArtifactLayer && candidate.RepairAction != RepairActionBoost),
            ReinsertedStereoSplats = selected.Count(candidate => candidate.ArtifactClass == ArtifactStereo && candidate.RepairAction != RepairActionBoost),
            BoostedSplats = selected.Count(candidate => candidate.RepairAction == RepairActionBoost),
            BoostSaturatedCells = finalResidual.Count(cell => cell.BoostSaturated),
            LodUpgradedSplats = selected.Count(candidate => candidate.RepairAction == RepairActionLodUpgrade),
            ShPreservedSplats = selected.Count(candidate => candidate.RepairAction == RepairActionShPreserve),
            QuantizationRelaxedSplats = selected.Count(candidate => candidate.RepairAction == RepairActionQuantRelax),
            CoverageResidualMean = Mean(cell => cell.CoverageResidualNormalized),
            EdgeResidualMean = Mean(cell => cell.EdgeResidualNormalized),
            TextureResidualMean = Mean(cell => cell.TextureResidualNormalized),
            LayerResidualMean = Mean(cell => cell.LayerResidualNormalized),
            StereoResidualMean = Mean(cell => cell.StereoResidualNormalized),
            MaxAppliedBoost = selected.Count > 0 ? selected.Max(candidate => MathF.Max(1f, candidate.CoverageBoost)) : 1f,
            ResidualBefore = residualBefore,
            ResidualAfter = residualAfter,
            ResidualReductionRatio = residualBefore > ProbeNumericalEpsilon ? Math.Clamp(1f - residualAfter / residualBefore, -1f, 1f) : 0f,
            RejectedRepairs = rejectedRepairs,
            StereoMismatchBefore = stereoBefore,
            StereoMismatchAfter = stereoAfter,
            LayerCrossBoostRejected = layerCrossBoostRejected,
            CellMetadata = cellMetadata,
            CellMetadataStride = SplatPackWriter.CellArtifactMetadataV2Stride,
        };
    }

    private static Vector4[] BuildCellMetadata(ProbeCell[] initialResidual, ProbeCell[] finalResidual)
    {
        var values = new List<Vector4>(Math.Min(4096, finalResidual.Length) * 3);
        int limit = Math.Min(initialResidual.Length, finalResidual.Length);
        for (int i = 0; i < limit && values.Count < 4096 * 3; i++)
        {
            ProbeCell before = initialResidual[i];
            ProbeCell after = finalResidual[i];
            if (before.ArtifactClass == ArtifactNone && after.ArtifactClass == ArtifactNone)
            {
                continue;
            }

            float beforeMagnitude = ResidualMagnitudeForArtifact(before, before.ArtifactClass);
            float afterMagnitude = ResidualMagnitudeForArtifact(after, after.ArtifactClass);
            values.Add(new Vector4(after.ArtifactClass, after.CoverageResidualNormalized, after.EdgeResidualNormalized, after.TextureResidualNormalized));
            values.Add(new Vector4(after.LayerResidualNormalized, after.StereoResidualNormalized, beforeMagnitude, afterMagnitude));
            values.Add(new Vector4(0f, after.LayerId, after.BoostSaturated ? 1f : 0f, beforeMagnitude > ProbeNumericalEpsilon ? 1f - afterMagnitude / beforeMagnitude : 0f));
        }

        return values.ToArray();
    }

    private static float WeightedResidualEnergy(ProbeCell[] residualCells, SplatPackPullbackWeights weights)
    {
        double sum = 0.0;
        foreach (ProbeCell cell in residualCells)
        {
            sum += weights.Coverage * cell.CoverageResidualNormalized * cell.CoverageResidualNormalized;
            sum += weights.Edge * cell.EdgeResidualNormalized * cell.EdgeResidualNormalized;
            sum += weights.Texture * cell.TextureResidualNormalized * cell.TextureResidualNormalized;
            sum += weights.Layer * cell.LayerResidualNormalized * cell.LayerResidualNormalized;
            sum += weights.Stereo * cell.StereoResidualNormalized * cell.StereoResidualNormalized;
        }

        return (float)sum;
    }

    private static float StereoMismatch(ProbeCell[] residualCells)
    {
        double sum = 0.0;
        foreach (ProbeCell cell in residualCells)
        {
            sum += cell.StereoResidualNormalized;
        }

        return (float)sum;
    }

    private static void GetCandidateAxes(
        GaussianSplatStream stream,
        int index,
        SplatPackRotationOrder rotationOrder,
        float axisLengthCap,
        float maxAxisRatio,
        out Vector3 axis0,
        out Vector3 axis1,
        out Vector3 axis2)
    {
        GetCandidateAxes(
            stream,
            index,
            rotationOrder,
            axisLengthCap,
            maxAxisRatio,
            ArtifactNone,
            RepairActionNone,
            out axis0,
            out axis1,
            out axis2);
    }

    private static void GetCandidateAxes(
        GaussianSplatStream stream,
        int index,
        SplatPackRotationOrder rotationOrder,
        float axisLengthCap,
        float maxAxisRatio,
        int artifactClass,
        int repairAction,
        out Vector3 axis0,
        out Vector3 axis1,
        out Vector3 axis2)
    {
        Vector3 scale = new(
            SafeExp(stream.ScalesLog[index * 3 + 0]),
            SafeExp(stream.ScalesLog[index * 3 + 1]),
            SafeExp(stream.ScalesLog[index * 3 + 2]));
        if (axisLengthCap > 0f)
        {
            scale = Vector3.Min(scale, new Vector3(axisLengthCap));
        }

        scale = ClampAxisRatio(scale, ResolveRepairMaxAxisRatio(maxAxisRatio, artifactClass, repairAction), out _);
        Quaternion rotation = DecodeRotation(stream, index, rotationOrder);
        axis0 = Vector3.Transform(Vector3.UnitX, rotation) * scale.X;
        axis1 = Vector3.Transform(Vector3.UnitY, rotation) * scale.Y;
        axis2 = Vector3.Transform(Vector3.UnitZ, rotation) * scale.Z;
    }

    private static Vector3 ToProbeView(Vector3 value, ProbeView view)
    {
        return new Vector3(Vector3.Dot(value, view.Right), Vector3.Dot(value, view.Up), Vector3.Dot(value, view.Forward));
    }

    private static void SolveEigen2(float c00, float c01, float c11, out float lambda0, out float lambda1, out Vector2 dir0)
    {
        float determinant = c00 * c11 - c01 * c01;
        float traceOver2 = 0.5f * (c00 + c11);
        float term2 = MathF.Sqrt(MathF.Max(1e-10f, traceOver2 * traceOver2 - determinant));
        lambda0 = traceOver2 + term2;
        lambda1 = traceOver2 - term2;
        if (MathF.Abs(c01) > 1e-8f)
        {
            dir0 = Vector2.Normalize(new Vector2(c01, lambda0 - c00));
        }
        else
        {
            dir0 = c00 >= c11 ? Vector2.UnitX : Vector2.UnitY;
        }
    }

    private static float FilterVarianceForProbe(SplatPackBuildOptions options)
    {
        float cellVariance = MathF.Max(0.25f, options.ProbeCellSize * options.ProbeCellSize * 0.0625f);
        return options.FilterMode switch
        {
            SplatPackFilterMode.Mip2D => cellVariance,
            SplatPackFilterMode.AnalyticPixel => cellVariance * 1.5f,
            _ => 0f,
        };
    }

    private static float NormalizeResidual(float residual, float denominator)
    {
        return residual <= ProbeNumericalEpsilon ? 0f : residual / Normalizer(denominator);
    }

    private static float Normalizer(float value)
    {
        return MathF.Max(1e-5f, MathF.Abs(value));
    }

    private static float Dot3(float ax, float ay, float az, float bx, float by, float bz)
    {
        return ax * bx + ay * by + az * bz;
    }

    private static float Luma(Vector3 rgb)
    {
        return 0.2126f * rgb.X + 0.7152f * rgb.Y + 0.0722f * rgb.Z;
    }

    internal readonly record struct SplatPackProbeDebugStats(
        int ViewCount,
        int CellsPerAxis,
        int NonEmptyCells,
        int MaxSamplesPerCell,
        float MinTransmittance,
        float MaxAlphaMass);

    internal static SplatPackProbeDebugStats DebugRenderProbeStats(GaussianSplatStream stream, SplatPackBuildOptions options)
    {
        options = options.NormalizeForBuild();
        Candidate[] candidates = SelectCandidates(
            stream,
            options,
            out _,
            out _,
            out _,
            out _,
            out _);
        if (candidates.Length == 0)
        {
            return new SplatPackProbeDebugStats(0, 0, 0, 0, 1f, 0f);
        }

        SplatPackRotationOrder rotationOrder = ResolveRotationOrder(stream, options.RotationOrder);
        float axisLengthCap = ResolveAxisLengthCap(candidates, options);
        SplatPackBounds bounds = ComputeBounds(stream, candidates);
        int probeResolution = Math.Clamp(options.ProbeResolution, 16, 1024);
        int probeCellSize = Math.Clamp(options.ProbeCellSize, 1, 32);
        int cellsPerAxis = Math.Clamp(probeResolution / probeCellSize, 4, 256);
        ProbeView[] views = ResolveProbeViews(bounds, options, probeResolution);
        ProbeFrame frame = RenderProbeFrame(stream, candidates, bounds, axisLengthCap, rotationOrder, options, views, cellsPerAxis, probeResolution, probeCellSize);
        int nonEmptyCells = frame.Cells.Count(cell => cell.AlphaMass > ProbeNumericalEpsilon);
        int maxSamples = frame.SamplesByCell.Length == 0 ? 0 : frame.SamplesByCell.Max(samples => samples.Count);
        float minTransmittance = frame.Cells.Length == 0 ? 1f : (float)frame.Cells.Min(cell => cell.Transmittance);
        float maxAlphaMass = frame.Cells.Length == 0 ? 0f : (float)frame.Cells.Max(cell => cell.AlphaMass);
        return new SplatPackProbeDebugStats(views.Length, cellsPerAxis, nonEmptyCells, maxSamples, minTransmittance, maxAlphaMass);
    }

    private static Vector3 ColorForCandidate(GaussianSplatStream stream, int index)
    {
        return stream.ShDc != null
            ? Vector3.Clamp(
                new Vector3(
                    0.5f + ShC0 * stream.ShDc[index * 3 + 0],
                    0.5f + ShC0 * stream.ShDc[index * 3 + 1],
                    0.5f + ShC0 * stream.ShDc[index * 3 + 2]),
                Vector3.Zero,
                Vector3.One)
            : Vector3.One;
    }

    private static int CellIndex(int view, int x, int y, int cellsPerAxis)
    {
        return view * cellsPerAxis * cellsPerAxis + y * cellsPerAxis + x;
    }
}
