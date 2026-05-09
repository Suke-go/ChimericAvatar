using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Events;

namespace Chimera.Runtime
{
    /// <summary>
    /// Renderer-agnostic caption surface. Subscribes to ScriptPlayback and emits
    /// the active segment's caption + citation footnotes via a UnityEvent so the
    /// concrete UI (UnityEngine.UI.Text, TextMeshProUGUI, world-space billboard)
    /// can hook in without this assembly depending on a specific text package.
    ///
    /// Citation labels prefer evidence-store metadata.citation_label
    /// (e.g. "[Smith23, p.4]"); when only a chunk id is available, the label
    /// falls back to a short "[doc-3-p4]" form derived from the chunk id.
    /// </summary>
    [Serializable] public class CaptionTextEvent : UnityEvent<string> { }

    public class CaptionPresenter : MonoBehaviour
    {
        [Tooltip("Source of segment + cue events. Auto-resolved from sibling/parent if empty.")]
        public ScriptPlayback Source;

        [Tooltip("Optional QA socket to render answer captions on top of script captions.")]
        public ChimeraSession Session;

        [Header("Output")]
        public CaptionTextEvent OnCaptionChanged;

        [Header("Formatting")]
        [Tooltip("Inserted between caption text and citation footnotes.")]
        public string CitationSeparator = "  ";

        [Tooltip("Maximum number of citation labels rendered per caption.")]
        public int MaxCitations = 4;

        // chunk_id → "[Smith23, p.4]" cache, populated as RetrievedChunks arrive.
        private readonly Dictionary<string, string> _citationLabels = new Dictionary<string, string>();
        private string _current = string.Empty;

        public string Current => _current;

        private void OnEnable()
        {
            Source ??= GetComponentInParent<ScriptPlayback>() ?? GetComponentInChildren<ScriptPlayback>();
            if (Source != null)
            {
                Source.OnSegmentStart += HandleSegmentStart;
                Source.OnSegmentEnd   += HandleSegmentEnd;
                Source.OnTrackComplete += HandleTrackComplete;
            }
            if (Session != null) Session.OnQaAnswer += HandleQaAnswer;
        }

        private void OnDisable()
        {
            if (Source != null)
            {
                Source.OnSegmentStart -= HandleSegmentStart;
                Source.OnSegmentEnd   -= HandleSegmentEnd;
                Source.OnTrackComplete -= HandleTrackComplete;
            }
            if (Session != null) Session.OnQaAnswer -= HandleQaAnswer;
        }

        public void RegisterCitationLabel(string chunkId, string label)
        {
            if (string.IsNullOrEmpty(chunkId) || string.IsNullOrEmpty(label)) return;
            _citationLabels[chunkId] = label;
        }

        private void HandleSegmentStart(ScriptSegment segment)
        {
            if (segment == null) return;
            string text = segment.Text ?? string.Empty;
            string footnote = BuildCitations(segment.EvidenceChunkIds);
            Emit(string.IsNullOrEmpty(footnote) ? text : text + CitationSeparator + footnote);
        }

        private void HandleSegmentEnd(ScriptSegment _) => Emit(string.Empty);

        private void HandleTrackComplete() => Emit(string.Empty);

        private void HandleQaAnswer(QaAnswer answer)
        {
            if (answer == null) return;
            // Cache citation labels carried by retrieved chunks for later script segments.
            if (answer.Chunks != null)
            {
                foreach (var c in answer.Chunks)
                {
                    if (c?.ChunkId == null || c.Metadata == null) continue;
                    if (c.Metadata.TryGetValue("citation_label", out object label) && label is string s)
                    {
                        _citationLabels[c.ChunkId] = s;
                    }
                }
            }
            string body = answer.DraftAnswer ?? string.Empty;
            string footnote = BuildCitations(answer.QaMatch?.EvidenceChunkIds);
            Emit(string.IsNullOrEmpty(footnote) ? body : body + CitationSeparator + footnote);
        }

        private string BuildCitations(IList<string> chunkIds)
        {
            if (chunkIds == null || chunkIds.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            int count = 0;
            foreach (string id in chunkIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (count >= MaxCitations) { sb.Append(" …"); break; }
                if (count > 0) sb.Append(' ');
                sb.Append(_citationLabels.TryGetValue(id, out string label)
                    ? label
                    : ShortenChunkId(id));
                count++;
            }
            return sb.ToString();
        }

        private static string ShortenChunkId(string chunkId)
        {
            // chunk ids look like "doc_<uuid>_p<page>_c<chunk>" in our schema; collapse
            // to "[doc-p<page>]" when we recognise the shape, otherwise the last 8 chars.
            if (chunkId.Length <= 10) return $"[{chunkId}]";
            int p = chunkId.LastIndexOf("_p", StringComparison.Ordinal);
            if (p > 0)
            {
                return $"[{chunkId.Substring(p + 1)}]";
            }
            return $"[{chunkId.Substring(chunkId.Length - 8)}]";
        }

        private void Emit(string caption)
        {
            _current = caption;
            OnCaptionChanged?.Invoke(caption);
        }
    }
}
