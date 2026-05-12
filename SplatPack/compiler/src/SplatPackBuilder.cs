using System.Numerics;

namespace SplatPack.Compiler;

public static class SplatPackBuilder
{
    private const float ShC0 = 0.28209479177387814f;

    public static SplatPackPackage Build(GaussianSplatStream stream, int targetChunkSize)
    {
        return Build(stream, new SplatPackBuildOptions { ChunkSize = targetChunkSize });
    }

    public static SplatPackPackage Build(GaussianSplatStream stream, SplatPackBuildOptions options)
    {
        if (stream.Count <= 0)
        {
            return new SplatPackPackage
            {
                Bounds = new SplatPackBounds(Vector3.Zero, Vector3.Zero),
                Chunks = Array.Empty<SplatPackChunk>(),
                Splats = Array.Empty<SplatPackSplat>(),
            };
        }

        int targetChunkSize = Math.Max(1, options.ChunkSize);
        SplatPackRotationOrder rotationOrder = ResolveRotationOrder(stream, options.RotationOrder);
        SplatPackBounds bounds = ComputeBounds(stream);
        int gridResolution = Math.Max(1, (int)Math.Ceiling(Math.Pow(stream.Count / (double)targetChunkSize, 1.0 / 3.0)));

        var order = new int[stream.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            long ka = GridKey(stream, bounds, gridResolution, a);
            long kb = GridKey(stream, bounds, gridResolution, b);
            int byKey = ka.CompareTo(kb);
            return byKey != 0 ? byKey : a.CompareTo(b);
        });

        var splats = new SplatPackSplat[stream.Count];
        var chunks = new List<SplatPackChunk>();

        int cursor = 0;
        while (cursor < order.Length)
        {
            long key = GridKey(stream, bounds, gridResolution, order[cursor]);
            int start = cursor;
            while (cursor < order.Length
                   && GridKey(stream, bounds, gridResolution, order[cursor]) == key
                   && cursor - start < targetChunkSize)
            {
                splats[cursor] = ConvertSplat(stream, order[cursor], rotationOrder);
                cursor++;
            }

            chunks.Add(BuildChunk(splats, start, cursor - start));
        }

        return new SplatPackPackage
        {
            Bounds = bounds,
            Chunks = chunks.ToArray(),
            Splats = splats,
        };
    }

    private static SplatPackBounds ComputeBounds(GaussianSplatStream stream)
    {
        var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        for (int i = 0; i < stream.Count; i++)
        {
            Vector3 p = Position(stream, i);
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

    private static SplatPackSplat ConvertSplat(GaussianSplatStream stream, int index, SplatPackRotationOrder rotationOrder)
    {
        Vector3 position = Position(stream, index);
        Vector3 scale = new(
            MathF.Exp(stream.ScalesLog[index * 3 + 0]),
            MathF.Exp(stream.ScalesLog[index * 3 + 1]),
            MathF.Exp(stream.ScalesLog[index * 3 + 2]));

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
}
