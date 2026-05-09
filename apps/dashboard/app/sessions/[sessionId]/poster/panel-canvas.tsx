"use client";

import { CSSProperties, PointerEvent, useEffect, useRef, useState } from "react";

import { PosterPanel } from "../../../../lib/types";

type Box = { x: number; y: number; width: number; height: number };

type DragState =
  | { mode: "draw"; startX: number; startY: number }
  | { mode: "move"; panelId: string; startX: number; startY: number; baseBox: Box }
  | {
      mode: "resize";
      panelId: string;
      handle: "nw" | "ne" | "sw" | "se";
      startX: number;
      startY: number;
      baseBox: Box;
    }
  | null;

type Props = {
  panels: PosterPanel[];
  selectedPanelId: string | null;
  posterImageUrl: string | null;
  posterIsPdf?: boolean; // deprecated: server now canonicalizes to PNG
  orientation: "portrait" | "landscape";
  onSelect: (panelId: string | null) => void;
  onCommitMove: (panelId: string, box: Box) => Promise<void> | void;
  onCommitDraw: (box: Box) => Promise<void> | void;
};

const MIN_SIZE = 0.02;

function clamp01(value: number): number {
  return Math.max(0, Math.min(1, value));
}

function clampBox(box: Box): Box {
  const x = clamp01(box.x);
  const y = clamp01(box.y);
  const width = Math.max(MIN_SIZE, Math.min(1 - x, box.width));
  const height = Math.max(MIN_SIZE, Math.min(1 - y, box.height));
  return { x, y, width, height };
}

