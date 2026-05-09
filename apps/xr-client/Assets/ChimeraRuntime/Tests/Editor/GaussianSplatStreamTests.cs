using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using Chimera.Runtime;

namespace Chimera.Runtime.Tests
{
    public class GaussianSplatStreamTests
    {
        private static byte[] BuildMinimal3DGSPly(out float[][] expected)
        {
            // 2 vertices, properties: x y z opacity scale_0 scale_1 scale_2 rot_0 rot_1 rot_2 rot_3
            string headerText = string.Join("\n", new[]
            {
                "ply",
                "format binary_little_endian 1.0",
                "element vertex 2",
                "property float x",
                "property float y",
                "property float z",
                "property float opacity",
                "property float scale_0",
                "property float scale_1",
                "property float scale_2",
                "property float rot_0",
                "property float rot_1",
                "property float rot_2",
                "property float rot_3",
                "end_header",
                "",
            });
            byte[] header = Encoding.ASCII.GetBytes(headerText);

            // 11 floats * 2 vertices = 88 bytes
            float[] v0 = { 1f, 2f, 3f, 0.5f, -1f, -2f, -3f, 0.1f, 0.2f, 0.3f, 0.9273f };
            float[] v1 = { 4f, 5f, 6f, 0.25f, 0f, 0f, 0f, 1f, 0f, 0f, 0f };
            expected = new[] { v0, v1 };

            using var ms = new MemoryStream();
            ms.Write(header, 0, header.Length);
            using var bw = new BinaryWriter(ms);
            for (int i = 0; i < v0.Length; i++) bw.Write(v0[i]);
            for (int i = 0; i < v1.Length; i++) bw.Write(v1[i]);
            return ms.ToArray();
        }

        [Test]
        public void Read_ExtractsPositionsOpacityScalesRotations()
        {
            byte[] bytes = BuildMinimal3DGSPly(out float[][] expected);
            var stream = GaussianSplatStream.Read(bytes);

            Assert.AreEqual(2, stream.Count);
            Assert.AreEqual(6, stream.PositionsXYZ.Length);
            Assert.AreEqual(2, stream.OpacitiesRaw.Length);
            Assert.AreEqual(6, stream.ScalesLog.Length);
            Assert.AreEqual(8, stream.RotationsXYZW.Length);

            Assert.AreEqual(expected[0][0], stream.PositionsXYZ[0]);
            Assert.AreEqual(expected[0][1], stream.PositionsXYZ[1]);
            Assert.AreEqual(expected[0][2], stream.PositionsXYZ[2]);
            Assert.AreEqual(expected[1][0], stream.PositionsXYZ[3]);

            Assert.AreEqual(expected[0][3], stream.OpacitiesRaw[0]);
            Assert.AreEqual(expected[1][3], stream.OpacitiesRaw[1]);

            Assert.AreEqual(expected[0][4], stream.ScalesLog[0]);
            Assert.AreEqual(expected[1][6], stream.ScalesLog[5]);

            Assert.AreEqual(expected[0][7], stream.RotationsXYZW[0]);
            Assert.AreEqual(expected[0][10], stream.RotationsXYZW[3]);
            Assert.AreEqual(expected[1][7], stream.RotationsXYZW[4]);

            // Optional channels absent.
            Assert.IsNull(stream.NormalsXYZ);
            Assert.IsNull(stream.ShDc);
            Assert.IsNull(stream.ShRest);
        }

        [Test]
        public void Read_RejectsAsciiPly()
        {
            string headerText = string.Join("\n", new[]
            {
                "ply",
                "format ascii 1.0",
                "element vertex 1",
                "property float x",
                "property float y",
                "property float z",
                "property float opacity",
                "property float scale_0",
                "property float scale_1",
                "property float scale_2",
                "property float rot_0",
                "property float rot_1",
                "property float rot_2",
                "property float rot_3",
                "end_header",
                "0 0 0 0 0 0 0 0 0 0 0",
                "",
            });
            byte[] bytes = Encoding.ASCII.GetBytes(headerText);
            Assert.Throws<InvalidDataException>(() => GaussianSplatStream.Read(bytes));
        }
    }
}
