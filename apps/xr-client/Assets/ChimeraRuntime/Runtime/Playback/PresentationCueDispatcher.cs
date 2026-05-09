using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Chimera.Runtime
{
    /// <summary>
    /// Routes <see cref="PresentationCue"/> events fired by <see cref="ScriptPlayback"/>
    /// to typed UnityEvents wired up in the editor. The cue catalog mirrors what
    /// the dashboard publishes via runtime-manifest.schema.json
    /// (presentationCue.type union):
    ///
    ///   focus_panel       → camera/avatar attention shifts to a poster panel
    ///   point_panel       → avatar arm IK points at a panel
    ///   highlight_text    → caption UI emphasises a span
    ///   look_at_audience  → look-at constraint targets the viewer
    ///   poster_overlay    → spawns/hides a 3D overlay attached to a panel
    ///
    /// Unknown cue types are forwarded to OnUnknownCue so dashboards can ship
    /// new cue types without breaking older clients.
    /// </summary>
    [Serializable] public class CueEvent : UnityEvent<PresentationCue> { }

    public class PresentationCueDispatcher : MonoBehaviour
    {
        [Tooltip("Source of cues. Auto-resolved from sibling/parent if left empty.")]
        public ScriptPlayback Source;

        [Tooltip("Anchor that knows panel placement. Used by handlers that need a Transform.")]
        public PosterAnchorRoot Anchor;

        [Header("Cues")]
        public CueEvent OnFocusPanel;
        public CueEvent OnPointPanel;
        public CueEvent OnHighlightText;
        public CueEvent OnLookAtAudience;
        public CueEvent OnPosterOverlay;
        public CueEvent OnUnknownCue;

        private readonly Dictionary<string, Transform> _panelAnchors = new Dictionary<string, Transform>();

        private void Awake()
        {
            if (Source == null) Source = GetComponentInParent<ScriptPlayback>() ?? GetComponentInChildren<ScriptPlayback>();
        }

        private void OnEnable()
        {
            if (Source != null) Source.OnCueFire += HandleCue;
        }

        private void OnDisable()
        {
            if (Source != null) Source.OnCueFire -= HandleCue;
        }

        public void RegisterPanel(string panelId, Transform anchor)
        {
            if (string.IsNullOrEmpty(panelId) || anchor == null) return;
            _panelAnchors[panelId] = anchor;
        }

        public bool TryGetPanelAnchor(string panelId, out Transform anchor) =>
            _panelAnchors.TryGetValue(panelId, out anchor);

        private void HandleCue(PresentationCue cue)
        {
            if (cue == null) return;
            switch (cue.Type)
            {
                case "focus_panel":      OnFocusPanel?.Invoke(cue); break;
                case "point_panel":      OnPointPanel?.Invoke(cue); break;
                case "highlight_text":   OnHighlightText?.Invoke(cue); break;
                case "look_at_audience": OnLookAtAudience?.Invoke(cue); break;
                case "poster_overlay":   OnPosterOverlay?.Invoke(cue); break;
                default:                 OnUnknownCue?.Invoke(cue); break;
            }
        }

        /// <summary>
        /// Convenience: pulls "panelId" out of cue.Payload and resolves it to
        /// a registered panel anchor. Handlers can call this from their UnityEvent.
        /// </summary>
        public Transform ResolvePanelAnchor(PresentationCue cue)
        {
            if (cue?.Payload == null) return null;
            if (!cue.Payload.TryGetValue("panelId", out object value)) return null;
            string panelId = value as string;
            if (string.IsNullOrEmpty(panelId)) return null;
            return _panelAnchors.TryGetValue(panelId, out var t) ? t : null;
        }
    }
}
