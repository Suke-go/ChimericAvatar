namespace SplatPack.Compiler;

public sealed class GaussianSplatStream
{
    public int Count;
    public required float[] PositionsXYZ;
    public required float[] OpacitiesRaw;
    public required float[] ScalesLog;
    public required float[] RotationsRaw;
    public float[]? ShDc;
    public float[]? ShRest;
    public int ShRestStride;

    public static GaussianSplatStream Read(byte[] plyBytes, PlyHeader? header = null)
    {
        header ??= PlyParser.ParseHeader(plyBytes);
        if (header.Format != PlyFormat.BinaryLittleEndian)
        {
            throw new InvalidDataException($"Only binary_little_endian 3DGS PLY is supported in the first compiler pass. Got {header.Format}.");
        }

        PlyElement vertex = header.FindElement("vertex") ?? throw new InvalidDataException("PLY missing vertex element.");
        int stride = PlyParser.FixedStride(vertex);

        var floatOffsets = new Dictionary<string, int>(vertex.Properties.Count, StringComparer.Ordinal);
        int offset = 0;
        foreach (PlyProperty property in vertex.Properties)
        {
            if (property.IsList)
            {
                throw new InvalidDataException("3DGS vertex element must not contain list properties.");
            }

            int size = PlyParser.ScalarSize(property.Type);
            if (property.Type is "float" or "float32")
            {
                floatOffsets[property.Name] = offset;
            }

            offset += size;
        }

        int expectedBody = stride * vertex.Count;
        int bodyAvailable = plyBytes.Length - header.HeaderByteLength;
        if (bodyAvailable < expectedBody)
        {
            throw new InvalidDataException($"PLY body too short: need {expectedBody} bytes after header, have {bodyAvailable}.");
        }

        var stream = new GaussianSplatStream
        {
            Count = vertex.Count,
            PositionsXYZ = ReadFloats(plyBytes, header.HeaderByteLength, stride, vertex.Count, RequireOffsets(floatOffsets, "x", "y", "z")),
            OpacitiesRaw = ReadFloats(plyBytes, header.HeaderByteLength, stride, vertex.Count, RequireOffsets(floatOffsets, "opacity")),
            ScalesLog = ReadFloats(plyBytes, header.HeaderByteLength, stride, vertex.Count, RequireOffsets(floatOffsets, "scale_0", "scale_1", "scale_2")),
            RotationsRaw = ReadFloats(plyBytes, header.HeaderByteLength, stride, vertex.Count, RequireOffsets(floatOffsets, "rot_0", "rot_1", "rot_2", "rot_3")),
        };

        if (TryGetOffsets(floatOffsets, out int[]? dc, "f_dc_0", "f_dc_1", "f_dc_2") && dc != null)
        {
            stream.ShDc = ReadFloats(plyBytes, header.HeaderByteLength, stride, vertex.Count, dc);
        }

        var restOffsets = new List<int>(45);
        for (int i = 0; ; i++)
        {
            if (!floatOffsets.TryGetValue($"f_rest_{i}", out int restOffset))
            {
                break;
            }

            restOffsets.Add(restOffset);
        }

        if (restOffsets.Count > 0)
        {
            stream.ShRestStride = restOffsets.Count;
            stream.ShRest = ReadFloats(plyBytes, header.HeaderByteLength, stride, vertex.Count, restOffsets.ToArray());
        }

        return stream;
    }

    private static bool TryGetOffsets(Dictionary<string, int> table, out int[]? result, params string[] names)
    {
        result = new int[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            if (!table.TryGetValue(names[i], out result[i]))
            {
                result = null;
                return false;
            }
        }

        return true;
    }

    private static int[] RequireOffsets(Dictionary<string, int> table, params string[] names)
    {
        if (!TryGetOffsets(table, out int[]? result, names) || result == null)
        {
            throw new InvalidDataException("3DGS PLY missing required properties: " + string.Join(",", names));
        }

        return result;
    }

    private static float[] ReadFloats(byte[] body, int bodyOffset, int stride, int count, int[] columnOffsets)
    {
        int columns = columnOffsets.Length;
        var dst = new float[count * columns];
        int dstIndex = 0;
        int rowBase = bodyOffset;

        for (int row = 0; row < count; row++)
        {
            for (int c = 0; c < columns; c++)
            {
                dst[dstIndex++] = BitConverter.ToSingle(body, rowBase + columnOffsets[c]);
            }

            rowBase += stride;
        }

        return dst;
    }
}