export default function PanelCanvas({
  panels,
  selectedPanelId,
  posterImageUrl,
  posterIsPdf: _posterIsPdf,
  orientation,
  onSelect,
  onCommitMove,
  onCommitDraw,
}: Props) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const [drag, setDrag] = useState<DragState>(null);
  // Live preview box during drawing/moving/resizing — committed on pointerup.
  const [previewBox, setPreviewBox] = useState<({ panelId?: string } & Box) | null>(null);
  // Match canvas aspect-ratio to the loaded image so panel coordinates (0–1)
  // correspond exactly to image content with no letterbox offset.
  const [imageRatio, setImageRatio] = useState<number | null>(null);

  // Reset measured ratio when the URL changes (new upload)
  useEffect(() => {
    setImageRatio(null);
  }, [posterImageUrl]);

  function localCoords(event: PointerEvent<HTMLDivElement>): { x: number; y: number } {
    const rect = containerRef.current!.getBoundingClientRect();
    return {
      x: clamp01((event.clientX - rect.left) / rect.width),
      y: clamp01((event.clientY - rect.top) / rect.height),
    };
  }

  function handleBackgroundPointerDown(event: PointerEvent<HTMLDivElement>) {
    if (event.button !== 0) return;
    if (event.target !== event.currentTarget) return;
    if (!posterImageUrl) return;
    event.preventDefault();
    containerRef.current?.setPointerCapture(event.pointerId);
    const { x, y } = localCoords(event);
    setDrag({ mode: "draw", startX: x, startY: y });
    setPreviewBox({ x, y, width: MIN_SIZE, height: MIN_SIZE });
    onSelect(null);
  }

  function handlePanelPointerDown(event: PointerEvent<HTMLDivElement>, panel: PosterPanel) {
    if (event.button !== 0) return;
    event.preventDefault();
    event.stopPropagation();
    containerRef.current?.setPointerCapture(event.pointerId);
    const { x, y } = localCoords(event);
    setDrag({
      mode: "move",
      panelId: panel.id,
      startX: x,
      startY: y,
      baseBox: { x: panel.x, y: panel.y, width: panel.width, height: panel.height },
    });
    setPreviewBox({ panelId: panel.id, x: panel.x, y: panel.y, width: panel.width, height: panel.height });
    onSelect(panel.id);
  }

  function handleResizePointerDown(
    event: PointerEvent<HTMLDivElement>,
    panel: PosterPanel,
    handle: "nw" | "ne" | "sw" | "se",
  ) {
    if (event.button !== 0) return;
    event.preventDefault();
    event.stopPropagation();
    containerRef.current?.setPointerCapture(event.pointerId);
    const { x, y } = localCoords(event);
    setDrag({
      mode: "resize",
      panelId: panel.id,
      handle,
      startX: x,
      startY: y,
      baseBox: { x: panel.x, y: panel.y, width: panel.width, height: panel.height },
    });
    setPreviewBox({ panelId: panel.id, x: panel.x, y: panel.y, width: panel.width, height: panel.height });
    onSelect(panel.id);
  }

  function handlePointerMove(event: PointerEvent<HTMLDivElement>) {
    if (!drag) return;
    const { x, y } = localCoords(event);
    if (drag.mode === "draw") {
      const x0 = Math.min(drag.startX, x);
      const y0 = Math.min(drag.startY, y);
      const w = Math.abs(x - drag.startX);
      const h = Math.abs(y - drag.startY);
      setPreviewBox(clampBox({ x: x0, y: y0, width: w, height: h }));
    } else if (drag.mode === "move") {
      const dx = x - drag.startX;
      const dy = y - drag.startY;
      const next = clampBox({
        x: drag.baseBox.x + dx,
        y: drag.baseBox.y + dy,
        width: drag.baseBox.width,
        height: drag.baseBox.height,
      });
      setPreviewBox({ panelId: drag.panelId, ...next });
    } else if (drag.mode === "resize") {
      const { baseBox, handle } = drag;
      let nx = baseBox.x;
      let ny = baseBox.y;
      let nw = baseBox.width;
      let nh = baseBox.height;
      if (handle === "nw") {
        nx = Math.min(x, baseBox.x + baseBox.width - MIN_SIZE);
        ny = Math.min(y, baseBox.y + baseBox.height - MIN_SIZE);
        nw = baseBox.width + (baseBox.x - nx);
        nh = baseBox.height + (baseBox.y - ny);
      } else if (handle === "ne") {
        ny = Math.min(y, baseBox.y + baseBox.height - MIN_SIZE);
        nw = Math.max(MIN_SIZE, x - baseBox.x);
        nh = baseBox.height + (baseBox.y - ny);
      } else if (handle === "sw") {
        nx = Math.min(x, baseBox.x + baseBox.width - MIN_SIZE);
        nw = baseBox.width + (baseBox.x - nx);
        nh = Math.max(MIN_SIZE, y - baseBox.y);
      } else {
        nw = Math.max(MIN_SIZE, x - baseBox.x);
        nh = Math.max(MIN_SIZE, y - baseBox.y);
      }
      const next = clampBox({ x: nx, y: ny, width: nw, height: nh });
      setPreviewBox({ panelId: drag.panelId, ...next });
    }
  }

  async function handlePointerUp(event: PointerEvent<HTMLDivElement>) {
    if (!drag || !previewBox) {
      setDrag(null);
      setPreviewBox(null);
      return;
    }
    containerRef.current?.releasePointerCapture(event.pointerId);
    const finalBox = clampBox({
      x: previewBox.x,
      y: previewBox.y,
      width: previewBox.width,
      height: previewBox.height,
    });
    const captured = drag;
    setDrag(null);
    setPreviewBox(null);
    if (captured.mode === "draw") {
      if (finalBox.width > MIN_SIZE * 1.5 && finalBox.height > MIN_SIZE * 1.5) {
        await onCommitDraw(finalBox);
      }
    } else {
      await onCommitMove(captured.panelId, finalBox);
    }
  }

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.key === "Escape") {
        setDrag(null);
        setPreviewBox(null);
        onSelect(null);
      }
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onSelect]);

  // Decide canvas aspect-ratio:
  //  - measured image ratio wins (eliminates letterbox)
  //  - else fall back to A0 portrait/landscape from the orientation setting
  const canvasStyle: CSSProperties =
    imageRatio !== null
      ? { aspectRatio: imageRatio }
      : {};

  return (
    <div
      className={`panel-canvas ${orientation === "landscape" ? "landscape" : ""}`}
      onPointerDown={handleBackgroundPointerDown}
      onPointerMove={handlePointerMove}
      onPointerUp={handlePointerUp}
      onPointerCancel={handlePointerUp}
      ref={containerRef}
      style={canvasStyle}
    >
      {posterImageUrl ? (
        // Server canonicalizes all uploads (PDF / JPEG / WebP) to PNG, so the
        // canvas always renders as a plain <img> — no browser PDF viewer
        // sidebar, no letterbox once we measure the natural ratio.
        (
          <img
            alt="Poster"
            className="poster-image"
            src={posterImageUrl}
            draggable={false}
            onLoad={(e) => {
              const img = e.currentTarget;
              if (img.naturalWidth > 0 && img.naturalHeight > 0) {
                setImageRatio(img.naturalWidth / img.naturalHeight);
              }
            }}
            style={{
              pointerEvents: "none",
              userSelect: "none",
              objectFit: imageRatio !== null ? "fill" : "contain",
            }}
          />
        )
      ) : (
        <p className="muted" style={{ position: "absolute", top: "50%", left: "50%", transform: "translate(-50%,-50%)" }}>
          Upload a poster image to start drawing panels.
        </p>
      )}

      {panels.map((panel) => {
        const live = previewBox?.panelId === panel.id ? previewBox : null;
        const box = live ?? panel;
        const isSelected = panel.id === selectedPanelId;
        return (
          <div
            key={panel.id}
            className={`panel-rect ${isSelected ? "selected" : ""}`}
            onPointerDown={(e) => handlePanelPointerDown(e, panel)}
            style={{
              left: `${box.x * 100}%`,
              top: `${box.y * 100}%`,
              width: `${box.width * 100}%`,
              height: `${box.height * 100}%`,
            }}
          >
            <span className="panel-label">{panel.label}</span>
            {isSelected ? (
              (["nw", "ne", "sw", "se"] as const).map((handle) => (
                <div
                  key={handle}
                  className={`resize-handle ${handle}`}
                  onPointerDown={(e) => handleResizePointerDown(e, panel, handle)}
                />
              ))
            ) : null}
          </div>
        );
      })}

      {previewBox && !previewBox.panelId ? (
        <div
          className="panel-rect drafting"
          style={{
            left: `${previewBox.x * 100}%`,
            top: `${previewBox.y * 100}%`,
            width: `${previewBox.width * 100}%`,
            height: `${previewBox.height * 100}%`,
          }}
        />
      ) : null}
    </div>
  );
}
