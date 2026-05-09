using UnityEngine;

namespace Chimera.Runtime
{
    /// <summary>
    /// XREAL One Pro + XREAL Eye + XREAL Beam Pro backend that drives the poster
    /// anchor from XREAL SDK 3.1.0 Image Tracking (NRTrackingImageBehaviour).
    ///
    /// Build gate:
    ///   Define <c>CHIMERA_XREAL_SDK</c> in PlayerSettings → Other → Scripting Define
    ///   Symbols once XREAL SDK 3.1.0 (.unitypackage import) is in the project.
    ///   Without the define, this file is an empty stand-in.
    ///
    /// Phase 3 work fills in:
    ///   - locate the NRTrackingImageBehaviour matching manifest.tracking.referenceImageName
    ///   - copy its world pose onto this transform each frame while tracked
    ///   - clear <c>IsTracking</c> on tracking lost; respect QR fallback marker if enabled
    /// </summary>
    public class XrealPosterAnchorRoot : PosterAnchorRoot
    {
#if CHIMERA_XREAL_SDK
        // TODO Phase 3: bind NRSDK image tracking here.
        // Sketch (concrete API depends on XREAL SDK 3.1.0 surface):
        //
        //   [SerializeField] private NRSDK.NRTrackingImageDatabase _database;
        //   private NRSDK.NRTrackingImageBehaviour _binding;
        //
        //   private void Update()
        //   {
        //       if (_binding == null) _binding = FindBindingByName(ReferenceImageName);
        //       if (_binding == null) return;
        //       transform.SetPositionAndRotation(_binding.transform.position, _binding.transform.rotation);
        //       IsTracking = _binding.GetTrackingState() == NRSDK.TrackingState.Tracking;
        //   }
#else
        private void Awake()
        {
            Debug.LogWarning(
                "[Chimera] XrealPosterAnchorRoot present but CHIMERA_XREAL_SDK is not defined. " +
                "Import the XREAL SDK 3.1.0 .unitypackage and add CHIMERA_XREAL_SDK to " +
                "Scripting Define Symbols, or remove this component from the scene.");
        }
#endif
    }
}
