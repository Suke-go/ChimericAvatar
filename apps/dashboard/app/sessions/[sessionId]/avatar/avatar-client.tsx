"use client";

import dynamic from "next/dynamic";
import type { ChangeEvent, FormEvent, ReactNode } from "react";
import { useEffect, useMemo, useRef, useState } from "react";

import SessionWorkspace from "../../../../components/SessionWorkspace";
import { useTaskTracker } from "../../../../lib/useTaskTracker";
import {
  cloneVoiceForSession,
  enqueueAvatarBuild,
  fetchAvatarBuilds,
  fetchAvatarConfig,
  generateVoiceSample,
  updateAvatarConfig,
  uploadAvatarSource,
  uploadGvrmArchive,
  type VoiceCloneResult,
  type VoiceSample,
} from "../../../../lib/api";
import { AvatarBuildJob, AvatarConfig, AvatarRuntimeType } from "../../../../lib/types";

// GvrmViewer pulls in three.js + @pixiv/three-vrm + gaussian-splats-3d
// (~1.5MB JS). Defer-load so the avatar page only pays the cost when a
// .gvrm is actually ready to preview.
const GvrmViewer = dynamic(() => import("../../../../components/GvrmViewer"), {
  ssr: false,
  loading: () => (
    <div className="hint" style={{ padding: 16 }}>
      loading viewer...
    </div>
  ),
});

const RUNTIME_LABEL: Record<AvatarRuntimeType, string> = {
  "default-vrm": "Default human VRM",
  gvrm: "Built .gvrm (Gaussian-VRM)",
  "scaniverse-source": "Scaniverse source (build pending)",
};

