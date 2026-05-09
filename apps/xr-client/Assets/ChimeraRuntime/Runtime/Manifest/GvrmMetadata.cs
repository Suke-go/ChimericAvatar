using System.Collections.Generic;
using Newtonsoft.Json;

namespace Chimera.Runtime
{
    public class GvrmMetadata
    {
        [JsonProperty("modelScale")] public float ModelScale = 1f;
        [JsonProperty("boneOperations")] public List<Dictionary<string, object>> BoneOperations;
        [JsonProperty("gsPosition")] public float[] GsPosition;
        [JsonProperty("gsQuaternion")] public float[] GsQuaternion;
        [JsonProperty("splatVertexIndices")] public List<int> SplatVertexIndices;
        [JsonProperty("splatBoneIndices")] public List<int> SplatBoneIndices;
        [JsonProperty("splatRelativePoses")] public List<float> SplatRelativePoses;
    }
}
