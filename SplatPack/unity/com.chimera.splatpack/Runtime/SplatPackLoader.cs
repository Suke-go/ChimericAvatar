using System;
using System.IO;
using UnityEngine;

namespace SplatPack.Runtime
{
    public static class SplatPackLoader
    {
        public static SplatPackPackage Load(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new ArgumentException("SplatPack bytes are empty.", nameof(bytes));
            }

            using var stream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(stream);

            byte s = reader.ReadByte();
            byte p = reader.ReadByte();
            byte k = reader.ReadByte();
            byte one = reader.ReadByte();
            if (s != (byte)'S' || p != (byte)'P' || k != (byte)'K' || one != (byte)'1')
            {
                throw new InvalidDataException("Invalid SplatPack magic.");
            }

            int version = reader.ReadInt32();
            if (version != SplatPackFormat.Version)
            {
                throw new InvalidDataException($"Unsupported SplatPack version {version}.");
            }

            int splatCount = reader.ReadInt32();
            int chunkCount = reader.ReadInt32();
            int splatStride = reader.ReadInt32();
            int chunkStride = reader.ReadInt32();
            if (splatStride != SplatPackFormat.SplatStride || chunkStride != SplatPackFormat.ChunkStride)
            {
                throw new InvalidDataException($"Unsupported SplatPack layout: splatStride={splatStride}, chunkStride={chunkStride}.");
            }

            Vector3 boundsMin = ReadVector3(reader);
            Vector3 boundsMax = ReadVector3(reader);
            var package = new SplatPackPackage
            {
                Bounds = new Bounds((boundsMin + boundsMax) * 0.5f, boundsMax - boundsMin),
                Chunks = new SplatPackChunk[chunkCount],
                Splats = new SplatPackSplat[splatCount],
            };

            for (int i = 0; i < chunkCount; i++)
            {
                package.Chunks[i] = new SplatPackChunk
                {
                    BoundsMin = ReadVector3(reader),
                    BoundsMax = ReadVector3(reader),
                    SplatOffset = reader.ReadInt32(),
                    SplatCount = reader.ReadInt32(),
                    LodLevel = reader.ReadInt32(),
                };
            }

            for (int i = 0; i < splatCount; i++)
            {
                package.Splats[i] = new SplatPackSplat
                {
                    CenterWS = ReadVector4(reader),
                    Axis0WS = ReadVector4(reader),
                    Axis1WS = ReadVector4(reader),
                    Axis2WS = ReadVector4(reader),
                    Color = ReadVector4(reader),
                    Meta = ReadVector4(reader),
                };
            }

            return package;
        }

        public static SplatPackPackage LoadFile(string path)
        {
            return Load(File.ReadAllBytes(path));
        }

        private static Vector3 ReadVector3(BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private static Vector4 ReadVector4(BinaryReader reader)
        {
            return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
    }
}
