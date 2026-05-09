using System.IO;
using System.IO.Compression;
using Newtonsoft.Json;

namespace Chimera.Runtime
{
    public class GvrmBundle
    {
        public byte[] VrmBytes;
        public byte[] PlyBytes;
        public GvrmMetadata Metadata;
        public string DataJsonRaw;
    }

    public static class GvrmZipReader
    {
        private const string EntryVrm = "model.vrm";
        private const string EntryPly = "model.ply";
        private const string EntryDataJson = "data.json";

        public static GvrmBundle Load(byte[] gvrmZipBytes)
        {
            if (gvrmZipBytes == null || gvrmZipBytes.Length < 4)
            {
                throw new InvalidDataException(".gvrm bytes are empty");
            }
            if (!(gvrmZipBytes[0] == 'P' && gvrmZipBytes[1] == 'K'))
            {
                throw new InvalidDataException(".gvrm bytes are not a ZIP archive (missing PK header)");
            }

            using var stream = new MemoryStream(gvrmZipBytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var bundle = new GvrmBundle
            {
                VrmBytes = ReadEntryBytes(archive, EntryVrm),
                PlyBytes = ReadEntryBytes(archive, EntryPly),
                DataJsonRaw = ReadEntryString(archive, EntryDataJson),
            };
            bundle.Metadata = JsonConvert.DeserializeObject<GvrmMetadata>(bundle.DataJsonRaw);

            ValidateMetadata(bundle.Metadata);
            ValidateVrm(bundle.VrmBytes);
            ValidatePly(bundle.PlyBytes);
            return bundle;
        }

        private static byte[] ReadEntryBytes(ZipArchive archive, string name)
        {
            ZipArchiveEntry entry = archive.GetEntry(name)
                ?? throw new InvalidDataException($".gvrm missing required entry: {name}");
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        private static string ReadEntryString(ZipArchive archive, string name)
        {
            ZipArchiveEntry entry = archive.GetEntry(name)
                ?? throw new InvalidDataException($".gvrm missing required entry: {name}");
            using var s = entry.Open();
            using var sr = new StreamReader(s);
            return sr.ReadToEnd();
        }

        private static void ValidateMetadata(GvrmMetadata m)
        {
            if (m == null) throw new InvalidDataException("data.json deserialized to null");
            if (m.ModelScale <= 0) throw new InvalidDataException("modelScale must be > 0");
            if (m.GsPosition == null || m.GsPosition.Length != 3) throw new InvalidDataException("gsPosition must have 3 floats");
            if (m.GsQuaternion == null || m.GsQuaternion.Length != 4) throw new InvalidDataException("gsQuaternion must have 4 floats");
            if (m.SplatVertexIndices == null) throw new InvalidDataException("splatVertexIndices missing");
            if (m.SplatBoneIndices == null) throw new InvalidDataException("splatBoneIndices missing");
            if (m.SplatRelativePoses == null) throw new InvalidDataException("splatRelativePoses missing");
        }

        private static void ValidateVrm(byte[] bytes)
        {
            if (bytes.Length < 4 || bytes[0] != 'g' || bytes[1] != 'l' || bytes[2] != 'T' || bytes[3] != 'F')
            {
                throw new InvalidDataException("model.vrm is not a valid GLB/VRM (expected glTF magic)");
            }
        }

        private static void ValidatePly(byte[] bytes)
        {
            if (bytes.Length < 3 || bytes[0] != 'p' || bytes[1] != 'l' || bytes[2] != 'y')
            {
                throw new InvalidDataException("model.ply does not start with 'ply'");
            }
        }
    }
}