export default function AvatarClient({ sessionId }: { sessionId: string }) {
  const [config, setConfig] = useState<AvatarConfig | null>(null);
  const [builds, setBuilds] = useState<AvatarBuildJob[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [info, setInfo] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [showPlacement, setShowPlacement] = useState(false);
  const [plyFile, setPlyFile] = useState<File | null>(null);
  const [vrmFile, setVrmFile] = useState<File | null>(null);
  const [gvrmFile, setGvrmFile] = useState<File | null>(null);
  const [gvrmMeta, setGvrmMeta] = useState({ display_name: "", attribution: "", license_note: "" });

  // Per-card upload progress so the status pill can flip to "uploading 42%"
  // immediately, instead of staying "missing" until the whole request finishes.
  const [plyProgress, setPlyProgress] = useState<number | null>(null);
  const [vrmProgress, setVrmProgress] = useState<number | null>(null);
  const [gvrmProgress, setGvrmProgress] = useState<number | null>(null);

  async function load() {
    try {
      const [cfg, jobs] = await Promise.all([fetchAvatarConfig(sessionId), fetchAvatarBuilds(sessionId)]);
      setConfig(cfg);
      setBuilds(jobs);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load avatar config");
    }
  }

  useEffect(() => {
    if (sessionId) void load();
  }, [sessionId]);

  // While any build job is running/queued, poll every 1.5s for progress updates.
  useEffect(() => {
    const active = builds.some((b) => b.status === "running" || b.status === "queued");
    if (!active) return;
    const handle = setInterval(() => {
      fetchAvatarBuilds(sessionId)
        .then((jobs) => {
          setBuilds(jobs);
          // If the build just succeeded, refresh the avatar config so the
          // gvrm_asset_id and runtime_type flip to "gvrm" in the UI.
          const justFinished = jobs.find(
            (j) => j.status === "succeeded" || j.status === "failed",
          );
          if (justFinished && builds.find((b) => b.id === justFinished.id)?.status !== justFinished.status) {
            fetchAvatarConfig(sessionId).then(setConfig).catch(() => {});
          }
        })
        .catch(() => {});
    }, 1500);
    return () => clearInterval(handle);
  }, [builds, sessionId]);

  const tracker = useTaskTracker();
  async function withBusy(label: string, fn: () => Promise<void>, runningLabel?: string) {
    setBusy(true);
    setError(null);
    setInfo(null);
    try {
      await tracker.track(runningLabel ?? label, fn);
      setInfo(label);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Operation failed");
    } finally {
      setBusy(false);
    }
  }

  async function handleUploadPly(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!plyFile) return;
    setPlyProgress(0);
    await withBusy(
      "Scan uploaded",
      async () => {
        try {
          const result = await uploadAvatarSource(sessionId, plyFile, (loaded, total) => {
            setPlyProgress(total > 0 ? Math.min(100, Math.round((loaded / total) * 100)) : 0);
          });
          setConfig(result.config);
          setPlyFile(null);
        } finally {
          setPlyProgress(null);
        }
      },
      "Uploading scan...",
    );
  }

  async function handleUploadVrm(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!vrmFile) return;
    setVrmProgress(0);
    await withBusy(
      "Base VRM uploaded",
      async () => {
        try {
          const result = await uploadAvatarSource(sessionId, vrmFile, (loaded, total) => {
            setVrmProgress(total > 0 ? Math.min(100, Math.round((loaded / total) * 100)) : 0);
          });
          setConfig(result.config);
          setVrmFile(null);
        } finally {
          setVrmProgress(null);
        }
      },
      "Uploading base VRM...",
    );
  }

  async function handleEnqueueBuild() {
    await withBusy(
      "Build queued",
      async () => {
        const job = await enqueueAvatarBuild(sessionId);
        // Dedupe insert 窶・if the polling effect refreshed state between
        // the request and this callback, `current` may already contain the
        // returned job. Skip the prepend in that case to avoid duplicate
        // React keys.
        setBuilds((current) =>
          current.some((b) => b.id === job.id) ? current : [job, ...current],
        );
      },
      "Queueing GVRM build...",
    );
  }

  async function handleUploadGvrm(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!gvrmFile) return;
    setGvrmProgress(0);
    await withBusy(
      ".gvrm replaced",
      async () => {
        try {
          const result = await uploadGvrmArchive(
            sessionId,
            gvrmFile,
            {
              display_name: gvrmMeta.display_name || undefined,
              attribution: gvrmMeta.attribution || undefined,
              license_note: gvrmMeta.license_note || undefined,
            },
            (loaded, total) => {
              setGvrmProgress(total > 0 ? Math.min(100, Math.round((loaded / total) * 100)) : 0);
            },
          );
          setConfig(result.config);
          setGvrmFile(null);
        } finally {
          setGvrmProgress(null);
        }
      },
      "Uploading .gvrm...",
    );
  }

  async function handleSavePlacement(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!config) return;
    await withBusy("Placement saved", async () => {
      const updated = await updateAvatarConfig(sessionId, {
        placement_position: config.placement_position,
        placement_rotation: config.placement_rotation,
        placement_scale: config.placement_scale,
        default_animation: config.default_animation,
        behavior_idle: config.behavior_idle,
        behavior_explain: config.behavior_explain,
        behavior_listening: config.behavior_listening,
        behavior_thinking: config.behavior_thinking,
        display_name: config.display_name ?? undefined,
        attribution: config.attribution ?? undefined,
        license_note: config.license_note ?? undefined,
      });
      setConfig(updated);
    });
  }

  function updatePlacementVec(field: "placement_position" | "placement_rotation", index: number, value: number) {
    if (!config) return;
    const next = [...config[field]] as [number, number, number];
    next[index] = Number.isFinite(value) ? value : 0;
    setConfig({ ...config, [field]: next });
  }

  const hasPly = !!config?.source_ply_asset_id;
  const hasVrm = !!config?.source_vrm_asset_id;
  const hasGvrm = !!config?.gvrm_asset_id || !!config?.gvrm_url;
  // Build prefers a freshly uploaded PLY/SPZ source. After a successful build
  // that upload is consumed into the .gvrm, so rebuilds can also use the
  // archive's bundled model.ply/model.vrm without storing duplicate source
  // assets.
  const buildReady = hasPly || hasGvrm;
  const latestBuild = builds[0];

  // Stepper state
  const stepUpload = hasPly || hasGvrm ? "done" : "active";
  const stepBuild = latestBuild?.status === "succeeded" ? "done" : buildReady ? "active" : "";
  const stepReady = config?.runtime_type === "gvrm" ? "done" : "";

  return (
    <SessionWorkspace
      sessionId={sessionId}
      eyebrow="Avatar"
      actions={config ? <span className="badge">{config.runtime_type}</span> : null}
    >
      {error && <div className="alert error">{error}</div>}
      {info && <div className="alert info">{info}</div>}

      {config && (
        <>
          <div className="stepper">
            <div className={`step ${stepUpload}`}>
              <span className="num">1</span>SCAN INGEST
            </div>
            <div className={`step ${stepBuild}`}>
              <span className="num">2</span>GVRM BUILD
            </div>
            <div className={`step ${stepReady}`}>
              <span className="num">3</span>READY
            </div>
          </div>
          <p className="hint" style={{ marginTop: -4, marginBottom: 16, fontSize: 12 }}>
            Already have a finished <code>.gvrm</code> file? You can skip the
            scan + build flow and{" "}
            <button
              type="button"
              onClick={() => setShowAdvanced(true)}
              style={{
                background: "none",
                border: 0,
                padding: 0,
                color: "var(--phos)",
                textDecoration: "underline",
                cursor: "pointer",
                font: "inherit",
              }}
            >
              upload it directly
            </button>{" "}
            via the Advanced section below.
          </p>

          {/* === STEP 1: ingest === */}
          <section className="panel">
            <div className="panel-title">
              <span className="marker" />
              STEP 01 // SCAN INGEST
              <span className="panel-subtitle">// PLY / SPZ scan</span>
            </div>
            <p className="hint" style={{ marginBottom: 16 }}>
              Upload your Scaniverse Gaussian Splat scan. We bundle a CC0 neutral humanoid base VRM
              (<code>fem_vroid.vrm</code>) for the skeleton - you don&apos;t need to provide one. The
              build pipeline binds your splats to the bundled rig and emits a <code>.gvrm</code> bundle
              (model.vrm + model.ply + skinning weights).
            </p>
            <p className="hint" style={{ marginBottom: 16 }}>
              <strong>PLY or SPZ both work.</strong> The server auto-converts <code>.ply</code> to{" "}
              <code>.spz</code> (~10x smaller) before bundling, so the output <code>.gvrm</code>
              fits Supabase Free&apos;s 50MB/file cap. Either upload format is fine; SPZ saves
              upload time on slow connections.
            </p>
            <div className="grid two">
              <UploadCard
                title="Scan (.ply / .spz)"
                accept=".ply,.spz"
                file={plyFile}
                progress={plyProgress}
                hasAsset={hasPly}
                disabled={busy && plyProgress === null}
                onFile={setPlyFile}
                onSubmit={handleUploadPly}
                buttonLabel="Upload scan"
                compressionHint="Tip: re-export from Scaniverse as .spz - about 10x smaller than .ply with no visible quality loss."
                consumedIntoGvrm={config?.runtime_type === "gvrm" && !hasPly}
              />
              <div className="card" style={{ background: "var(--bg-base)", opacity: 0.85 }}>
                <h3 style={{ marginTop: 0 }}>Base VRM</h3>
                <p className="hint" style={{ marginTop: 6 }}>
                  status:{" "}
                  <span className="badge cyan" style={{ display: "inline-flex", gap: 4 }}>
                    {hasVrm ? "custom override" : `bundled (${config.base_vrm_preset ?? "fem_vroid"})`}
                  </span>
                </p>
                {!hasVrm && (
                  <div className="field" style={{ marginTop: 8 }}>
                    <label style={{ fontSize: 11 }}>Bundled preset (CC0, madjin/vrm-samples)</label>
                    <div className="row" style={{ gap: 12 }}>
                      <label style={{ display: "flex", alignItems: "center", gap: 4, cursor: "pointer" }}>
                        <input
                          type="radio"
                          name="base-vrm-preset"
                          value="fem_vroid"
                          checked={(config.base_vrm_preset ?? "fem_vroid") === "fem_vroid"}
                          disabled={busy}
                          onChange={() =>
                            withBusy("Female VRM selected", async () => {
                              const updated = await updateAvatarConfig(sessionId, {
                                base_vrm_preset: "fem_vroid",
                              });
                              setConfig(updated);
                            })
                          }
                        />
                        Female (fem_vroid)
                      </label>
                      <label style={{ display: "flex", alignItems: "center", gap: 4, cursor: "pointer" }}>
                        <input
                          type="radio"
                          name="base-vrm-preset"
                          value="masc_vroid"
                          checked={config.base_vrm_preset === "masc_vroid"}
                          disabled={busy}
                          onChange={() =>
                            withBusy("Male VRM selected", async () => {
                              const updated = await updateAvatarConfig(sessionId, {
                                base_vrm_preset: "masc_vroid",
                              });
                              setConfig(updated);
                            })
                          }
                        />
                        Male (masc_vroid)
                      </label>
                    </div>
                  </div>
                )}
                <HeightScaleSlider
                  config={config}
                  busy={busy}
                  onSave={(scale) =>
                    withBusy("Avatar height saved", async () => {
                      const updated = await updateAvatarConfig(sessionId, {
                        avatar_height_scale: scale,
                      });
                      setConfig(updated);
                    })
                  }
                />
                <p className="hint" style={{ marginTop: 8, fontSize: 11 }}>
                  Need totally different proportions? See &quot;Advanced // CUSTOM VRM&quot; below.
                </p>
              </div>
            </div>
          </section>

          {/* === STEP 2: build === */}
          <section className="panel">
            <div className="panel-title">
              <span className="marker" />
              STEP 02 // GAUSSIAN-VRM BUILD
              <span className="panel-subtitle">// server-side conversion</span>
            </div>
            <div className="row" style={{ justifyContent: "space-between" }}>
              <p className="hint" style={{ margin: 0 }}>
                {buildReady
                  ? hasPly
                  ? hasVrm
                    ? "Scan ready (with custom VRM override). Queue a build to produce the .gvrm bundle."
                    : "Scan ready. Queue a build - the bundled humanoid VRM will be used as the skeleton."
                  : "No separate scan upload is stored. Queue a rebuild from the current .gvrm archive."
                  : "Upload your scan in step 1 to enable the build."}
              </p>
              <button
                className="button"
                type="button"
                disabled={busy || !buildReady}
                onClick={handleEnqueueBuild}
              >
                {busy ? (
                  <>
                    <span className="spinner" /> Working...
                  </>
                ) : latestBuild?.status === "running" ? (
                  <>
                    <span className="spinner" /> Building...
                  </>
                ) : (
                  "Queue build"
                )}
              </button>
            </div>

            {builds.length > 0 && (
              <div className="list" style={{ marginTop: 16 }}>
                {/* Dedupe by id 窶・handleEnqueueBuild's optimistic prepend can
                    race with the polling fetch, briefly leaving the same job
                    twice in state and triggering React's "duplicate key"
                    warning. Keep the first occurrence (newest) of each id. */}
                {builds
                  .filter((job, i, arr) => arr.findIndex((j) => j.id === job.id) === i)
                  .slice(0, 4)
                  .map((job) => (
                    <div className="card" key={job.id}>
                      <div className="row" style={{ justifyContent: "space-between" }}>
                        <span className="mono">{job.id.slice(0, 8)}</span>
                        <span className={`badge ${badgeForStatus(job.status)}`}>{job.status}</span>
                      </div>
                      <div className="row" style={{ marginTop: 6, justifyContent: "space-between" }}>
                        <span className="hint">{job.created_at?.replace("T", " ").slice(0, 19)}</span>
                        <span className="hint">{job.progress}%</span>
                      </div>
                      {job.error && <p className="hint" style={{ color: "var(--coral)", marginTop: 6 }}>{job.error}</p>}
                    </div>
                  ))}
              </div>
            )}
          </section>

          {/* === Preview === Live in-browser render of the built .gvrm.
              Only visible after a successful build flips runtime_type to
              "gvrm" and the server hands back a signed URL. */}
          {config.runtime_type === "gvrm" && config.gvrm_url && (
            <PreviewPanel config={config} />
          )}

          {/* === Voice === ElevenLabs voice ID input + record / clone /
              upload + preview audio. Cached server-side per
              (voice_id, text, lang) so repeat previews don't burn
              ElevenLabs credits. */}
          <VoicePanel
            sessionId={sessionId}
            config={config}
            busy={busy}
            onSaveVoiceId={(voiceId) =>
              withBusy("Voice ID saved", async () => {
                const updated = await updateAvatarConfig(sessionId, { voice_id: voiceId });
                setConfig(updated);
              })
            }
            onCloned={(result) => {
              if (result.avatar_voice_id_set) {
                // Refresh the local config view so the new voice ID shows
                // up immediately in the input + drives the preview.
                setConfig((current) =>
                  current ? { ...current, voice_id: result.voice_id } : current,
                );
              }
            }}
          />

          {/* === STEP 3: ready / placement === */}
          <section className="panel">
            <div
              className="panel-title"
              style={{ cursor: "pointer" }}
              onClick={() => setShowPlacement((v) => !v)}
            >
              <span className="marker" />
              STEP 03 // PLACEMENT & BEHAVIORS
              <span className="panel-subtitle">// {showPlacement ? "笆ｼ" : "笆ｶ"}  click to {showPlacement ? "collapse" : "expand"}</span>
            </div>
            {showPlacement && (
              <form onSubmit={handleSavePlacement} className="grid two">
                <div className="stack">
                  <div className="field">
                    <label>Position (x y z, meters from poster anchor)</label>
                    <div className="row">
                      {[0, 1, 2].map((i) => (
                        <input
                          key={`pos-${i}`}
                          type="number"
                          step="0.01"
                          value={config.placement_position[i]}
                          onChange={(e) => updatePlacementVec("placement_position", i, parseFloat(e.target.value))}
                        />
                      ))}
                    </div>
                  </div>
                  <div className="field">
                    <label>Rotation (deg, x y z)</label>
                    <div className="row">
                      {[0, 1, 2].map((i) => (
                        <input
                          key={`rot-${i}`}
                          type="number"
                          step="1"
                          value={config.placement_rotation[i]}
                          onChange={(e) => updatePlacementVec("placement_rotation", i, parseFloat(e.target.value))}
                        />
                      ))}
                    </div>
                  </div>
                  <div className="field">
                    <label>Scale</label>
                    <input
                      type="number"
                      step="0.01"
                      min="0.05"
                      max="5"
                      value={config.placement_scale}
                      onChange={(e) => setConfig({ ...config, placement_scale: parseFloat(e.target.value) || 1 })}
                    />
                  </div>
                </div>
                <div className="stack">
                  <div className="field">
                    <label>Default animation</label>
                    <input
                      value={config.default_animation}
                      onChange={(e) => setConfig({ ...config, default_animation: e.target.value })}
                    />
                  </div>
                  {(["behavior_idle", "behavior_explain", "behavior_listening", "behavior_thinking"] as const).map(
                    (field) => (
                      <div key={field} className="field">
                        <label>{field.replace("behavior_", "Behavior - ")}</label>
                        <input
                          value={config[field]}
                          onChange={(e) => setConfig({ ...config, [field]: e.target.value })}
                        />
                      </div>
                    ),
                  )}
                  <div className="field">
                    <label>Display name</label>
                    <input
                      value={config.display_name ?? ""}
                      onChange={(e) => setConfig({ ...config, display_name: e.target.value })}
                    />
                  </div>
                  <div className="field">
                    <label>Attribution</label>
                    <input
                      value={config.attribution ?? ""}
                      onChange={(e) => setConfig({ ...config, attribution: e.target.value })}
                    />
                  </div>
                  <div className="field">
                    <label>License note</label>
                    <input
                      value={config.license_note ?? ""}
                      onChange={(e) => setConfig({ ...config, license_note: e.target.value })}
                    />
                  </div>
                  <button type="submit" className="button" disabled={busy} style={{ alignSelf: "flex-start" }}>
                    Save
                  </button>
                </div>
              </form>
            )}
          </section>

          {/* === Advanced: custom VRM override + pre-built .gvrm upload === */}
          <section className="panel">
            <div
              className="panel-title"
              style={{ cursor: "pointer" }}
              onClick={() => setShowAdvanced((v) => !v)}
            >
              <span className="marker" />
              ADVANCED // CUSTOM VRM / PRE-BUILT .GVRM
              <span className="panel-subtitle">// {showAdvanced ? "笆ｼ" : "笆ｶ"}</span>
            </div>
            {showAdvanced && (
              <>
                <p className="hint" style={{ marginBottom: 12 }}>
                  <strong>Custom VRM override</strong>: replace the bundled <code>fem_vroid.vrm</code> skeleton
                  with your own humanoid VRM. Useful when proportions matter (height &gt; 180cm, child, etc.).
                  Matched against your scan&apos;s splats during the build.
                </p>
                <UploadCard
                  title="Custom base VRM"
                  accept=".vrm"
                  file={vrmFile}
                  progress={vrmProgress}
                  hasAsset={hasVrm}
                  disabled={busy && vrmProgress === null}
                  onFile={setVrmFile}
                  onSubmit={handleUploadVrm}
                  buttonLabel="Upload custom VRM"
                  consumedIntoGvrm={config?.runtime_type === "gvrm" && !hasVrm}
                />
                <p className="hint" style={{ marginTop: 24, marginBottom: 12 }}>
                  <strong>Pre-built .gvrm</strong>: skip the build entirely by uploading a zip of
                  model.vrm + model.ply + data.json.
                </p>
                <form onSubmit={handleUploadGvrm} className="stack">
                  <input
                    type="file"
                    accept=".gvrm,application/zip"
                    onChange={(e) => setGvrmFile(e.target.files?.[0] ?? null)}
                  />
                  <div className="field">
                    <label>Display name</label>
                    <input
                      value={gvrmMeta.display_name}
                      onChange={(e) => setGvrmMeta({ ...gvrmMeta, display_name: e.target.value })}
                    />
                  </div>
                  <div className="field">
                    <label>Attribution</label>
                    <input
                      value={gvrmMeta.attribution}
                      onChange={(e) => setGvrmMeta({ ...gvrmMeta, attribution: e.target.value })}
                    />
                  </div>
                  <div className="field">
                    <label>License note</label>
                    <input
                      value={gvrmMeta.license_note}
                      onChange={(e) => setGvrmMeta({ ...gvrmMeta, license_note: e.target.value })}
                    />
                  </div>
                  <button type="submit" className="button" disabled={busy || !gvrmFile}>
                    Upload .gvrm
                  </button>
                </form>
              </>
            )}
          </section>
        </>
      )}
    </SessionWorkspace>
  );
}

