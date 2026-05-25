using System.Numerics;

namespace SplatPack.Compiler;

public static class SplatPackWriter
{
    public const int Version = 4;
    public const int ReferenceFloatVersion = 5;
    public const int ResearchSectionedVersion = 6;
    public const int FloatSplatStride = 36 * sizeof(float);
    public const int Sh3Vector4PerSplat = 12;
    public const int Sh3SectionStride = Sh3Vector4PerSplat * 4 * sizeof(float);
    public const int ResearchMetadataStride = 4 * sizeof(float);
    public const int ArtifactRepairMetadataStride = 4 * sizeof(float);
    public const int CellArtifactMetadataStride = 4 * sizeof(float);
    public const int CellArtifactMetadataV2Stride = 12 * sizeof(float);
    public const int SupportMetadataStride = 2 * sizeof(uint);
    public const int SphericalSupportFieldStride = 4 * sizeof(float);
    public const int SphericalSupportFieldV1Stride = 2 * SphericalSupportFieldStride;
    public const int SplatStride = 50;
    public const int ChunkStride = 6 * sizeof(float) + 3 * sizeof(int);
    private const int FeaturePackedQuantizedAttributes = 1;
    private const int FeatureRenderHints = 2;
    private const int FeatureReferenceFloatAttributes = 4;
    private const int FeatureResearchSections = 8;
    public const int SectionTagSh3 = 0x20334853;
    public const int SectionTagResearchMetadata = 0x54454D51;
    public const int SectionTagArtifactRepairMetadata = 0x52505241;
    public const int SectionTagCellArtifactMetadata = 0x4C4C4543;
    public const int SectionTagSupportMetadata = 0x50555353;
    public const int SectionTagSphericalSupportField = 0x46505353;

    public enum WriteFormat
    {
        PackedRenderHints,
        ReferenceFloat,
        ResearchSectioned,
    }

    public static WriteFormat FormatForTarget(SplatPackTargetProfile targetProfile)
    {
        return targetProfile == SplatPackTargetProfile.Reference
            ? WriteFormat.ReferenceFloat
            : WriteFormat.PackedRenderHints;
    }

    public static WriteFormat FormatForOptions(SplatPackBuildOptions options)
    {
        return options.OutputFormat switch
        {
            SplatPackOutputFormat.PackedV4 => WriteFormat.PackedRenderHints,
            SplatPackOutputFormat.ReferenceFloatV5 => WriteFormat.ReferenceFloat,
            SplatPackOutputFormat.ResearchSectionedV6 => WriteFormat.ResearchSectioned,
            _ => options.TargetProfile == SplatPackTargetProfile.Reference
                ? WriteFormat.ReferenceFloat
                : WriteFormat.ResearchSectioned,
        };
    }

    public static int SplatStrideForTarget(SplatPackTargetProfile targetProfile)
    {
        return SplatStrideForFormat(FormatForTarget(targetProfile));
    }

    public static int SplatStrideForFormat(WriteFormat format)
    {
        return format == WriteFormat.PackedRenderHints ? SplatStride : FloatSplatStride;
    }

    public static void Write(string path, SplatPackPackage package)
    {
        Write(path, package, WriteFormat.PackedRenderHints);
    }

    public static void Write(string path, SplatPackPackage package, WriteFormat format)
    {
        using FileStream stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        int version = format switch
        {
            WriteFormat.ReferenceFloat => ReferenceFloatVersion,
            WriteFormat.ResearchSectioned => ResearchSectionedVersion,
            _ => Version,
        };
        int splatStride = SplatStrideForFormat(format);
        int featureFlags = format switch
        {
            WriteFormat.ReferenceFloat => FeatureReferenceFloatAttributes | FeatureRenderHints,
            WriteFormat.ResearchSectioned => FeatureReferenceFloatAttributes | FeatureRenderHints | FeatureResearchSections,
            _ => FeaturePackedQuantizedAttributes | FeatureRenderHints,
        };

        writer.Write((byte)'S');
        writer.Write((byte)'P');
        writer.Write((byte)'K');
        writer.Write((byte)'1');
        writer.Write(version);
        writer.Write(package.Splats.Length);
        writer.Write(package.Chunks.Length);
        writer.Write(splatStride);
        writer.Write(ChunkStride);
        WriteVector3(writer, package.Bounds.Min);
        WriteVector3(writer, package.Bounds.Max);
        float axisRange = ResolveAxisRange(package);
        writer.Write(axisRange);
        writer.Write(featureFlags);

        foreach (SplatPackChunk chunk in package.Chunks)
        {
            WriteVector3(writer, chunk.BoundsMin);
            WriteVector3(writer, chunk.BoundsMax);
            writer.Write(chunk.SplatOffset);
            writer.Write(chunk.SplatCount);
            writer.Write(chunk.LodLevel);
        }

        foreach (SplatPackSplat splat in package.Splats)
        {
            if (format == WriteFormat.ReferenceFloat || format == WriteFormat.ResearchSectioned)
            {
                WriteFloatSplat(writer, splat);
            }
            else
            {
                WritePackedSplat(writer, splat, package.Bounds, axisRange);
            }
        }

        if (format == WriteFormat.ResearchSectioned)
        {
            WriteResearchSections(writer, package);
        }
    }

