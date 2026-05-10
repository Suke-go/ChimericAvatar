"use client";

import { useEffect, useMemo, useRef, useState } from "react";

import SessionWorkspace from "../../../../components/SessionWorkspace";
import {
  fetchPanels,
  fetchPoster,
  fetchPosterImageUrl,
  fetchPreviewCues,
  replacePreviewCues,
} from "../../../../lib/api";
import { PosterConfig, PosterPanel, PreviewCue } from "../../../../lib/types";

type PreviewState = {
  activePanelId: string | null;
  caption: string;
  pointer: { x: number; y: number } | null;
};

const defaultCueTemplates = [
  { type: "panel_focus", start_ms: 0, duration_ms: 2400, payload: { panelLabel: "Intro" } },
  {
    type: "caption_show",
    start_ms: 300,
    duration_ms: 2200,
    payload: { text: "This panel introduces the poster narrative." },
  },
  { type: "pointer_move", start_ms: 600, duration_ms: 1200, payload: { x: 0.26, y: 0.2 } },
];

export default function PreviewClient({ sessionId }: { sessionId: string }) {
  const [poster, setPoster] = useState<PosterConfig | null>(null);
  const [posterImageUrl, setPosterImageUrl] = useState<string | null>(null);
  const [panels, setPanels] = useState<PosterPanel[]>([]);
  const [cues, setCues] = useState<PreviewCue[]>([]);
  const [status, setStatus] = useState("idle");
  const [previewState, setPreviewState] = useState<PreviewState>({
    activePanelId: null,
    caption: "",
    pointer: null,
  });
  const timersRef = useRef<number[]>([]);

  function clearPreviewTimers() {
    timersRef.current.forEach((timer) => window.clearTimeout(timer));
    timersRef.current = [];
  }

  const [posterImageMissing, setPosterImageMissing] = useState(false);

  useEffect(() => {
    if (!sessionId) return;
    Promise.all([fetchPoster(sessionId), fetchPanels(sessionId), fetchPreviewCues(sessionId)]).then(
      ([posterData, panelData, cueData]) => {
        setPoster(posterData);
        setPanels(panelData);
        setCues(cueData);
        if (posterData.poster_asset_id) {
          // The API now returns { url: null, missing: true } instead of a 502
          // when the bucket has lost the object. Surface that to the UI as a
          // re-upload prompt rather than a crash.
          fetchPosterImageUrl(sessionId)
            .then((signedUrl) => {
              setPosterImageUrl(signedUrl.url ?? null);
              setPosterImageMissing(Boolean(signedUrl.missing));
            })
            .catch((err) => {
              // Genuine errors (auth, network) — still degrade gracefully.
              console.warn("Preview: poster image url fetch failed", err);
              setPosterImageUrl(null);
              setPosterImageMissing(true);
            });
        } else {
          setPosterImageUrl(null);
          setPosterImageMissing(false);
        }
      },
    );
    return () => clearPreviewTimers();
  }, [sessionId]);

  const cueList = useMemo(
    () =>
      cues.map((cue) => ({
        type: cue.type ?? cue.cue_type ?? "",
        start_ms: cue.start_ms,
        duration_ms: cue.duration_ms ?? undefined,
        payload: cue.payload,
      })),
    [cues],
  );

  async function saveDefaultCues() {
    const resolved = defaultCueTemplates.map((cue) => {
      if (cue.type === "panel_focus") {
        const intro = panels[0];
        return {
          ...cue,
          payload: {
            panelLabel: intro?.label ?? "Panel",
            panelId: intro?.id ?? null,
          },
        };
      }
      return cue;
    });
    const stored = await replacePreviewCues(sessionId, resolved);
    setCues(stored);
  }

  function playPreview() {
    clearPreviewTimers();
    setStatus("playing");
    setPreviewState({ activePanelId: null, caption: "", pointer: null });
    const timers = cueList.map((cue) =>
      window.setTimeout(() => {
        if (cue.type === "panel_focus") {
          const target = panels.find(
            (panel) => panel.id === cue.payload.panelId || panel.label === cue.payload.panelLabel,
          );
          setPreviewState((current) => ({ ...current, activePanelId: target?.id ?? null }));
        }
        if (cue.type === "caption_show") {
          setPreviewState((current) => ({ ...current, caption: String(cue.payload.text ?? "") }));
        }
        if (cue.type === "pointer_move") {
          setPreviewState((current) => ({
            ...current,
            pointer: {
              x: Number(cue.payload.x ?? 0.5),
              y: Number(cue.payload.y ?? 0.5),
            },
          }));
        }
      }, cue.start_ms),
    );
    const endAt = Math.max(3000, ...cueList.map((cue) => cue.start_ms + (cue.duration_ms ?? 0)));
    const endTimer = window.setTimeout(() => {
      clearPreviewTimers();
      setStatus("idle");
    }, endAt + 400);
    timersRef.current = [...timers, endTimer];
  }

  return (
    <SessionWorkspace sessionId={sessionId} eyebrow="Preview" actions={<span className="badge">{status}</span>}>
      <section className="panel">
        <div className="panel-title">
          <span className="marker" />
          NON-AVATAR PREVIEW
          <span className="panel-subtitle">// panel focus · pointer · caption · timing</span>
        </div>
        <div className="row">
          <button className="button" onClick={playPreview} type="button">
            Play Preview
          </button>
          <button className="button secondary" onClick={saveDefaultCues} type="button">
            Save Default Cues
          </button>
          <span className="badge">{status}</span>
        </div>
        <div className={`poster-frame ${poster?.orientation === "landscape" ? "landscape" : ""}`}>
          {posterImageUrl ? (
            <img alt="Poster" className="poster-image" src={posterImageUrl} />
          ) : posterImageMissing ? (
            <div className="poster-image poster-image-missing" role="alert">
              <strong>Poster image is missing</strong>
              <span className="muted">
                The DB still references this image, but the storage backend reports it as
                deleted. Re-upload it from the Poster tab to restore the preview.
              </span>
            </div>
          ) : null}
          {panels.map((panel) => (
            <div
              className={`panel-box ${previewState.activePanelId === panel.id ? "active" : ""}`}
              key={panel.id}
              style={{
                left: `${panel.x * 100}%`,
                top: `${panel.y * 100}%`,
                width: `${panel.width * 100}%`,
                height: `${panel.height * 100}%`,
              }}
            >
              <span>{panel.label}</span>
            </div>
          ))}
          {previewState.pointer ? (
            <div
              className="pointer"
              style={{ left: `${previewState.pointer.x * 100}%`, top: `${previewState.pointer.y * 100}%` }}
            />
          ) : null}
          {previewState.caption ? <div className="caption">{previewState.caption}</div> : null}
        </div>
        <div className="timeline">
          {cueList.map((cue, index) => (
            <div className="timeline-item" key={`${cue.type}-${cue.start_ms}-${index}`}>
              <strong>{cue.type}</strong>
              <span className="muted">{cue.start_ms}ms</span>
            </div>
          ))}
        </div>
      </section>
    </SessionWorkspace>
  );
}