function badgeForStatus(s: AvatarBuildJob["status"]): string {
  if (s === "succeeded") return "";
  if (s === "running") return "cyan";
  if (s === "failed") return "coral";
  return "amber";
}

function PreviewPanel({ config }: { config: AvatarConfig }) {
  // Height-rebuild detection: data.json was baked with the AvatarConfig's
  // height_scale at build time (now 竊・metadata.modelScale). If the slider
  // moved since, the live preview is stale until a rebuild runs.
  const builtScale = useMemo(() => {
    const meta = config.metadata;
    if (!meta || typeof meta !== "object") return null;
    const record = meta as Record<string, unknown>;
    const v = record._builtHeightScale ?? record.modelScale;
    return typeof v === "number" ? v : null;
  }, [config.metadata]);
  const heightDrift = builtScale !== null && Math.abs(builtScale - config.avatar_height_scale) > 0.005;

  // Stub fallback: server.preprocess fell back to the legacy stub schema
  // (no real splat竊鍛one bindings). Renderer can still display but won't
  // animate properly. Surface as a yellow warning so users rebuild.
  const isStub = config.gvrm_build_version === "stub-v1";
  const isServer = config.gvrm_build_version?.startsWith("server-") ?? false;

  // Stale-build detection: compare against the server's CURRENT preprocess
  // version. Mismatch means our binding algorithm has been upgraded since
  // this .gvrm was built and the live preview won't match what new builds
  // produce 窶・surface as a banner that explicitly recommends rebuilding.
  // Only fires when both sides report a version (so we don't show "stale"
  // for legacy assets that pre-date the build_version column).
  const isStaleBuild = !!(
    config.gvrm_build_version
    && config.expected_build_version
    && config.gvrm_build_version !== config.expected_build_version
  );
  const staleAssetReason = isStaleBuild
    ? `Bindings were built with ${config.gvrm_build_version}, current server expects ${config.expected_build_version}.`
    : heightDrift
      ? `Avatar height changed from ${builtScale?.toFixed(2)}x to ${config.avatar_height_scale.toFixed(2)}x.`
      : isStub
        ? "This asset uses stub bindings, so the GVRM preview is not reliable."
        : null;
  const hasCurrentGvrm = !!config.gvrm_url && !staleAssetReason;
  const hasStaleGvrm = !!config.gvrm_url && !!staleAssetReason;
  const currentGvrmUrl: string | undefined = hasCurrentGvrm && config.gvrm_url
    ? config.gvrm_url
    : undefined;

  return (
    <section className="panel">
      <div className="panel-title">
        <span className="marker" />
        PREVIEW
        <span className="panel-subtitle">// in-browser render of the built .gvrm</span>
      </div>
      {isStaleBuild && (
        <div
          className="row"
          style={{
            marginBottom: 12,
            padding: "10px 12px",
            background: "rgba(255, 200, 0, 0.08)",
            border: "1px solid rgba(255, 200, 0, 0.4)",
            borderRadius: 6,
            fontSize: 12,
            lineHeight: 1.5,
          }}
        >
          <strong style={{ color: "#ffc800" }}>outdated build</strong>
          <span style={{ marginLeft: 8 }}>
            This avatar was built with <code>{config.gvrm_build_version}</code>, but the server now expects{" "}
            <code>{config.expected_build_version}</code>. Rebuild the avatar before using the live preview.
          </span>
        </div>
      )}
      <div className="row" style={{ marginBottom: 12, justifyContent: "space-between", flexWrap: "wrap", gap: 8 }}>
        <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
          {isServer && !isStaleBuild && (
            <span className="badge">bindings: {config.gvrm_build_version}</span>
          )}
          {isServer && isStaleBuild && (
            <span className="badge amber">bindings: {config.gvrm_build_version} (stale)</span>
          )}
          {isStub && (
            <span className="badge amber" title="Server preprocess fell back to stub bindings (empty splatBoneIndices). Avatar will render as a static splat cloud with no skinning. Rebuild to retry.">
              bindings: stub fallback
            </span>
          )}
          {heightDrift && (
            <span className="badge amber">
              rebuild needed (height changed: built {builtScale?.toFixed(2)}x to now {config.avatar_height_scale.toFixed(2)}x)
            </span>
          )}
        </div>
        {currentGvrmUrl && (
          <a
            className="button"
            href={currentGvrmUrl}
            download={`session-${config.session_id.slice(0, 8)}.gvrm`}
            style={{ fontSize: 12 }}
          >
            Download .gvrm
          </a>
        )}
      </div>
      {currentGvrmUrl && (
        <GvrmViewer gvrmUrl={currentGvrmUrl} cacheKey={config.gvrm_asset_id ?? undefined} />
      )}
      {hasStaleGvrm && (
        <div
          style={{
            height: 320,
            display: "flex",
            alignItems: "center",
            justifyContent: "center",
            padding: 20,
            background: "var(--bg-base, #0a0e10)",
            border: "1px solid rgba(255, 200, 0, 0.35)",
            color: "var(--amber, #ffc800)",
            fontSize: 12,
            lineHeight: 1.6,
            textAlign: "center",
          }}
        >
          This .gvrm is from an older build and is hidden until rebuild. {staleAssetReason}
        </div>
      )}
      <p className="hint" style={{ marginTop: 8, fontSize: 11 }}>
        {staleAssetReason
          ? "Run Build Avatar to create the current .gvrm for this configuration."
          : "Drag to rotate. Scroll to zoom."}
      </p>
    </section>
  );
}

