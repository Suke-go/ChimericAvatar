using System;
using System.Collections.Generic;
using System.IO;

namespace Chimera.Runtime
{
    /// <summary>
    /// Decoded Gaussian-Splat vertex stream from a 3DGS PLY (binary_little_endian).
    /// Field order matches the typical Inria/gsplat export and Scaniverse export:
    ///   x, y, z, nx, ny, nz, f_dc_0..2, f_rest_0..N, opacity, scale_0..2, rot_0..3.
    /// Missing optional channels (normals, SH-rest) are simply absent.
    /// All raw values; scale is log-space and rot is non-normalized — caller decides.
    /// </summary>
    public class GaussianSplatStream
    {
        public int Count;

        // Required channels.
        public float[] PositionsXYZ;     // 3 * Count
        public float[] OpacitiesRaw;     // 1 * Count
        public float[] ScalesLog;        // 3 * Count
        public float[] RotationsXYZW;    // 4 * Count

        // Optional channels.
        public float[] NormalsXYZ;       // 3 * Count, may be null
        public float[] ShDc;             // 3 * Count (degree 0), may be null
        public float[] ShRest;           // ShRestStride * Count, may be null
        public int ShRestStride;         // typically 45 for SH degree 3

        /// <summary>
        /// Parse the binary_little_endian vertex element of a 3DGS PLY into a typed stream.
        /// Header is parsed by <see cref="PlyParser.ParseHeader"/>.
        /// </summary>
        public static GaussianSplatStream Read(byte[] plyBytes, PlyHeader header = null)
        {
            if (plyBytes == null || plyBytes.Length == 0) throw new ArgumentNullException(nameof(plyBytes));
            header ??= PlyParser.ParseHeader(plyBytes);
            if (header.Format != PlyFormat.BinaryLittleEndian)
            {
                throw new InvalidDataException(
                    $"GaussianSplatStream requires binary_little_endian PLY (got {header.Format})");
            }

            PlyElement vertex = header.FindElement("vertex")
                ?? throw new InvalidDataException("PLY missing 'vertex' element");
            int stride = PlyParser.VertexStride(vertex);

            // Build column offsets keyed by property name (only float-valued props are addressable here).
            var floatOffsets = new Dictionary<string, int>(vertex.Properties.Count);
            int offset = 0;
            foreach (var p in vertex.Properties)
            {
                if (p.IsList) throw new InvalidDataException("3DGS PLY vertex element must not contain list properties");
                int size = PlyParser.ScalarSize(p.Type);
                if (p.Type == "float" || p.Type == "float32") floatOffsets[p.Name] = offset;
                offset += size;
            }

            int expectedBody = stride * vertex.Count;
            int bodyAvailable = plyBytes.Length - header.HeaderByteLength;
            if (bodyAvailable < expectedBody)
            {
                throw new InvalidDataException(
                    $"PLY body too short: need {expectedBody} bytes after header, have {bodyAvailable}");
            }

            var stream = new GaussianSplatStream { Count = vertex.Count };

            int[] xyz = RequireOffsets(floatOffsets, "x", "y", "z");
            stream.PositionsXYZ = ReadFloats(plyBytes, header.HeaderByteLength, stride, vertex.Count, xyz);

            if (TryGetOffsets(floatOffsets, out var nxyz, "nx", "ny", "nz"))
            {
                stream.NormalsXYZ = ReadFloats(plyBytes, header.HeaderByteLength, stride, vertex.Count, nxyz);
            }

            if (!floatOffsets.TryGetValue("opacity", out int opOff))
            {
                throw new InvalidDataException("3DGS PLY missing 'opacity' property");
            }
            stream.OpacitiesRaw = ReadFloats(plyBytes, header.HeaderByteLength, stride, vertex.Count, new[] { opOff });

            int[] scl = RequireOffsets(floatOffsets, "scale_0", "scale_1", "scale_2");
            stream.ScalesLog = ReadFloats(plyBytes, header.HeaderByteLength, stride, vertex.Count, scl);

            int[] rot = RequireOffsets(floatOffsets, "rot_0", "rot_1", "rot_2", "rot_3");
            stream.RotationsXYZW = ReadFloats(plyBytes, header.HeaderByteLength, stride, vertex.Count, rot);

            if (TryGetOffsets(floatOffsets, out var dc, "f_dc_0", "f_dc_1", "f_dc_2"))
            {
                stream.ShDc = ReadFloats(plyBytes, header.HeaderByteLength, stride, vertex.Count, dc);
            }

            // f_rest_0..N — discover dynamically.
            var restOffs = new List<int>(45);
            for (int i = 0; ; i++)
            {
                if (!floatOffsets.TryGetValue($"f_rest_{i}", out int o)) break;
                restOffs.Add(o);
            }
            if (restOffs.Count > 0)
            {
                stream.ShRestStride = restOffs.Count;
                stream.ShRest = ReadFloats(plyBytes, header.HeaderByteLength, stride, vertex.Count, restOffs.ToArray());
            }

            return stream;
        }

        private static bool TryGetOffsets(Dictionary<string, int> table, out int[] result, params string[] names)
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
            if (!TryGetOffsets(table, out var result, names))
            {
                throw new InvalidDataException("3DGS PLY missing required properties: " + string.Join(",", names));
            }
            return result;
        }

        private static float[] ReadFloats(byte[] body, int bodyOffset, int stride, int count, int[] columnOffsets)
        {
            int columns = columnOffsets.Length;
            var dst = new float[(long)count * columns];
            // Hot loop: avoid BitConverter allocation by reading bytes directly.
            // Endian assumption: PLY is binary_little_endian, runtime is little-endian
            // on every platform Unity targets — verified by BitConverter.IsLittleEndian.
            if (!BitConverter.IsLittleEndian)
            {
                ReadFloatsBigEndianHost(body, bodyOffset, stride, count, columnOffsets, dst);
                return dst;
            }
            int dstIdx = 0;
            int rowBase = bodyOffset;
            for (int row = 0; row < count; row++)
            {
                for (int c = 0; c < columns; c++)
                {
                    dst[dstIdx++] = BitConverter.ToSingle(body, rowBase + columnOffsets[c]);
                }
                rowBase += stride;
            }
            return dst;
        }

        private static void ReadFloatsBigEndianHost(byte[] body, int bodyOffset, int stride, int count, int[] columnOffsets, float[] dst)
        {
            byte[] swap = new byte[4];
            int dstIdx = 0;
            int rowBase = bodyOffset;
            for (int row = 0; row < count; row++)
            {
                for (int c = 0; c < columnOffsets.Length; c++)
                {
                    int off = rowBase + columnOffsets[c];
                    swap[0] = body[off + 3];
                    swap[1] = body[off + 2];
                    swap[2] = body[off + 1];
                    swap[3] = body[off + 0];
                    dst[dstIdx++] = BitConverter.ToSingle(swap, 0);
                }
                rowBase += stride;
            }
        }
    }
}
