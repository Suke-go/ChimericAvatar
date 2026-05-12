using System.Numerics;

namespace SplatPack.Compiler;

public static class SplatPackBuilder
{
    private const float ShC0 = 0.28209479177387814f;
    private const float MinimumScale = 1e-8f;

    private readonly record struct Candidate(int SourceIndex, float Opacity, float MaxAxisLength);

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
                Report = BuildReport(emptyPackage, options, SplatPackRotationOrder.WXYZ, 0f, 0, 0, 0, Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>()),
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
            out float[] rawMaxAxisLengths);

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
                Report = BuildReport(emptyPackage, options, rotationOrder, 0f, prunedInvalid, prunedLowOpacity, 0, rawOpacities, rawMaxAxisLengths, Array.Empty<float>(), Array.Empty<float>()),
            };
        }

        float axisLengthCap = ResolveAxisLengthCap(candidates, options);
        SplatPackBounds bounds = ComputeBounds(stream, candidates);
        int gridResolution = Math.Max(1, (int)Math.Ceiling(Math.Pow(candidates.Length / (double)targetChunkSize, 1.0 / 3.0)));

        var order = new int[candidates.Length];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            long ka = GridKey(stream, bounds, gridResolution, candidates[a].SourceIndex);
            long kb = GridKey(stream, bounds, gridResolution, candidates[b].SourceIndex);
            int byKey = ka.CompareTo(kb);
            return byKey != 0 ? byKey : a.CompareTo(b);
        });

        var splats = new SplatPackSplat[candidates.Length];
        var chunks = new List<SplatPackChunk>();
        var emittedOpacities = new float[candidates.Length];
        var emittedMaxAxisLengths = new float[candidates.Length];
        int axisClampedSplats = 0;

        int cursor = 0;
        while (cursor < order.Length)
        {
            long key = GridKey(stream, bounds, gridResolution, candidates[order[cursor]].SourceIndex);
            int start = cursor;
            while (cursor < order.Length
                   && GridKey(stream, bounds, gridResolution, candidates[order[cursor]].SourceIndex) == key
                   && cursor - start < targetChunkSize)
            {
                Candidate candidate = candidates[order[cursor]];
                splats[cursor] = ConvertSplat(stream, candidate.SourceIndex, rotationOrder, axisLengthCap, out bool axisClamped, out float emittedMaxAxisLength);
                emittedOpacities[cursor] = splats[cursor].Color.W;
                emittedMaxAxisLengths[cursor] = emittedMaxAxisLength;
                if (axisClamped)
                {
                    axisClampedSplats++;
                }

                cursor++;
            }

            chunks.Add(BuildChunk(splats, start, cursor - start));
        }

        var package = new SplatPackPackage
        {
            Bounds = bounds,
            Chunks = chunks.ToArray(),
            Splats = splats,
        };
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
                axisClampedSplats,
                rawOpacities,
                rawMaxAxisLengths,
                emittedOpacities,
                emittedMaxAxisLengths),
        };
    }

    private static Candidate[] SelectCandidates(
        GaussianSplatStream stream,
        SplatPackBuildOptions options,
        out int prunedInvalid,
        out int prunedLowOpacity,
        out float[] rawOpacities,
        out float[] rawMaxAxisLengths)
    {
        float opacityPruneThreshold = MathF.Max(0f, options.OpacityPruneThreshold);
        var candidates = new List<Candidate>(stream.Count);
        var opacityStats = new List<float>(stream.Count);
        var axisStats = new List<float>(stream.Count);
        prunedInvalid = 0;
        prunedLowOpacity = 0;

        for (int i = 0; i < stream.Count; i++)
        {
            if (!TryInspectRawSplat(stream, i, out float opacity, out float maxAxisLength))
            {
                prunedInvalid++;
                continue;
            }

            opacityStats.Add(opacity);
            axisStats.Add(maxAxisLength);
            if (opacity < opacityPruneThreshold)
            {
                prunedLowOpacity++;
                continue;
            }

            candidates.Add(new Candidate(i, opacity, maxAxisLength));
        }

        rawOpacities = opacityStats.ToArray();
        rawMaxAxisLengths = axisStats.ToArray();
        return candidates.ToArray();
    }

    private static bool TryInspectRawSplat(GaussianSplatStream stream, int index, out float opacity, out float maxAxisLength)
    {
        opacity = 0f;
        maxAxisLength = 0f;
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
        return IsFinite(opacity) && IsFinite(maxAxisLength);
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

    private static SplatPackChunk BuildChunk(SplatPackSplat[] splats, int offset, int count)
    {
        var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        for (int i = offset; i < offset + count; i++)
        {
            Vector3 p = new(splats[i].CenterWS.X, splats[i].CenterWS.Y, splats[i].CenterWS.Z);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return new SplatPackChunk(min, max, offset, count, 0);
    }

    private static SplatPackSplat ConvertSplat(
        GaussianSplatStream stream,
        int index,
        SplatPackRotationOrder rotationOrder,
        float axisLengthCap,
        out bool axisClamped,
        out float emittedMaxAxisLength)
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

        axisClamped = scale != unclampedScale;
        emittedMaxAxisLength = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));

        Quaternion rotation = DecodeRotation(stream, index, rotationOrder);

        Vector3 axis0 = Vector3.Transform(Vector3.UnitX, rotation) * scale.X;
        Vector3 axis1 = Vector3.Transform(Vector3.UnitY, rotation) * scale.Y;
        Vector3 axis2 = Vector3.Transform(Vector3.UnitZ, rotation) * scale.Z;

        Vector3 rgb = stream.ShDc != null
            ? new Vector3(
                0.5f + ShC0 * stream.ShDc[index * 3 + 0],
                0.5f + ShC0 * stream.ShDc[index * 3 + 1],
                0.5f + ShC0 * stream.ShDc[index * 3 + 2])
            : Vector3.One;

        rgb = Vector3.Clamp(rgb, Vector3.Zero, Vector3.One);
        float opacity = Sigmoid(stream.OpacitiesRaw[index]);

        return new SplatPackSplat(
            new Vector4(position, 1f),
            new Vector4(axis0, 0f),
            new Vector4(axis1, 0f),
            new Vector4(axis2, 0f),
            new Vector4(rgb, opacity),
            new Vector4(-1f, 0f, 0f, 0f));
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
        return x + (long)y * resolution + (long)z * resolution * resolution;
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
        int axisClampedSplats,
        float[] rawOpacities,
        float[] rawMaxAxisLengths,
        float[] emittedOpacities,
        float[] emittedMaxAxisLengths)
    {
        Vector3 extents = package.Bounds.Max - package.Bounds.Min;
        return new SplatPackBuildReport
        {
            TargetProfile = options.TargetProfile.ToString(),
            RotationOrder = rotationOrder.ToString(),
            Options = new SplatPackBuildReportOptions
            {
                ChunkSize = Math.Max(1, options.ChunkSize),
                OpacityPruneThreshold = MathF.Max(0f, options.OpacityPruneThreshold),
                AxisLengthCap = axisLengthCap,
                MaxAxisLengthPercentile = options.MaxAxisLengthPercentile,
                MaxAxisLengthMultiplier = options.MaxAxisLengthMultiplier,
            },
            Counts = new SplatPackBuildReportCounts
            {
                InputSplats = rawOpacities.Length + prunedInvalid,
                EmittedSplats = package.Splats.Length,
                PrunedInvalidSplats = prunedInvalid,
                PrunedLowOpacitySplats = prunedLowOpacity,
                AxisClampedSplats = axisClampedSplats,
            },
            Bounds = new SplatPackBuildReportBounds
            {
                Min = ToDto(package.Bounds.Min),
                Max = ToDto(package.Bounds.Max),
                Extents = ToDto(extents),
            },
            RawOpacity = BuildStats(rawOpacities),
            RawMaxAxisLength = BuildStats(rawMaxAxisLengths),
            EmittedOpacity = BuildStats(emittedOpacities),
            EmittedMaxAxisLength = BuildStats(emittedMaxAxisLengths),
            Chunks = BuildChunkStats(package.Chunks),
            Memory = new SplatPackMemoryStats
            {
                SplatStrideBytes = SplatPackWriter.SplatStride,
                ChunkStrideBytes = SplatPackWriter.ChunkStride,
                SplatBytes = (long)package.Splats.Length * SplatPackWriter.SplatStride,
                ChunkBytes = (long)package.Chunks.Length * SplatPackWriter.ChunkStride,
                TotalUncompressedBytes = (long)package.Splats.Length * SplatPackWriter.SplatStride + (long)package.Chunks.Length * SplatPackWriter.ChunkStride,
                RuntimeVertices = package.Splats.Length * 6,
            },
        };
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
}