// Avatar PLY/SPZ/VRM uploads are routed to the API server's local volume
// (LOCAL_AVATAR_KINDS) rather than Supabase Storage, so the 50MB Free-tier
// per-file cap does NOT apply. The server's MAX_AVATAR_UPLOAD_BYTES is 256MB.
const SOFT_WARN_BYTES = 100 * 1024 * 1024;    // 100MB 竊・"upload may take a while"
const HARD_REJECT_BYTES = 256 * 1024 * 1024;  // 256MB 竊・matches server cap

// fem_vroid.vrm's base height in cm. Used to translate the multiplier slider
// into a "your effective height" preview number for the user.
const BASE_VRM_HEIGHT_CM = 165;

function HeightScaleSlider({
  config,
  busy,
  onSave,
}: {
  config: AvatarConfig;
  busy: boolean;
  onSave: (scale: number) => void;
}) {
  const [draft, setDraft] = useState<number>(config.avatar_height_scale ?? 1.0);
  useEffect(() => {
    setDraft(config.avatar_height_scale ?? 1.0);
  }, [config.avatar_height_scale]);
  const dirty = Math.abs(draft - (config.avatar_height_scale ?? 1.0)) > 0.001;
  const effectiveCm = Math.round(BASE_VRM_HEIGHT_CM * draft);
  return (
    <div className="field" style={{ marginTop: 12 }}>
      <label style={{ display: "flex", justifyContent: "space-between" }}>
        <span>Avatar height</span>
        <span className="hint mono">
          {draft.toFixed(2)}x - {effectiveCm} cm
        </span>
      </label>
      <input
        type="range"
        min={0.7}
        max={1.3}
        step={0.01}
        value={draft}
        onChange={(e) => setDraft(parseFloat(e.target.value))}
        style={{ width: "100%" }}
      />
      <div className="row" style={{ justifyContent: "space-between", marginTop: 4 }}>
        <span className="hint" style={{ fontSize: 11 }}>
          0.7x (115cm)
        </span>
        <span className="hint" style={{ fontSize: 11 }}>
          1.3x (215cm)
        </span>
      </div>
      {dirty && (
        <button
          className="button"
          type="button"
          disabled={busy}
          onClick={() => onSave(draft)}
          style={{ marginTop: 8, width: "100%" }}
        >
          Save height ({draft.toFixed(2)}x)
        </button>
      )}
    </div>
  );
}

