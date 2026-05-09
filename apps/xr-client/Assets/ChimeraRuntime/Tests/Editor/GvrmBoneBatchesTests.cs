using NUnit.Framework;
using Chimera.Runtime;

namespace Chimera.Runtime.Tests
{
    public class GvrmBoneBatchesTests
    {
        [Test]
        public void Build_GroupsSplatsContiguouslyByBone()
        {
            // 7 splats, bones interleaved: 1, 0, 2, 1, 0, 0, 2
            int[] boneIndices = { 1, 0, 2, 1, 0, 0, 2 };
            var batches = GvrmBoneBatches.Build(boneIndices);

            Assert.AreEqual(3, batches.BoneCount);
            Assert.AreEqual(7, batches.SplatCount);
            Assert.AreEqual(3, batches.Ranges.Length);

            // bone 0 first (3 splats), bone 1 next (2 splats), bone 2 last (2 splats)
            Assert.AreEqual(0, batches.Ranges[0].BoneIndex);
            Assert.AreEqual(0, batches.Ranges[0].Start);
            Assert.AreEqual(3, batches.Ranges[0].Count);

            Assert.AreEqual(1, batches.Ranges[1].BoneIndex);
            Assert.AreEqual(3, batches.Ranges[1].Start);
            Assert.AreEqual(2, batches.Ranges[1].Count);

            Assert.AreEqual(2, batches.Ranges[2].BoneIndex);
            Assert.AreEqual(5, batches.Ranges[2].Start);
            Assert.AreEqual(2, batches.Ranges[2].Count);

            // Stable within bone: original indices 1, 4, 5 have bone 0 in that order.
            Assert.AreEqual(1, batches.PermutedSplatIndices[0]);
            Assert.AreEqual(4, batches.PermutedSplatIndices[1]);
            Assert.AreEqual(5, batches.PermutedSplatIndices[2]);
        }

        [Test]
        public void Build_AllowsSparseBoneIds()
        {
            // bone 5 only: ranges should still grow to 6 to keep boneIndex addressable.
            int[] boneIndices = { 5, 5 };
            var batches = GvrmBoneBatches.Build(boneIndices);
            Assert.AreEqual(6, batches.BoneCount);
            Assert.AreEqual(1, batches.Ranges.Length);
            Assert.AreEqual(5, batches.Ranges[0].BoneIndex);
            Assert.AreEqual(2, batches.Ranges[0].Count);
        }
    }
}
