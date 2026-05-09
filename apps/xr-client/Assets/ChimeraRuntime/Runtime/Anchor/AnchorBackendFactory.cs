using System;
using UnityEngine;

namespace Chimera.Runtime
{
    /// <summary>
    /// Resolves a <see cref="PosterAnchorRoot"/> instance from a
    /// <see cref="RuntimeBuildProfile"/> at boot time.
    ///
    /// Two resolution flavors:
    ///   - <see cref="ResolveOrInstantiate"/> picks an already-attached anchor
    ///     under <paramref name="parent"/> if it matches, otherwise instantiates
    ///     a fresh GameObject with the right component.
    ///   - <see cref="CreateInstance"/> always creates a fresh GameObject.
    ///
    /// Vendor backends only compile when the matching SDK + define are present
    /// (CHIMERA_META_XR for Meta XR SDK, CHIMERA_XREAL_SDK for XREAL SDK 3.1.0).
    /// When a backend is unavailable, the factory transparently falls through to
    /// the next entry in <see cref="RuntimeBuildProfile.AnchorBackendOrder"/>.
    /// </summary>
    public static class AnchorBackendFactory
    {
        public static PosterAnchorRoot ResolveOrInstantiate(RuntimeBuildProfile profile, Transform parent, RuntimeManifest manifest = null)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            // If the scene already has an anchor wired up, prefer it (designer-driven override).
            var existing = parent != null ? parent.GetComponentInChildren<PosterAnchorRoot>(true) : null;
            if (existing != null) return existing;
            return CreateInstance(profile, parent, manifest);
        }

        public static PosterAnchorRoot CreateInstance(RuntimeBuildProfile profile, Transform parent, RuntimeManifest manifest = null)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            foreach (AnchorBackendKind kind in profile.AnchorBackendOrder)
            {
                PosterAnchorRoot anchor = TryCreate(kind, parent);
                if (anchor != null)
                {
                    ApplyManifest(anchor, manifest);
                    Debug.Log($"[Chimera] Anchor backend resolved: {kind} for profile '{profile.DisplayName}'");
                    return anchor;
                }
                else
                {
                    Debug.LogWarning($"[Chimera] Anchor backend '{kind}' unavailable in this build; trying next.");
                }
            }
            // Fallthrough: always succeed with the editor stub so scenes never come up
            // without a poster transform — caption / cue / audio still exercise.
            Debug.LogWarning($"[Chimera] No backend in profile '{profile.DisplayName}' was available; falling back to Stub.");
            var fallback = NewBackendGameObject<StubPosterAnchorRoot>(parent, "PosterAnchor(StubFallback)");
            ApplyManifest(fallback, manifest);
            return fallback;
        }

        private static PosterAnchorRoot TryCreate(AnchorBackendKind kind, Transform parent)
        {
            switch (kind)
            {
                case AnchorBackendKind.Stub:
                    return NewBackendGameObject<StubPosterAnchorRoot>(parent, "PosterAnchor(Stub)");
                case AnchorBackendKind.ManualController:
                    return NewBackendGameObject<ManualControllerAnchorRoot>(parent, "PosterAnchor(ManualController)");
                case AnchorBackendKind.MetaMarker:
#if CHIMERA_META_XR
                    return NewBackendGameObject<MetaMarkerPosterAnchorRoot>(parent, "PosterAnchor(MetaMarker)");
#else
                    return null;
#endif
                case AnchorBackendKind.XrealImage:
#if CHIMERA_XREAL_SDK
                    return NewBackendGameObject<XrealPosterAnchorRoot>(parent, "PosterAnchor(XrealImage)");
#else
                    return null;
#endif
                default:
                    return null;
            }
        }

        private static T NewBackendGameObject<T>(Transform parent, string name) where T : PosterAnchorRoot
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, worldPositionStays: false);
            return go.AddComponent<T>();
        }

        private static void ApplyManifest(PosterAnchorRoot anchor, RuntimeManifest manifest)
        {
            if (anchor == null || manifest?.Tracking == null) return;
            anchor.ReferenceImageName = manifest.Tracking.ReferenceImageName;
            if (manifest.Tracking.PhysicalSizeM != null && manifest.Tracking.PhysicalSizeM.Length == 2)
            {
                anchor.PhysicalSizeM = new Vector2(
                    manifest.Tracking.PhysicalSizeM[0],
                    manifest.Tracking.PhysicalSizeM[1]);
            }
        }
    }
}
