using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Chimera.Runtime
{
    /// <summary>
    /// Loads the default human-form VRM under a parent transform.
    /// Two backends:
    ///
    ///   CHIMERA_UNIVRM defined  → real UniVRM 1.0 runtime importer
    ///                             (UniGLTF.GltfData + Vrm10.LoadAsync).
    ///   not defined             → instantiates a primitive placeholder so the
    ///                             rest of the pipeline (cues, captions, audio)
    ///                             can be exercised without the .unitypackage.
    ///
    /// Define CHIMERA_UNIVRM in PlayerSettings → Other → Scripting Define Symbols
    /// after importing UniVRM v0.131+ via .unitypackage or via Package Manager
    /// (com.vrmc.vrm). Until then, this script compiles cleanly and produces
    /// a visible stand-in avatar.
    /// </summary>
    public class DefaultVrmLoader : MonoBehaviour
    {
        [Tooltip("Optional bytes pulled from manifest.avatar.sources['vrm']; leave null to instantiate the placeholder.")]
        public byte[] VrmBytes;

        [Tooltip("Where to parent the loaded VRM. Defaults to this transform.")]
        public Transform Parent;

        [Tooltip("If true, calls LoadAsync from Start.")]
        public bool AutoLoadOnStart;

        public GameObject LoadedAvatar { get; private set; }
        public bool IsUsingPlaceholder { get; private set; }

        public event Action<GameObject> OnAvatarLoaded;

        private async void Start()
        {
            if (AutoLoadOnStart) await LoadAsync();
        }

        public async Task<GameObject> LoadAsync(CancellationToken ct = default)
        {
            Transform parent = Parent != null ? Parent : transform;

#if CHIMERA_UNIVRM
            if (VrmBytes != null && VrmBytes.Length > 0)
            {
                LoadedAvatar = await LoadWithUniVrmAsync(VrmBytes, parent, ct);
                IsUsingPlaceholder = false;
                OnAvatarLoaded?.Invoke(LoadedAvatar);
                return LoadedAvatar;
            }
#endif

            LoadedAvatar = CreatePlaceholder(parent);
            IsUsingPlaceholder = true;
            await Task.Yield();
            OnAvatarLoaded?.Invoke(LoadedAvatar);
            return LoadedAvatar;
        }

#if CHIMERA_UNIVRM
        private static async Task<GameObject> LoadWithUniVrmAsync(byte[] vrmBytes, Transform parent, CancellationToken ct)
        {
            // UniVRM 1.0 (com.vrmc.vrm) entrypoint; falls back to UniVRM 0.x via
            // VRMImporterContext if your project uses the legacy line — wire here.
            // We use reflection to keep this assembly independent of the UniVRM types
            // until the user-side define+import are both in place.
            var loaderType = Type.GetType("UniVRM10.Vrm10, VRM10")
                              ?? Type.GetType("VRM.VRMImporterContext, VRM");
            if (loaderType == null)
            {
                throw new InvalidOperationException(
                    "CHIMERA_UNIVRM is defined but neither UniVRM10.Vrm10 nor VRM.VRMImporterContext was found. " +
                    "Import UniVRM v0.131+ before defining CHIMERA_UNIVRM.");
            }

            // UniVRM10.Vrm10.LoadBytesAsync(byte[], bool) → Task<Vrm10Instance>
            var method = loaderType.GetMethod("LoadBytesAsync", new[] { typeof(byte[]), typeof(bool) });
            if (method == null)
            {
                throw new InvalidOperationException(
                    "UniVRM10.Vrm10.LoadBytesAsync signature not found. Wire your local UniVRM API here.");
            }
            var task = (Task)method.Invoke(null, new object[] { vrmBytes, true });
            await task.ConfigureAwait(true);
            var resultProp = task.GetType().GetProperty("Result");
            object instance = resultProp?.GetValue(task);
            if (instance == null) throw new InvalidOperationException("UniVRM returned null instance");
            var goProp = instance.GetType().GetProperty("gameObject") ?? instance.GetType().GetProperty("GameObject");
            var avatar = (GameObject)(goProp?.GetValue(instance));
            if (avatar == null) throw new InvalidOperationException("UniVRM instance had no gameObject");
            avatar.transform.SetParent(parent, worldPositionStays: false);
            return avatar;
        }
#endif

        private static GameObject CreatePlaceholder(Transform parent)
        {
            var root = new GameObject("DefaultVrm(Placeholder)");
            root.transform.SetParent(parent, worldPositionStays: false);

            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(root.transform, worldPositionStays: false);
            body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            body.transform.localScale = new Vector3(0.4f, 0.6f, 0.4f);

            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            head.transform.SetParent(root.transform, worldPositionStays: false);
            head.transform.localPosition = new Vector3(0f, 1.65f, 0f);
            head.transform.localScale = Vector3.one * 0.25f;

            return root;
        }
    }
}