    private static void WriteResearchSections(BinaryWriter writer, SplatPackPackage package)
    {
        bool hasSh3 = package.Sh3Coefficients.Length == package.Splats.Length * Sh3Vector4PerSplat;
        bool hasResearchMetadata = package.ResearchMetadata.Length == package.Splats.Length;
        bool hasArtifactRepairMetadata = package.ArtifactRepairMetadata.Length == package.Splats.Length;
        bool hasSupportMetadata = package.SupportMetadata.Length == package.Splats.Length;
        int cellArtifactStride = ResolveCellArtifactStride(package);
        int cellArtifactVector4PerRecord = Math.Max(1, cellArtifactStride / (4 * sizeof(float)));
        bool hasCellArtifactMetadata = package.CellArtifactMetadata.Length >= cellArtifactVector4PerRecord
                                       && package.CellArtifactMetadata.Length % cellArtifactVector4PerRecord == 0;
        int sphericalSupportStride = ResolveSphericalSupportFieldStride(package);
        int sphericalSupportVector4PerRecord = Math.Max(1, sphericalSupportStride / (4 * sizeof(float)));
        bool hasSphericalSupportField = package.SphericalSupportField.Length >= sphericalSupportVector4PerRecord
                                        && package.SphericalSupportField.Length % sphericalSupportVector4PerRecord == 0;
        int sectionCount = (hasSh3 ? 1 : 0)
                           + (hasResearchMetadata ? 1 : 0)
                           + (hasArtifactRepairMetadata ? 1 : 0)
                           + (hasCellArtifactMetadata ? 1 : 0)
                           + (hasSupportMetadata ? 1 : 0)
                           + (hasSphericalSupportField ? 1 : 0);
        writer.Write(sectionCount);
        if (hasSh3)
        {
            writer.Write(SectionTagSh3);
            writer.Write(package.Splats.Length);
            writer.Write(Sh3SectionStride);
            foreach (Vector4 value in package.Sh3Coefficients)
            {
                WriteVector4(writer, value);
            }
        }

        if (hasResearchMetadata)
        {
            writer.Write(SectionTagResearchMetadata);
            writer.Write(package.Splats.Length);
            writer.Write(ResearchMetadataStride);
            foreach (Vector4 value in package.ResearchMetadata)
            {
                WriteVector4(writer, value);
            }
        }

        if (hasArtifactRepairMetadata)
        {
            writer.Write(SectionTagArtifactRepairMetadata);
            writer.Write(package.Splats.Length);
            writer.Write(ArtifactRepairMetadataStride);
            foreach (Vector4 value in package.ArtifactRepairMetadata)
            {
                WriteVector4(writer, value);
            }
        }

        if (hasCellArtifactMetadata)
        {
            writer.Write(SectionTagCellArtifactMetadata);
            writer.Write(package.CellArtifactMetadata.Length / cellArtifactVector4PerRecord);
            writer.Write(cellArtifactStride);
            foreach (Vector4 value in package.CellArtifactMetadata)
            {
                WriteVector4(writer, value);
            }
        }

        if (hasSupportMetadata)
        {
            writer.Write(SectionTagSupportMetadata);
            writer.Write(package.Splats.Length);
            writer.Write(SupportMetadataStride);
            foreach (SplatPackSupportMetadata value in package.SupportMetadata)
            {
                writer.Write(value.Packed0);
                writer.Write(value.Packed1);
            }
        }

        if (hasSphericalSupportField)
        {
            writer.Write(SectionTagSphericalSupportField);
            writer.Write(package.SphericalSupportField.Length / sphericalSupportVector4PerRecord);
            writer.Write(sphericalSupportStride);
            foreach (Vector4 value in package.SphericalSupportField)
            {
                WriteVector4(writer, value);
            }
        }
    }

    private static int ResolveCellArtifactStride(SplatPackPackage package)
    {
        int stride = package.CellArtifactMetadataStride;
        return stride > 0 && stride % (4 * sizeof(float)) == 0
            ? stride
            : CellArtifactMetadataStride;
    }

