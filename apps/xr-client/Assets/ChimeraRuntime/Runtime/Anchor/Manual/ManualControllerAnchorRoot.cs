using System;
using UnityEngine;

namespace Chimera.Runtime
{
    /// <summary>
    /// Controller-driven manual placement. Works on Quest 3 (Touch Plus) and XREAL
    /// (Beam Pro controller) without requiring image tracking. Bootstrap or any
    /// other component calls <see cref="ConfirmAtPose"/> when the user presses
    /// the confirm button — typically wired through XR Interaction Toolkit
    /// InputAction in the Phase 1 scene, or via keyboard in the editor.
    ///
    /// Placement convention: <paramref name="poseWorld"/> is the pose of the poster
    /// CENTER, with +Z facing OUT of the poster surface. The caller is expected to
    /// have aligned a preview quad with PhysicalSizeM before confirming.
    ///
    /// To preview placement before commit: keep a preview transform
    /// (any visual you like — quad textured with the poster image is recommended)
    /// updated each frame from the controller pose, then call ConfirmAtPose on input.
    /// </summary>
    public class ManualControllerAnchorRoot : PosterAnchorRoot
    {
        [Tooltip("If true, pressing Space in the editor confirms placement at the main camera + 1m forward (debug aid).")]
        public bool EditorConfirmWithSpace = true;

        [Tooltip("Distance ahead of the camera to place the poster when using the editor Space shortcut.")]
        public float EditorPlacementDistance = 1f;

        public event Action<Pose> OnPlacementConfirmed;

        /// <summary>
        /// Commits poster placement. Call from your InputAction.performed handler.
        /// </summary>
        public void ConfirmAtPose(Pose poseWorld)
        {
            transform.SetPositionAndRotation(poseWorld.position, poseWorld.rotation);
            IsTracking = true;
            OnPlacementConfirmed?.Invoke(poseWorld);
        }

        /// <summary>
        /// Convenience overload: places the poster at <paramref name="origin"/>
        /// with the surface normal facing back toward the user / camera.
        /// </summary>
        public void ConfirmFacingCamera(Vector3 origin, Camera camera = null)
        {
            Camera cam = camera != null ? camera : Camera.main;
            Vector3 toCam = cam != null ? (cam.transform.position - origin) : Vector3.back;
            toCam.y = 0f;
            if (toCam.sqrMagnitude < 1e-6f) toCam = Vector3.back;
            Quaternion rot = Quaternion.LookRotation(toCam.normalized, Vector3.up);
            ConfirmAtPose(new Pose(origin, rot));
        }

        public void ResetPlacement()
        {
            IsTracking = false;
        }

#if UNITY_EDITOR
        private void Update()
        {
            if (!EditorConfirmWithSpace || IsTracking) return;
            if (Input.GetKeyDown(KeyCode.Space))
            {
                Camera cam = Camera.main;
                if (cam == null) return;
                Vector3 origin = cam.transform.position + cam.transform.forward * EditorPlacementDistance;
                ConfirmFacingCamera(origin, cam);
            }
        }
#endif
    }
}