type UploadCardProps = {
  title: string;
  accept: string;
  file: File | null;
  progress: number | null;
  hasAsset: boolean;
  disabled: boolean;
  onFile: (f: File | null) => void;
  onSubmit: (e: FormEvent<HTMLFormElement>) => void;
  buttonLabel: string;
  /** Optional pointer to alternative compressed format if user picked the heavy one. */
  compressionHint?: string;
  /** Set when a successful build has consumed this source into the current .gvrm. */
  consumedIntoGvrm?: boolean;
};

function UploadCard({
  title,
  accept,
  file,
  progress,
  hasAsset,
  disabled,
  onFile,
  onSubmit,
  buttonLabel,
  compressionHint,
  consumedIntoGvrm,
}: UploadCardProps) {
  const uploading = progress !== null;
  const sizeMB = file ? file.size / (1024 * 1024) : 0;
  const tooBig = file ? file.size > HARD_REJECT_BYTES : false;
  const big = file ? file.size > SOFT_WARN_BYTES && !tooBig : false;

  let badgeClass = "muted";
  let badgeContent: ReactNode = "missing";
  if (uploading) {
    badgeClass = "amber";
    badgeContent = (
      <>
        <span className="spinner" /> uploading {progress}%
      </>
    );
  } else if (hasAsset) {
    badgeClass = "";
    badgeContent = "ready";
  } else if (consumedIntoGvrm) {
    badgeClass = "cyan";
    badgeContent = "bundled into .gvrm";
  }

  return (
    <div className="card">
      <h3>{title}</h3>
      <form onSubmit={onSubmit}>
        <input
          type="file"
          accept={accept}
          disabled={uploading}
          onChange={(e) => onFile(e.target.files?.[0] ?? null)}
        />
        {file && (
          <p className="hint" style={{ marginTop: 6 }}>
            {file.name} - {sizeMB.toFixed(1)} MB
          </p>
        )}
        {tooBig && (
          <div className="alert error" style={{ marginTop: 8 }}>
            File is {sizeMB.toFixed(0)} MB - over the 256 MB server upload cap.
            {compressionHint ? ` ${compressionHint}` : ""}
          </div>
        )}
        {big && (
          <div className="alert warn" style={{ marginTop: 8 }}>
            {sizeMB.toFixed(0)} MB - upload may take a while on slow connections.
            {compressionHint ? ` ${compressionHint}` : ""}
          </div>
        )}
        {uploading && (
          <div
            style={{
              marginTop: 8,
              height: 4,
              background: "var(--bg-base)",
              border: "1px solid var(--line)",
              overflow: "hidden",
            }}
          >
            <div
              style={{
                width: `${progress ?? 0}%`,
                height: "100%",
                background: "var(--phos)",
                transition: "width 120ms linear",
              }}
            />
          </div>
        )}
        <button
          className="button"
          type="submit"
          disabled={disabled || !file || uploading || tooBig}
          style={{ marginTop: 12 }}
        >
          {uploading ? (
            <>
              <span className="spinner" /> Uploading {progress}%
            </>
          ) : (
            buttonLabel
          )}
        </button>
      </form>
      <p className="hint" style={{ marginTop: 8 }}>
        status:{" "}
        <span
          className={`badge ${badgeClass}`}
          style={{ display: "inline-flex", alignItems: "center", gap: 4 }}
        >
          {badgeContent}
        </span>
      </p>
    </div>
  );
}

