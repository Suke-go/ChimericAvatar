using UnityEngine;

namespace Chimera.Runtime
{
    /// <summary>
    /// Quest 3 backend that drives the poster anchor from Meta XR SDK Marker Tracking.
    ///
    /// Build gate:
    ///   Define <c>CHIMERA_META_XR</c> in PlayerSettings → Other → Scripting Define
    ///   Symbols once com.meta.xr.sdk.core (and com.meta.xr.sdk.markertracking, when
    ///   shipped) are imported. Without the define, this file is an empty stand-in
    ///   so the rest of the assembly compiles for Editor / XREAL builds.
    ///
    /// Phase 2 work fills in:
    ///   - subscribe to <c>OVRMarkerTracking</c> (or successor) detected events
    ///   - filter to manifest.tracking.referenceImageName
    ///   - copy reported world pose onto this transform (poster center, +Z out)
    ///   - set <c>IsTracking = true</c> while the marker is in view
    ///   - drop tracking after the configured timeout to allow ManualController fallback
    /// </summary>
    public class MetaMarkerPosterAnchorRoot : PosterAnchorRoot
    {
#if CHIMERA_META_XR
        // TODO Phase 2: bind Meta XR SDK marker subscription here.
        // Example sketch (concrete API depends on the shipped SDK version):
        //
        //   private void OnEnable()  { Meta.XR.MRUtility.MarkerEvents.MarkerUpdated += OnMarkerUpdated; }
        //   private void OnDisable() { Meta.XR.MRUtility.MarkerEvents.MarkerUpdated -= OnMarkerUpdated; }
        //   private void OnMarkerUpdated(MarkerData m)
        //   {
        //       if (m.Name != ReferenceImageName) return;
        //       transform.SetPositionAndRotation(m.Pose.position, m.Pose.rotation);
        //       IsTracking = m.IsTracking;
        //   }
#else
        private void Awake()
        {
            Debug.LogWarning(
                "[Chimera] MetaMarkerPosterAnchorRoot present but CHIMERA_META_XR is not defined. " +
                "Import com.meta.xr.sdk.core and add CHIMERA_META_XR to Scripting Define Symbols, " +
                "or remove this component from the scene.");
        }
#endif
    }
}
