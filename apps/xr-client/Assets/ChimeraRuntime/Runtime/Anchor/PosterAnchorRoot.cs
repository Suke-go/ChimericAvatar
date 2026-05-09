using UnityEngine;

namespace Chimera.Runtime
{
    /// <summary>
    /// Abstract poster-anchored root. The transform is the center of the A0 poster
    /// plane in world space; local +X right, +Y up, +Z forward from the poster.
    ///
    /// Concrete backends live under sibling folders and are picked at boot by
    /// <see cref="AnchorBackendFactory"/> based on <see cref="RuntimeBuildProfile"/>:
    ///   Anchor/Stub/    StubPosterAnchorRoot           — editor smoke
    ///   Anchor/Manual/  ManualControllerAnchorRoot     — Quest 3 / XREAL controller-driven
    ///   Anchor/Meta/    MetaMarkerPosterAnchorRoot     — Meta XR SDK Marker Tracking (Quest 3)
    ///   Anchor/Xreal/   XrealPosterAnchorRoot          — XREAL SDK 3.1.0 Image Tracking
    /// </summary>
    public abstract class PosterAnchorRoot : MonoBehaviour
    {
        [Tooltip("Reference image name from manifest.tracking.referenceImageName")]
        public string ReferenceImageName;

        [Tooltip("Physical size in meters [width, height]")]
        public Vector2 PhysicalSizeM = new Vector2(0.841f, 1.189f);

        public bool IsTracking { get; protected set; }

        /// <summary>
        /// Spawns a child anchor at the given panel-local pose. Used by gesture/cue
        /// handlers to hang text/figure/IK targets off the poster surface.
        /// </summary>
        public virtual Transform CreatePanelAnchor(string panelId, PanelLocalBounds bounds)
        {
            var go = new GameObject($"PanelAnchor:{panelId}");
            go.transform.SetParent(transform, worldPositionStays: false);
            if (bounds?.Center != null && bounds.Center.Length == 3)
            {
                go.transform.localPosition = new Vector3(bounds.Center[0], bounds.Center[1], bounds.Center[2]);
            }
            return go.transform;
        }
    }
}
