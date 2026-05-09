using System.IO;
using System.IO.Compression;
using System.Text;
using NUnit.Framework;
using Newtonsoft.Json;
using Chimera.Runtime;

namespace Chimera.Runtime.Tests
{
    public class GvrmZipReaderTests
    {
        private static byte[] BuildSyntheticGvrm(string dataJson = null, byte[] vrmBytes = null, byte[] plyBytes = null)
        {
            dataJson ??= JsonConvert.SerializeObject(new
            {
                modelScale = 1.0,
                boneOperations = new object[0],
                gsPosition = new[] { 0f, 0f, 0f },
                gsQuaternion = new[] { 0f, 0f, 0f, 1f },
                splatVertexIndices = new[] { 0, 1, 2 },
                splatBoneIndices = new[] { 0, 0, 1 },
                splatRelativePoses = new[] { 0f, 0f, 0f, 0f, 0f, 0f, 1f },
            });
            // Minimal valid magic markers: 'glTF' for VRM/GLB, 'ply' for PLY.
            vrmBytes ??= Encoding.ASCII.GetBytes("glTF\0\0\0");
            plyBytes ??= Encoding.ASCII.GetBytes("ply\nformat ascii 1.0\nend_header\n");

            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(archive, "model.vrm", vrmBytes);
                WriteEntry(archive, "model.ply", plyBytes);
                WriteEntry(archive, "data.json", Encoding.UTF8.GetBytes(dataJson));
            }
            return ms.ToArray();
        }

        private static void WriteEntry(ZipArchive archive, string name, byte[] payload)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
            using var s = entry.Open();
            s.Write(payload, 0, payload.Length);
        }

        [Test]
        public void Load_ParsesAllRequiredEntries()
        {
            byte[] gvrm = BuildSyntheticGvrm();
            var bundle = GvrmZipReader.Load(gvrm);

            Assert.IsNotNull(bundle.VrmBytes);
            Assert.IsNotNull(bundle.PlyBytes);
            Assert.IsNotNull(bundle.Metadata);
            Assert.AreEqual(1f, bundle.Metadata.ModelScale);
            Assert.AreEqual(3, bundle.Metadata.SplatVertexIndices.Count);
            Assert.AreEqual(0, bundle.Metadata.SplatBoneIndices[0]);
            Assert.AreEqual(0, bundle.Metadata.SplatBoneIndices[1]);
            Assert.AreEqual(1, bundle.Metadata.SplatBoneIndices[2]);
        }

        [Test]
        public void Load_RejectsMissingMagic()
        {
            byte[] notZip = Encoding.ASCII.GetBytes("not a zip");
            Assert.Throws<InvalidDataException>(() => GvrmZipReader.Load(notZip));
        }

        [Test]
        public void Load_RejectsBadVrmMagic()
        {
            byte[] gvrm = BuildSyntheticGvrm(vrmBytes: Encoding.ASCII.GetBytes("XXXX"));
            Assert.Throws<InvalidDataException>(() => GvrmZipReader.Load(gvrm));
        }

        [Test]
        public void Load_RejectsBadPlyMagic()
        {
            byte[] gvrm = BuildSyntheticGvrm(plyBytes: Encoding.ASCII.GetBytes("XXX"));
            Assert.Throws<InvalidDataException>(() => GvrmZipReader.Load(gvrm));
        }

        [Test]
        public void Load_RejectsBadMetadata()
        {
            string broken = JsonConvert.SerializeObject(new
            {
                modelScale = -1.0,
                boneOperations = new object[0],
                gsPosition = new[] { 0f, 0f, 0f },
                gsQuaternion = new[] { 0f, 0f, 0f, 1f },
                splatVertexIndices = new int[0],
                splatBoneIndices = new int[0],
                splatRelativePoses = new float[0],
            });
            byte[] gvrm = BuildSyntheticGvrm(dataJson: broken);
            Assert.Throws<InvalidDataException>(() => GvrmZipReader.Load(gvrm));
        }
    }
}
