using System;
using System.Collections.Generic;

namespace Chimera.Runtime
{
    /// <summary>
    /// One contiguous run of splats that all bind to the same bone.
    /// PermutedSplatIndices[Start..Start+Count) holds the original splat indices
    /// from the PLY vertex stream that belong to BoneIndex.
    /// </summary>
    public struct SplatBoneRange
    {
        public int BoneIndex;
        public int Start;
        public int Count;
    }

    /// <summary>
    /// Reorders splats so that all splats sharing a bone are contiguous, which lets the
    /// skinning compute shader dispatch one work group per bone with a tight read pattern.
    /// Stable within a bone (preserves original relative order), so splatRelativePoses
    /// indexed by the original splat index can be remapped 1:1 via PermutedSplatIndices.
    /// </summary>
    public class GvrmBoneBatches
    {
        public int[] PermutedSplatIndices;
        public SplatBoneRange[] Ranges;
        public int BoneCount;
        public int SplatCount;

        public static GvrmBoneBatches Build(IList<int> splatBoneIndices)
        {
            if (splatBoneIndices == null) throw new ArgumentNullException(nameof(splatBoneIndices));
            int n = splatBoneIndices.Count;
            int boneCount = 0;
            for (int i = 0; i < n; i++)
            {
                int b = splatBoneIndices[i];
                if (b < 0) throw new ArgumentOutOfRangeException(nameof(splatBoneIndices), $"negative bone index at {i}: {b}");
                if (b + 1 > boneCount) boneCount = b + 1;
            }

            // Counting sort by bone index — O(n) and stable.
            var counts = new int[boneCount];
            for (int i = 0; i < n; i++) counts[splatBoneIndices[i]]++;

            var starts = new int[boneCount];
            int running = 0;
            var ranges = new List<SplatBoneRange>(boneCount);
            for (int b = 0; b < boneCount; b++)
            {
                starts[b] = running;
                if (counts[b] > 0)
                {
                    ranges.Add(new SplatBoneRange { BoneIndex = b, Start = running, Count = counts[b] });
                }
                running += counts[b];
            }

            var permuted = new int[n];
            var cursor = (int[])starts.Clone();
            for (int i = 0; i < n; i++)
            {
                int b = splatBoneIndices[i];
                permuted[cursor[b]++] = i;
            }

            return new GvrmBoneBatches
            {
                PermutedSplatIndices = permuted,
                Ranges = ranges.ToArray(),
                BoneCount = boneCount,
                SplatCount = n,
            };
        }
    }
}
