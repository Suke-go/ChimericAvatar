using System.Numerics;

namespace SplatPack.Compiler;

public static class SplatPackWriter
{
    public const int Version = 1;
    public const int SplatStride = 24 * sizeof(float);
    public const int ChunkStride = 8 * sizeof(float) + 3 * sizeof(int);

    public static void Write(string path, SplatPackPackage package)
    {
        using FileStream stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write((byte)'S');
        writer.Write((byte)'P');
        writer.Write((byte)'K');
        writer.Write((byte)'1');
        writer.Write(Version);
        writer.Write(package.Splats.Length);
        writer.Write(package.Chunks.Length);
        writer.Write(SplatStride);
        writer.Write(ChunkStride);
        WriteVector3(writer, package.Bounds.Min);
        WriteVector3(writer, package.Bounds.Max);

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
            WriteVector4(writer, splat.CenterWS);
            WriteVector4(writer, splat.Axis0WS);
            WriteVector4(writer, splat.Axis1WS);
            WriteVector4(writer, splat.Axis2WS);
            WriteVector4(writer, splat.Color);
            WriteVector4(writer, splat.Meta);
        }
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