    private static int ResolveSphericalSupportFieldStride(SplatPackPackage package)
    {
        int stride = package.SphericalSupportFieldStride;
        return stride > 0 && stride % (4 * sizeof(float)) == 0
            ? stride
            : SphericalSupportFieldStride;
    }

    private static float ResolveAxisRange(SplatPackPackage package)
    {
        float axisRange = 1e-6f;
        foreach (SplatPackSplat splat in package.Splats)
        {
            axisRange = MathF.Max(axisRange, MaxAbs(splat.Axis0WS));
            axisRange = MathF.Max(axisRange, MaxAbs(splat.Axis1WS));
            axisRange = MathF.Max(axisRange, MaxAbs(splat.Axis2WS));
        }

        return axisRange;
    }

    private static float MaxAbs(Vector4 value)
    {
        return MathF.Max(MathF.Abs(value.X), MathF.Max(MathF.Abs(value.Y), MathF.Abs(value.Z)));
    }

    private static void WritePackedSplat(BinaryWriter writer, SplatPackSplat splat, SplatPackBounds bounds, float axisRange)
    {
        writer.Write(QuantizeUnsigned(splat.CenterWS.X, bounds.Min.X, bounds.Max.X));
        writer.Write(QuantizeUnsigned(splat.CenterWS.Y, bounds.Min.Y, bounds.Max.Y));
        writer.Write(QuantizeUnsigned(splat.CenterWS.Z, bounds.Min.Z, bounds.Max.Z));
        WritePackedAxis(writer, splat.Axis0WS, axisRange);
        WritePackedAxis(writer, splat.Axis1WS, axisRange);
        WritePackedAxis(writer, splat.Axis2WS, axisRange);
        writer.Write(QuantizeByte(splat.Color.X));
        writer.Write(QuantizeByte(splat.Color.Y));
        writer.Write(QuantizeByte(splat.Color.Z));
        writer.Write(QuantizeByte(splat.Color.W));
        writer.Write(QuantizeUnsigned(splat.Meta.Z, 0f, 1f));
        writer.Write((byte)Math.Clamp((int)MathF.Round(splat.Meta.W), 0, 2));
        writer.Write((byte)Math.Clamp((int)MathF.Round(splat.Meta.Y), 0, 255));
        WritePackedSh1(writer, splat.Sh1R);
        WritePackedSh1(writer, splat.Sh1G);
        WritePackedSh1(writer, splat.Sh1B);
    }

    private static void WriteFloatSplat(BinaryWriter writer, SplatPackSplat splat)
    {
        WriteVector4(writer, splat.CenterWS);
        WriteVector4(writer, splat.Axis0WS);
        WriteVector4(writer, splat.Axis1WS);
        WriteVector4(writer, splat.Axis2WS);
        WriteVector4(writer, splat.Color);
        WriteVector4(writer, splat.Meta);
        WriteVector4(writer, splat.Sh1R);
        WriteVector4(writer, splat.Sh1G);
        WriteVector4(writer, splat.Sh1B);
    }

    private static void WritePackedAxis(BinaryWriter writer, Vector4 axis, float axisRange)
    {
        writer.Write(QuantizeSigned(axis.X, axisRange));
        writer.Write(QuantizeSigned(axis.Y, axisRange));
        writer.Write(QuantizeSigned(axis.Z, axisRange));
    }

    private static void WritePackedSh1(BinaryWriter writer, Vector4 coeffs)
    {
        writer.Write(QuantizeSigned(coeffs.X, 1f));
        writer.Write(QuantizeSigned(coeffs.Y, 1f));
        writer.Write(QuantizeSigned(coeffs.Z, 1f));
    }

    private static ushort QuantizeUnsigned(float value, float min, float max)
    {
        if (max <= min)
        {
            return 0;
        }

        float t = Math.Clamp((value - min) / (max - min), 0f, 1f);
        return (ushort)Math.Clamp((int)MathF.Round(t * ushort.MaxValue), 0, ushort.MaxValue);
    }

    private static short QuantizeSigned(float value, float axisRange)
    {
        float t = axisRange > 0f ? Math.Clamp(value / axisRange, -1f, 1f) : 0f;
        return (short)Math.Clamp((int)MathF.Round(t * short.MaxValue), short.MinValue + 1, short.MaxValue);
    }

    private static byte QuantizeByte(float value)
    {
        return (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * byte.MaxValue), 0, byte.MaxValue);
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static void WriteVector4(BinaryWriter writer, Vector4 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
        writer.Write(value.W);
    }

}
