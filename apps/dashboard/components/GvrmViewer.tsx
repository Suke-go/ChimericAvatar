"use client";

import { useEffect, useRef, useState } from "react";

type Props = {
  /** Short-lived signed URL pointing at the .gvrm zip. */
  gvrmUrl: string;
  /** Force a fresh mount when the URL changes (rebuild scenarios). */
  cacheKey?: string;
  /** Optional override for the default 320px viewer height. */
  heightPx?: number;
};

type RuntimeGvrm = {
  update?: () => void;
  dispose?: () => Promise<void> | void;
  gs?: { viewer?: { dispose?: () => Promise<void> | void } };
};

export default function GvrmViewer({ gvrmUrl, cacheKey, heightPx = 320 }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const [phase, setPhase] = useState<"idle" | "loading" | "ready" | "error">("idle");
  const [error, setError] = useState<string | null>(null);
  const [loadRequested, setLoadRequested] = useState(false);

  useEffect(() => {
    setLoadRequested(false);
    setPhase("idle");
    setError(null);
  }, [gvrmUrl, cacheKey]);

  useEffect(() => {
    if (!gvrmUrl || !loadRequested) {
      setPhase("idle");
      return;
    }

    let cancelled = false;
    let cleanup: (() => void) | null = null;
    let loadedGvrm: RuntimeGvrm | null = null;

    setPhase("loading");
    setError(null);

    (async () => {
      try {
        const THREE = await import("three");
        const orbitMod = await import("three/addons/controls/OrbitControls.js");
        const gvrmMod = await import("../lib/gvrm-vendor/gvrm-format/gvrm.js");

        if (cancelled || !containerRef.current) return;

        const container = containerRef.current;
        const w = container.clientWidth || 480;
        const h = heightPx;

        const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false });
        renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
        renderer.setSize(w, h);
        renderer.outputColorSpace = THREE.SRGBColorSpace;
        container.replaceChildren(renderer.domElement);

        const scene = new THREE.Scene();
        scene.background = new THREE.Color(0x0a0e10);

        const camera = new THREE.PerspectiveCamera(65, w / h, 0.01, 100);
        camera.position.set(0, 0.4, 1.5);

        const controls = new orbitMod.OrbitControls(camera, renderer.domElement);
        controls.target.set(0, 0.4, 0);
        controls.enableDamping = true;
        controls.dampingFactor = 0.08;
        controls.minDistance = 0.6;
        controls.maxDistance = 6;
        controls.update();

        cleanup = () => {
          if (loadedGvrm?.dispose) loadedGvrm.dispose();
          else loadedGvrm?.gs?.viewer?.dispose?.();
          controls.dispose();
          renderer.dispose();
          if (renderer.domElement.parentNode === container) {
            container.removeChild(renderer.domElement);
          }
          scene.clear();
        };

        const gvrmApi = (gvrmMod as {
          GVRM: {
            loadStaticPreview?: (...args: unknown[]) => Promise<unknown>;
            load: (...args: unknown[]) => Promise<unknown>;
          };
        }).GVRM;
        const gvrm = await (
          gvrmApi.loadStaticPreview
            ? gvrmApi.loadStaticPreview(gvrmUrl, scene)
            : gvrmApi.load(gvrmUrl, scene, camera, renderer, "preview.gvrm")
        ) as RuntimeGvrm;
        loadedGvrm = gvrm;

        if (cancelled) {
          cleanup?.();
          return;
        }

        setPhase("ready");

        let rafId = 0;
        const animate = () => {
          if (cancelled) return;
          rafId = requestAnimationFrame(animate);

          gvrm.update?.();
          controls.update();
          renderer.render(scene, camera);
        };
        animate();

        const ro = new ResizeObserver(() => {
          const newW = container.clientWidth || w;
          renderer.setSize(newW, h);
          camera.aspect = newW / h;
          camera.updateProjectionMatrix();
        });
        ro.observe(container);

        cleanup = () => {
          cancelAnimationFrame(rafId);
          ro.disconnect();
          if (loadedGvrm?.dispose) loadedGvrm.dispose();
          else loadedGvrm?.gs?.viewer?.dispose?.();
          controls.dispose();
          renderer.dispose();
          if (renderer.domElement.parentNode === container) {
            container.removeChild(renderer.domElement);
          }
          scene.clear();
        };
      } catch (err) {
        if (cancelled) return;
        setPhase("error");
        console.error("[GvrmViewer] preview failed:", err);
        const stackHint = err instanceof Error && err.stack
          ? err.stack.split("\n").slice(0, 3).join(" | ")
          : "";
        setError(
          err instanceof Error
            ? `${err.message}${stackHint ? "  ::  " + stackHint : ""}`
            : "Failed to load avatar preview",
        );
      }
    })();

    return () => {
      cancelled = true;
      cleanup?.();
    };
  }, [gvrmUrl, cacheKey, heightPx, loadRequested]);

  return (
    <div
      style={{
        position: "relative",
        width: "100%",
        height: heightPx,
        background: "var(--bg-base, #0a0e10)",
        border: "1px solid var(--line, #244)",
        overflow: "hidden",
      }}
    >
      <div ref={containerRef} style={{ width: "100%", height: "100%" }} />
      {phase === "idle" && (
        <div
          style={{
            position: "absolute",
            inset: 0,
            display: "flex",
            flexDirection: "column",
            alignItems: "center",
            justifyContent: "center",
            gap: 10,
            color: "var(--phos, #aef)",
            background: "rgba(0,0,0,0.35)",
            fontSize: 12,
            textAlign: "center",
            padding: 16,
          }}
        >
          <button className="button" type="button" onClick={() => setLoadRequested(true)}>
            Load 3D preview
          </button>
          <span style={{ maxWidth: 420, lineHeight: 1.5, opacity: 0.85 }}>
            Large Gaussian avatars are loaded only on demand to keep the page within browser memory limits.
          </span>
        </div>
      )}
      {phase === "loading" && (
        <div
          style={{
            position: "absolute",
            inset: 0,
            display: "flex",
            alignItems: "center",
            justifyContent: "center",
            color: "var(--phos, #aef)",
            background: "rgba(0,0,0,0.4)",
            pointerEvents: "none",
            fontSize: 13,
          }}
        >
          <span className="spinner" /> &nbsp;Loading .gvrm preview...
        </div>
      )}
      {phase === "error" && (
        <div
          style={{
            position: "absolute",
            inset: 0,
            display: "flex",
            alignItems: "center",
            justifyContent: "center",
            padding: 16,
            color: "var(--coral, #f88)",
            background: "rgba(0,0,0,0.6)",
            fontSize: 12,
          }}
        >
          Preview failed: {error}
        </div>
      )}
    </div>
  );
}
