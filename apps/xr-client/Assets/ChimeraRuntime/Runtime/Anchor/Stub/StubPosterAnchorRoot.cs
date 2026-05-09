using UnityEngine;

namespace Chimera.Runtime
{
    /// <summary>
    /// Editor / desk-test stand-in that pretends tracking succeeded the moment the scene starts.
    /// Used by Bootstrap.Editor.unity. Replace with a vendor backend for device builds.
    /// </summary>
    public class StubPosterAnchorRoot : PosterAnchorRoot
    {
        [Tooltip("Optional Transform that already represents the tracked poster pose.")]
        public Transform DebugAnchor;

        private void Awake()
        {
            if (DebugAnchor != null)
            {
                transform.SetParent(DebugAnchor, worldPositionStays: false);
                transform.localPosition = Vector3.zero;
                transform.localRotation = Quaternion.identity;
            }
            IsTracking = true;
        }
    }
}