type VoiceMode = "paste" | "record" | "upload";

const VOICE_MODE_LABELS: Record<VoiceMode, string> = {
  paste: "Paste voice ID",
  record: "Record",
  upload: "Upload audio",
};

const RECORD_HARD_LIMIT_SEC = 180;

/**
 * Voice ID + in-app cloning panel.
 *
 * Three modes:
 *   - **paste**: paste a voice ID created on ElevenLabs Voice Lab
 *   - **record**: capture audio in-browser (MediaRecorder), then clone
 *   - **upload**: pick an audio file (mp3/wav/m4a/webm/...) and clone
 *
 * The session's voice_id persists in AvatarConfig (overrides
 * ELEVENLABS_DEFAULT_VOICE_ID for this session only). Previews are cached
 * server-side keyed by (voice_id, text, lang) so retrying a preview never
 * burns ElevenLabs API credits beyond the first call.
 *
 * Cloning forwards audio to the API, which calls ElevenLabs IVC
 * server-side and writes a VoiceConsent row. The dashboard never touches
 * the ElevenLabs API key directly.
 */
function VoicePanel({
  sessionId,
  config,
  busy,
  onSaveVoiceId,
  onCloned,
}: {
  sessionId: string;
  config: AvatarConfig;
  busy: boolean;
  onSaveVoiceId: (voiceId: string) => void;
  onCloned?: (result: VoiceCloneResult) => void;
}) {
  const [mode, setMode] = useState<VoiceMode>("paste");
  const [voiceIdDraft, setVoiceIdDraft] = useState(config.voice_id ?? "");
  const [text, setText] = useState("");
  const [sample, setSample] = useState<VoiceSample | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [generating, setGenerating] = useState(false);

  useEffect(() => {
    setVoiceIdDraft(config.voice_id ?? "");
  }, [config.voice_id]);

  const dirty = (config.voice_id ?? "") !== voiceIdDraft.trim();
  const trimmedId = voiceIdDraft.trim();

  async function handleGenerate() {
    if (!trimmedId) {
      setError("Enter an ElevenLabs voice ID first");
      return;
    }
    setGenerating(true);
    setError(null);
    try {
      const result = await generateVoiceSample({
        voice_id: trimmedId,
        text: text.trim() || undefined,
        language: "ja",
      });
      setSample(result);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Voice preview failed");
    } finally {
      setGenerating(false);
    }
  }

  // After a successful clone we want to immediately verify by playing the
  // freshly-cloned voice — operators almost always want the preview next.
  async function previewClonedVoice(voiceId: string) {
    try {
      const result = await generateVoiceSample({
        voice_id: voiceId,
        text: text.trim() || undefined,
        language: "ja",
      });
      setSample(result);
    } catch {
      // Preview failure shouldn't fail the clone flow itself.
    }
  }

  return (
    <section className="panel">
      <div className="panel-title">
        <span className="marker" />
        VOICE
        <span className="panel-subtitle">// ElevenLabs voice ID + clone + cached preview</span>
      </div>
      <div className="row" style={{ gap: 8, marginBottom: 12 }}>
        {(Object.keys(VOICE_MODE_LABELS) as VoiceMode[]).map((m) => (
          <button
            key={m}
            type="button"
            className={`button ${mode === m ? "" : "secondary"}`}
            onClick={() => {
              setMode(m);
              setError(null);
            }}
          >
            {VOICE_MODE_LABELS[m]}
          </button>
        ))}
      </div>

      {mode === "paste" && (
        <p className="hint" style={{ marginBottom: 12 }}>
          Already have a voice on{" "}
          <a href="https://elevenlabs.io/app/voice-lab" target="_blank" rel="noreferrer" style={{ color: "var(--phos)" }}>
            ElevenLabs Voice Lab
          </a>
          ? Paste the voice ID below. Re-clicking <em>Generate preview</em> reuses cached audio so no
          extra ElevenLabs credits are spent.
        </p>
      )}
      {mode === "record" && (
        <p className="hint" style={{ marginBottom: 12 }}>
          Record up to {RECORD_HARD_LIMIT_SEC}s of speech. The audio stays in your browser until you
          click <em>Clone voice</em>; the server then forwards it to ElevenLabs Instant Voice Cloning
          and saves your consent.
        </p>
      )}
      {mode === "upload" && (
        <p className="hint" style={{ marginBottom: 12 }}>
          Upload one or more audio files (mp3 / wav / m4a / webm / flac). Up to 10 files, 10 MB each.
          Server forwards them to ElevenLabs IVC; the consent label you provide is recorded for
          compliance.
        </p>
      )}

      <div className="grid two">
        <div className="stack">
          <div className="field">
            <label>ElevenLabs Voice ID</label>
            <input
              value={voiceIdDraft}
              placeholder="e.g. 21m00Tcm4TlvDq8ikWAM"
              onChange={(e) => setVoiceIdDraft(e.target.value)}
            />
          </div>
          <div className="row">
            <button
              type="button"
              className="button"
              disabled={busy || !dirty}
              onClick={() => onSaveVoiceId(trimmedId)}
            >
              {dirty ? "Save voice ID" : "Saved"}
            </button>
            <span className="hint" style={{ fontSize: 11 }}>
              Empty = use server default
            </span>
          </div>

          {mode === "record" && (
            <RecordCloneForm
              sessionId={sessionId}
              onCloned={(result) => {
                setVoiceIdDraft(result.voice_id);
                onCloned?.(result);
                void previewClonedVoice(result.voice_id);
              }}
            />
          )}
          {mode === "upload" && (
            <UploadCloneForm
              sessionId={sessionId}
              onCloned={(result) => {
                setVoiceIdDraft(result.voice_id);
                onCloned?.(result);
                void previewClonedVoice(result.voice_id);
              }}
            />
          )}

          <div className="field">
            <label>Preview text (optional)</label>
            <textarea
              rows={3}
              value={text}
              placeholder="Leave empty for the default Japanese phrase"
              onChange={(e) => setText(e.target.value)}
            />
          </div>
          <button
            type="button"
            className="button"
            disabled={generating || !trimmedId}
            onClick={handleGenerate}
            style={{ alignSelf: "flex-start" }}
          >
            {generating ? (
              <>
                <span className="spinner" /> Generating...
              </>
            ) : (
              "Generate preview"
            )}
          </button>
          {error && <p className="hint" style={{ color: "var(--coral)" }}>{error}</p>}
        </div>
        <div className="stack">
          {sample ? (
            <>
              <p className="hint">
                <span className={`badge ${sample.cached ? "" : "amber"}`}>
                  {sample.cached ? "✓ cached (no API call)" : "✱ fresh (API call made)"}
                </span>
              </p>
              <audio controls src={sample.audio_url ?? undefined} style={{ width: "100%" }} />
              <p className="hint" style={{ fontSize: 11 }}>
                {sample.text}
              </p>
              <p className="hint" style={{ fontSize: 11 }}>
                model: <code>{sample.model_id}</code>
              </p>
            </>
          ) : (
            <p className="hint" style={{ marginTop: 32, textAlign: "center", opacity: 0.6 }}>
              Record / upload / paste a voice, then generate a preview.
            </p>
          )}
        </div>
      </div>
    </section>
  );
}

