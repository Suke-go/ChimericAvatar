"use client";

import { FormEvent, useEffect, useMemo, useState } from "react";

import SessionWorkspace from "../../../../components/SessionWorkspace";
import {
  createPanel,
  deletePanel,
  fetchPanels,
  fetchPoster,
  fetchPosterImageUrl,
  updatePanel,
  updatePoster,
  uploadPoster,
} from "../../../../lib/api";
import { cachedSignedUrl, invalidateSignedUrl } from "../../../../lib/signed-url-cache";
import { useTaskTracker } from "../../../../lib/useTaskTracker";
import { PosterConfig, PosterPanel } from "../../../../lib/types";
import PanelCanvas from "./panel-canvas";

type Box = { x: number; y: number; width: number; height: number };

export default function PosterClient({ sessionId }: { sessionId: string }) {
  const [poster, setPoster] = useState<PosterConfig | null>(null);
  const [posterImageUrl, setPosterImageUrl] = useState<string | null>(null);
  const [posterMime, setPosterMime] = useState<string | null>(null);
  const [panels, setPanels] = useState<PosterPanel[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [posterFile, setPosterFile] = useState<File | null>(null);
  const [selectedPanelId, setSelectedPanelId] = useState<string | null>(null);
  const [pendingLabel, setPendingLabel] = useState<string>("");
  const [uploading, setUploading] = useState(false);
  const tracker = useTaskTracker();

  const selectedPanel = useMemo(
    () => panels.find((panel) => panel.id === selectedPanelId) ?? null,
    [panels, selectedPanelId],
  );

  async function load(targetSessionId: string) {
    try {
      const [posterData, panelData] = await Promise.all([
        fetchPoster(targetSessionId),
        fetchPanels(targetSessionId),
      ]);
      setPoster(posterData);
      setPanels(panelData);
      if (posterData.poster_asset_id) {
        const cacheKey = `poster-image:${targetSessionId}:${posterData.poster_asset_id}`;
        const signedUrl = await cachedSignedUrl(cacheKey, () => fetchPosterImageUrl(targetSessionId));
        // The API now returns { url: null, missing: true } when the bucket
        // has lost the object instead of throwing 502, so the upload form
        // stays usable and the operator can simply re-upload.
        setPosterImageUrl(signedUrl.url ?? null);
        setPosterMime(signedUrl.mime_type ?? null);
        if (signedUrl.missing) {
          setError(
            "The previously uploaded poster image is no longer in storage. Re-upload to restore it.",
          );
        } else {
          setError(null);
        }
      } else {
        setPosterImageUrl(null);
        setPosterMime(null);
        setError(null);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load poster");
    }
  }

  useEffect(() => {
    if (sessionId) {
      load(sessionId);
    }
  }, [sessionId]);

  async function handlePosterConfig(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!poster) return;
    const updated = await updatePoster(sessionId, {
      orientation: poster.orientation,
      qr_fallback_enabled: poster.qr_fallback_enabled,
    });
    setPoster(updated);
  }

  async function handleUpload(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!posterFile) return;
    setUploading(true);
    try {
      await tracker.track("Uploading poster…", async () => {
        await uploadPoster(sessionId, posterFile);
      });
      if (poster?.poster_asset_id) {
        invalidateSignedUrl(`poster-image:${sessionId}:${poster.poster_asset_id}`);
      }
      await load(sessionId);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Upload failed");
    } finally {
      setUploading(false);
    }
  }

  async function handleCommitDraw(box: Box) {
    const orderIndex = panels.length;
    const label = (pendingLabel.trim() || `Panel ${orderIndex + 1}`).slice(0, 80);
    try {
      const created = await createPanel(sessionId, {
        label,
        order_index: orderIndex,
        ...box,
      });
      setPanels((current) => [...current, created].sort((a, b) => a.order_index - b.order_index));
      setSelectedPanelId(created.id);
      setPendingLabel("");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to create panel");
    }
  }

  async function handleCommitMove(panelId: string, box: Box) {
    try {
      const updated = await updatePanel(sessionId, panelId, box);
      setPanels((current) => current.map((item) => (item.id === panelId ? updated : item)));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to move panel");
    }
  }

  async function handlePanelLabel(panel: PosterPanel, label: string) {
    // Skip empty labels — server requires min_length=1 and would 422.
    // The input swap below also delays commit until blur so we don't
    // hammer the API on every keystroke.
    const trimmed = label.trim();
    if (!trimmed || trimmed === panel.label) return;
    try {
      const updated = await updatePanel(sessionId, panel.id, { label: trimmed });
      setPanels((current) => current.map((item) => (item.id === panel.id ? updated : item)));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to rename panel");
    }
  }

  async function handlePanelOrder(panel: PosterPanel, order_index: number) {
    try {
      const updated = await updatePanel(sessionId, panel.id, { order_index });
      setPanels((current) =>
        current.map((item) => (item.id === panel.id ? updated : item)).sort((a, b) => a.order_index - b.order_index),
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to reorder panel");
    }
  }

  async function handleMovePanel(panel: PosterPanel, direction: -1 | 1) {
    // Sort by order_index to find the neighbor we should swap with.
    const sorted = [...panels].sort((a, b) => a.order_index - b.order_index);
    const idx = sorted.findIndex((p) => p.id === panel.id);
    const targetIdx = idx + direction;
    if (idx < 0 || targetIdx < 0 || targetIdx >= sorted.length) return;
    const neighbor = sorted[targetIdx];
    try {
      // Two-step swap to avoid the unique (session_id, order_index) clash:
      //   - bump the moving panel to a temp slot beyond current max
      //   - move neighbor into the moving panel's old slot
      //   - finally place the moving panel into the neighbor's old slot
      const moveOrder = panel.order_index;
      const neighborOrder = neighbor.order_index;
      const tempOrder = Math.max(...sorted.map((p) => p.order_index)) + 100;
      await updatePanel(sessionId, panel.id, { order_index: tempOrder });
      const movedNeighbor = await updatePanel(sessionId, neighbor.id, { order_index: moveOrder });
      const finalSelf = await updatePanel(sessionId, panel.id, { order_index: neighborOrder });
      setPanels((current) =>
        current
          .map((p) => {
            if (p.id === finalSelf.id) return finalSelf;
            if (p.id === movedNeighbor.id) return movedNeighbor;
            return p;
          })
          .sort((a, b) => a.order_index - b.order_index),
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to reorder panel");
    }
  }

  async function handleDeletePanel(panel: PosterPanel) {
    if (!window.confirm(`Delete panel "${panel.label}"?`)) return;
    try {
      await deletePanel(sessionId, panel.id);
      setPanels((current) => current.filter((item) => item.id !== panel.id));
      setSelectedPanelId(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to delete panel");
    }
  }

  // One-shot migration: panels drawn before the canvas was made image-shaped
  // were stored relative to a fixed A0 canvas with letterbox bars. Re-map them
  // to image-relative coordinates so they line up visually again.
  async function handleRefitPanels() {
    if (!posterImageUrl) {
      setError("Upload a poster first");
      return;
    }
    if (panels.length === 0) return;
    if (
      !window.confirm(
        "Re-map panel coordinates from the old A0 canvas to the actual image. Run this once if existing panels look offset.",
      )
    ) {
      return;
    }
    try {
      const img = new Image();
      img.crossOrigin = "anonymous";
      img.src = posterImageUrl;
      await img.decode();
      const imageRatio = img.naturalWidth / img.naturalHeight;
      const canvasRatio = poster?.orientation === "landscape" ? 1189 / 841 : 841 / 1189;
      let xBar = 0;
      let yBar = 0;
      if (imageRatio > canvasRatio) {
        yBar = (1 - canvasRatio / imageRatio) / 2;
      } else if (imageRatio < canvasRatio) {
        xBar = (1 - imageRatio / canvasRatio) / 2;
      }
      const xScale = 1 / Math.max(0.001, 1 - 2 * xBar);
      const yScale = 1 / Math.max(0.001, 1 - 2 * yBar);
      const updated: PosterPanel[] = [];
      for (const panel of panels) {
        const nx = Math.max(0, Math.min(1 - 0.02, (panel.x - xBar) * xScale));
        const ny = Math.max(0, Math.min(1 - 0.02, (panel.y - yBar) * yScale));
        const nw = Math.max(0.02, Math.min(1 - nx, panel.width * xScale));
        const nh = Math.max(0.02, Math.min(1 - ny, panel.height * yScale));
        const result = await updatePanel(sessionId, panel.id, { x: nx, y: ny, width: nw, height: nh });
        updated.push(result);
      }
      setPanels(updated.sort((a, b) => a.order_index - b.order_index));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to refit panels");
    }
  }

  return (
    <SessionWorkspace
      sessionId={sessionId}
      eyebrow="Poster"
      actions={<span className="badge">{poster?.poster_format ?? "A0"}</span>}
    >
      {error ? <div className="alert error">{error}</div> : null}

      <div className="grid two">
        <section className="panel">
          <div className="panel-title">
            <span className="marker" />
            CONFIG
          </div>
          {poster ? (
            <form className="stack" onSubmit={handlePosterConfig}>
              <div className="field">
                <label htmlFor="orientation">Orientation</label>
                <select
                  id="orientation"
                  value={poster.orientation}
                  onChange={(event) =>
                    setPoster((current) =>
                      current
                        ? { ...current, orientation: event.target.value as "portrait" | "landscape" }
                        : current,
                    )
                  }
                >
                  <option value="portrait">A0 portrait</option>
                  <option value="landscape">A0 landscape</option>
                </select>
              </div>
              <div className="field">
                <label>
                  <input
                    checked={poster.qr_fallback_enabled}
                    onChange={(event) =>
                      setPoster((current) =>
                        current ? { ...current, qr_fallback_enabled: event.target.checked } : current,
                      )
                    }
                    type="checkbox"
                  />{" "}
                  Enable QR fallback
                </label>
              </div>
              <p className="muted">
                {poster.physical_width_m.toFixed(3)}m × {poster.physical_height_m.toFixed(3)}m
              </p>
              <button className="button" type="submit">
                Save Poster Config
              </button>
            </form>
          ) : null}
        </section>

        <section className="panel">
          <div className="panel-title">
            <span className="marker" />
            UPLOAD
          </div>
          <form className="stack" onSubmit={handleUpload}>
            <div className="field">
              <label htmlFor="posterFile">
                Poster file (PNG / JPEG / WebP / PDF — auto-converted to PNG on server)
              </label>
              <input
                id="posterFile"
                accept="image/png,image/jpeg,image/webp,application/pdf"
                onChange={(event) => setPosterFile(event.target.files?.[0] ?? null)}
                type="file"
              />
              {posterFile && (
                <p className="hint">
                  Selected: {posterFile.name} ({Math.round(posterFile.size / 1024)} KB,{" "}
                  {posterFile.type || "unknown"})
                </p>
              )}
            </div>
            <button className="button" disabled={!posterFile || uploading} type="submit">
              {uploading ? (
                <>
                  <span className="spinner" /> Uploading…
                </>
              ) : (
                "Upload Poster"
              )}
            </button>
          </form>
        </section>
      </div>

      <section className="panel">
        <div className="panel-title">
          <span className="marker" />
          PANEL EDITOR
          <span className="panel-subtitle">// drag on poster to draw • click to select • esc to deselect</span>
        </div>
        <p className="hint" style={{ display: "none" }}>
          Drag on the poster to draw a new panel. Click a panel to select it; drag its body to move,
          drag the corner handles to resize. Press <kbd>Esc</kbd> to deselect.
        </p>
        <div className="grid" style={{ gridTemplateColumns: "minmax(0, 1fr) 320px", gap: 16 }}>
          <PanelCanvas
            panels={panels}
            selectedPanelId={selectedPanelId}
            posterImageUrl={posterImageUrl}
            posterIsPdf={posterMime === "application/pdf"}
            orientation={poster?.orientation === "landscape" ? "landscape" : "portrait"}
            onSelect={setSelectedPanelId}
            onCommitMove={handleCommitMove}
            onCommitDraw={handleCommitDraw}
          />
          <aside className="stack">
            <div className="card">
              <h3>New panel default name</h3>
              <input
                placeholder={`Panel ${panels.length + 1}`}
                value={pendingLabel}
                onChange={(event) => setPendingLabel(event.target.value)}
              />
              <p className="hint">
                Used when you draw a fresh rectangle. You can rename it after creation.
              </p>
            </div>

            {selectedPanel ? (
              <div className="card">
                <h3>Selected: {selectedPanel.label}</h3>
                <div className="field">
                  <label htmlFor="selectedLabel">Label</label>
                  <PanelLabelInput
                    panel={selectedPanel}
                    onCommit={(label) => handlePanelLabel(selectedPanel, label)}
                  />
                </div>
                <div className="field">
                  <label htmlFor="selectedOrder">Order index</label>
                  <input
                    id="selectedOrder"
                    type="number"
                    value={selectedPanel.order_index}
                    onChange={(event) => handlePanelOrder(selectedPanel, Number(event.target.value))}
                  />
                </div>
                <p className="muted">
                  x={selectedPanel.x.toFixed(3)} y={selectedPanel.y.toFixed(3)}
                  <br />
                  w={selectedPanel.width.toFixed(3)} h={selectedPanel.height.toFixed(3)}
                </p>
                <div style={{ marginTop: 12 }}>
                  <h4>Captured text</h4>
                  {selectedPanel.text_content ? (
                    <div
                      className="hint"
                      style={{
                        padding: 8,
                        background: "var(--bg-base)",
                        border: "1px solid var(--line)",
                        borderRadius: 2,
                        maxHeight: 200,
                        overflow: "auto",
                        whiteSpace: "pre-wrap",
                        fontFamily: "var(--font-mono)",
                      }}
                    >
                      {selectedPanel.text_content}
                    </div>
                  ) : (
                    <p className="hint">
                      No PDF text overlapped this panel. Upload a PDF poster to enable
                      auto-capture, or fill knowledge text manually in the Knowledge tab.
                    </p>
                  )}
                </div>
                <button
                  className="button secondary"
                  type="button"
                  style={{ marginTop: 12 }}
                  onClick={() => handleDeletePanel(selectedPanel)}
                >
                  Delete panel
                </button>
              </div>
            ) : (
              <div className="card">
                <h3>No panel selected</h3>
                <p className="hint">Drag on the poster to draw, or click an existing panel.</p>
              </div>
            )}

            <div className="card">
              <h3>All panels</h3>
              {panels.length === 0 ? (
                <p className="hint">None yet — draw your first panel on the poster.</p>
              ) : (
                <ul className="list" style={{ paddingLeft: 0, listStyle: "none" }}>
                  {[...panels]
                    .sort((a, b) => a.order_index - b.order_index)
                    .map((panel, idx, sortedArr) => (
                    <li
                      key={panel.id}
                      style={{
                        display: "flex",
                        alignItems: "center",
                        gap: 4,
                        padding: "4px 8px",
                        borderRadius: 6,
                        background:
                          panel.id === selectedPanelId ? "rgba(184,92,56,0.12)" : "transparent",
                      }}
                    >
                      <button
                        type="button"
                        className="button ghost"
                        title="Move up"
                        disabled={idx === 0}
                        onClick={(e) => {
                          e.stopPropagation();
                          handleMovePanel(panel, -1);
                        }}
                        style={{ padding: "2px 6px", minWidth: 0 }}
                      >
                        ▲
                      </button>
                      <button
                        type="button"
                        className="button ghost"
                        title="Move down"
                        disabled={idx === sortedArr.length - 1}
                        onClick={(e) => {
                          e.stopPropagation();
                          handleMovePanel(panel, 1);
                        }}
                        style={{ padding: "2px 6px", minWidth: 0 }}
                      >
                        ▼
                      </button>
                      <span
                        onClick={() => setSelectedPanelId(panel.id)}
                        style={{ cursor: "pointer", flex: 1 }}
                      >
                        <strong>{panel.order_index}.</strong> {panel.label}{" "}
                        <span className="muted">
                          ({panel.x.toFixed(2)}, {panel.y.toFixed(2)})
                        </span>
                      </span>
                    </li>
                  ))}
                </ul>
              )}
            </div>

            {panels.length > 0 && (
              <div className="card">
                <h3>Calibration</h3>
                <p className="hint" style={{ marginBottom: 8 }}>
                  Existing panels look offset against the image? Run once to re-map old A0-canvas coordinates onto the actual image.
                </p>
                <button type="button" className="button secondary" onClick={handleRefitPanels}>
                  Refit panels to image
                </button>
              </div>
            )}
          </aside>
        </div>
      </section>
    </SessionWorkspace>
  );
}

/**
 * Local-state input that only commits to the API on blur or Enter, and
 * skips empty/unchanged values. Replaces the previous onChange-per-keystroke
 * pattern that hammered /poster/panels/<id> with 422s mid-typing (server's
 * label field has min_length=1).
 */
function PanelLabelInput({
  panel,
  onCommit,
}: {
  panel: PosterPanel;
  onCommit: (label: string) => void;
}) {
  const [draft, setDraft] = useState(panel.label);
  useEffect(() => {
    setDraft(panel.label);
  }, [panel.id, panel.label]);
  return (
    <input
      id="selectedLabel"
      value={draft}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={() => onCommit(draft)}
      onKeyDown={(e) => {
        if (e.key === "Enter") (e.currentTarget as HTMLInputElement).blur();
      }}
    />
  );
}
