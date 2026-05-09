using System.Collections.Generic;
using UnityEngine;

namespace Chimera.Runtime
{
    /// <summary>
    /// Per-build wiring for the Chimera XR client. One asset per device target lives
    /// in Assets/ChimeraRuntime/BuildProfiles/. Bootstrap scenes drag the right
    /// profile into ChimeraSession or AnchorBackendFactory.
    ///
    /// We prefer this over a stack of #if branches in scene scripts because
    /// the same Bootstrap.unity scene can ship to Quest, XREAL, and Editor by
    /// just swapping the asset reference.
    /// </summary>
    public enum AnchorBackendKind
    {
        Stub = 0,
        ManualController = 1,
        MetaMarker = 2,
        XrealImage = 3,
    }

    public enum XrPlatform
    {
        Editor = 0,
        MetaQuest = 1,
        Xreal = 2,
    }

    [CreateAssetMenu(fileName = "RuntimeBuildProfile", menuName = "Chimera/Runtime Build Profile", order = 0)]
    public class RuntimeBuildProfile : ScriptableObject
    {
        [Tooltip("Display name shown in editor logs and on-device debug UI.")]
        public string DisplayName = "Editor";

        [Tooltip("Which XR platform this build targets.")]
        public XrPlatform Platform = XrPlatform.Editor;

        [Tooltip("Anchor backends are tried in order; the first one whose runtime requirements are met wins.")]
        public List<AnchorBackendKind> AnchorBackendOrder = new List<AnchorBackendKind>
        {
            AnchorBackendKind.Stub,
        };

        [Tooltip("Default Chimera API base URL for this build. Overridable from ChimeraSession.")]
        public string DefaultApiBaseUrl = "http://localhost:8000";

        [Tooltip("If true, the build is allowed to override manifest.tracking.type with the user-confirmed manual placement.")]
        public bool AllowManualPlacementOverride = true;
    }
}