/**
 * In-browser voice recorder. Uses MediaRecorder to capture the default mic
 * stream; we don't pin a codec because Chromium / Firefox / Safari each
 * pick a different default and ElevenLabs accepts all the common ones.
 *
 * Hard-stops at RECORD_HARD_LIMIT_SEC so a stuck tab doesn't quietly
 * collect minutes of audio.
 */
function RecordCloneForm({
  sessionId,
  onCloned,
}: {
  sessionId: string;
  onCloned: (result: VoiceCloneResult) => void;
}) {
  const [recording, setRecording] = useState(false);
  const [elapsed, setElapsed] = useState(0);
  const [blob, setBlob] = useState<Blob | null>(null);
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  const [voiceName, setVoiceName] = useState("");
  const [consent, setConsent] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const recorderRef = useRef<MediaRecorder | null>(null);
  const streamRef = useRef<MediaStream | null>(null);
  const chunksRef = useRef<Blob[]>([]);
  const timerRef = useRef<number | null>(null);

  // Always release the mic + revoke the preview URL on unmount, even if
  // the user navigated away mid-recording.
  useEffect(() => {
    return () => {
      if (timerRef.current !== null) window.clearInterval(timerRef.current);
      streamRef.current?.getTracks().forEach((t) => t.stop());
      if (previewUrl) URL.revokeObjectURL(previewUrl);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function startRecording() {
    setError(null);
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      streamRef.current = stream;
      const recorder = new MediaRecorder(stream);
      chunksRef.current = [];
      recorder.ondataavailable = (event) => {
        if (event.data && event.data.size > 0) chunksRef.current.push(event.data);
      };
      recorder.onstop = () => {
        const mime = recorder.mimeType || "audio/webm";
        const merged = new Blob(chunksRef.current, { type: mime });
        chunksRef.current = [];
        if (previewUrl) URL.revokeObjectURL(previewUrl);
        const nextUrl = URL.createObjectURL(merged);
        setPreviewUrl(nextUrl);
        setBlob(merged);
        streamRef.current?.getTracks().forEach((t) => t.stop());
        streamRef.current = null;
      };
      recorder.start();
      recorderRef.current = recorder;
      setRecording(true);
      setElapsed(0);
      timerRef.current = window.setInterval(() => {
        setElapsed((prev) => {
          const next = prev + 1;
          if (next >= RECORD_HARD_LIMIT_SEC) {
            stopRecording();
          }
          return next;
        });
      }, 1000);
    } catch (err) {
      setError(
        err instanceof Error
          ? `Microphone access failed: ${err.message}`
          : "Microphone access failed",
      );
    }
  }

  function stopRecording() {
    if (timerRef.current !== null) {
      window.clearInterval(timerRef.current);
      timerRef.current = null;
    }
    const recorder = recorderRef.current;
    if (recorder && recorder.state !== "inactive") {
      recorder.stop();
    }
    setRecording(false);
  }

  function discardTake() {
    if (previewUrl) URL.revokeObjectURL(previewUrl);
    setPreviewUrl(null);
    setBlob(null);
  }

  async function submitClone() {
    if (!blob || !voiceName.trim() || !consent) return;
    setSubmitting(true);
    setError(null);
    try {
      // Pick a sensible filename so this take is identifiable in the
      // ElevenLabs voice editor later.
      const ext = (blob.type.split("/")[1] || "webm").split(";")[0];
      const filename = `${voiceName.trim().replace(/\s+/g, "_")}-${Date.now()}.${ext}`;
      const file = new File([blob], filename, { type: blob.type });
      const result = await cloneVoiceForSession(sessionId, {
        name: voiceName.trim(),
        consent_label: "Recorded in dashboard with explicit consent checkbox.",
        files: [file],
      });
      onCloned(result);
      discardTake();
      setVoiceName("");
      setConsent(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Voice clone failed");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="stack" style={{ gap: 8 }}>
      <div className="field">
        <label>Voice name</label>
        <input
          value={voiceName}
          placeholder="e.g. Presenter A — CHI 2026"
          maxLength={80}
          onChange={(e) => setVoiceName(e.target.value)}
        />
      </div>
      <div className="row" style={{ alignItems: "center", gap: 12 }}>
        {!recording && !blob && (
          <button type="button" className="button" onClick={startRecording}>
            ● Start recording
          </button>
        )}
        {recording && (
          <button type="button" className="button" onClick={stopRecording}>
            ■ Stop ({elapsed}s)
          </button>
        )}
        {!recording && blob && (
          <>
            <button type="button" className="button secondary" onClick={discardTake}>
              ✕ Discard take
            </button>
            <button type="button" className="button secondary" onClick={startRecording}>
              ↻ Record again
            </button>
          </>
        )}
        {recording && (
          <span className="hint" style={{ fontSize: 11 }}>
            max {RECORD_HARD_LIMIT_SEC}s
          </span>
        )}
      </div>
      {previewUrl && (
        <audio controls src={previewUrl} style={{ width: "100%" }} />
      )}
      <label className="row" style={{ gap: 6, alignItems: "flex-start", fontSize: 12 }}>
        <input
          type="checkbox"
          checked={consent}
          onChange={(e) => setConsent(e.target.checked)}
          style={{ marginTop: 3 }}
        />
        <span>
          I confirm I am the owner of this voice or have explicit permission to clone it for this
          presentation. Consent is recorded server-side.
        </span>
      </label>
      <button
        type="button"
        className="button"
        disabled={!blob || !voiceName.trim() || !consent || submitting}
        onClick={submitClone}
        style={{ alignSelf: "flex-start" }}
      >
        {submitting ? (
          <>
            <span className="spinner" /> Cloning...
          </>
        ) : (
          "Clone voice"
        )}
      </button>
      {error && <p className="hint" style={{ color: "var(--coral)" }}>{error}</p>}
    </div>
  );
}

/**
 * Audio file upload → IVC. Mirrors RecordCloneForm but the audio comes
 * from a file picker. Multi-select supported because IVC happily takes
 * several samples and the resulting voice is more stable.
 */
function UploadCloneForm({
  sessionId,
  onCloned,
}: {
  sessionId: string;
  onCloned: (result: VoiceCloneResult) => void;
}) {
  const [files, setFiles] = useState<File[]>([]);
  const [voiceName, setVoiceName] = useState("");
  const [consent, setConsent] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function pickFiles(e: ChangeEvent<HTMLInputElement>) {
    const next = Array.from(e.target.files ?? []);
    setFiles(next);
    setError(null);
  }

  async function submitClone() {
    if (!files.length || !voiceName.trim() || !consent) return;
    setSubmitting(true);
    setError(null);
    try {
      const result = await cloneVoiceForSession(sessionId, {
        name: voiceName.trim(),
        consent_label: "Uploaded in dashboard with explicit consent checkbox.",
        files,
      });
      onCloned(result);
      setFiles([]);
      setVoiceName("");
      setConsent(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Voice clone failed");
    } finally {
      setSubmitting(false);
    }
  }

  const totalKB = Math.round(files.reduce((sum, file) => sum + file.size, 0) / 1024);

  return (
    <div className="stack" style={{ gap: 8 }}>
      <div className="field">
        <label>Voice name</label>
        <input
          value={voiceName}
          placeholder="e.g. Presenter A — CHI 2026"
          maxLength={80}
          onChange={(e) => setVoiceName(e.target.value)}
        />
      </div>
      <input
        type="file"
        accept="audio/*"
        multiple
        onChange={pickFiles}
      />
      {files.length > 0 && (
        <p className="hint" style={{ fontSize: 11 }}>
          {files.length} file{files.length === 1 ? "" : "s"} · {totalKB}KB total
        </p>
      )}
      <label className="row" style={{ gap: 6, alignItems: "flex-start", fontSize: 12 }}>
        <input
          type="checkbox"
          checked={consent}
          onChange={(e) => setConsent(e.target.checked)}
          style={{ marginTop: 3 }}
        />
        <span>
          I confirm I am the owner of this voice or have explicit permission to clone it for this
          presentation. Consent is recorded server-side.
        </span>
      </label>
      <button
        type="button"
        className="button"
        disabled={!files.length || !voiceName.trim() || !consent || submitting}
        onClick={submitClone}
        style={{ alignSelf: "flex-start" }}
      >
        {submitting ? (
          <>
            <span className="spinner" /> Cloning...
          </>
        ) : (
          "Clone voice"
        )}
      </button>
      {error && <p className="hint" style={{ color: "var(--coral)" }}>{error}</p>}
    </div>
  );
}
