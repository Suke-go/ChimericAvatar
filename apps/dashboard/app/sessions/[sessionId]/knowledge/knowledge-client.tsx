"use client";

import { FormEvent, useCallback, useEffect, useMemo, useState } from "react";

import SaveBadge from "../../../../components/SaveBadge";
import SessionWorkspace from "../../../../components/SessionWorkspace";
import { useAutoSave } from "../../../../lib/useAutoSave";
import { useTaskTracker } from "../../../../lib/useTaskTracker";
import {
  createKnowledgeText,
  deleteKnowledgeDocument,
  fetchKnowledgeChunks,
  fetchKnowledgeDocuments,
  fetchPanels,
  fetchScripts,
  fetchSimulatedQas,
  fetchVoiceConsents,
  generateScripts,
  generateSegmentTts,
  updateScriptSegment,
  updateSimulatedQa,
  uploadKnowledgeFile,
} from "../../../../lib/api";
import {
  AudienceProfile,
  KnowledgeChunk,
  KnowledgeDocument,
  PosterPanel,
  ScriptLanguage,
  ScriptSegment,
  SimulatedQa,
  VoiceConsent,
} from "../../../../lib/types";

const profileLabels: Record<AudienceProfile, string> = {
  beginner: "Beginner",
  master: "Master",
  professional: "Professional",
};

type Kind = "paper" | "poster" | "author_notes" | "limitation" | "expected_qa" | "reference";

const KIND_HINTS: Record<Kind, string> = {
  paper: "Published paper / preprint (PDF)",
  poster: "A0 poster image — OCR'd via vision model",
  author_notes: "Personal notes, methodology details, motivation",
  limitation: "Known limitations / failure modes",
  expected_qa: "Author-curated Q&A pairs",
  reference: "Background reading, prior work",
};

export default function KnowledgeClient({ sessionId }: { sessionId: string }) {
  const [documents, setDocuments] = useState<KnowledgeDocument[]>([]);
  const [chunks, setChunks] = useState<KnowledgeChunk[]>([]);
  const [panels, setPanels] = useState<PosterPanel[]>([]);
  const [scripts, setScripts] = useState<ScriptSegment[]>([]);
  const [simulatedQas, setSimulatedQas] = useState<SimulatedQa[]>([]);
  const [voiceConsents, setVoiceConsents] = useState<VoiceConsent[]>([]);

  // Step 1 — ingest
  const [kind, setKind] = useState<Kind>("paper");
  const [language, setLanguage] = useState<ScriptLanguage>("ja");
  const [profile, setProfile] = useState<AudienceProfile>("master");
  const [panelId, setPanelId] = useState("");
  const [title, setTitle] = useState("");
  const [text, setText] = useState("");
  const [file, setFile] = useState<File | null>(null);
  const [trustLevel, setTrustLevel] = useState<"primary" | "author_note" | "reference" | "weak">("primary");
  const [showAdvancedIngest, setShowAdvancedIngest] = useState(false);
  const [visibility, setVisibility] = useState<"admin_only" | "presenter" | "runtime">("runtime");
  const [useForScript, setUseForScript] = useState(true);
  const [useForLiveQa, setUseForLiveQa] = useState(true);
  const [sourceUrl, setSourceUrl] = useState("");
  const [citationLabel, setCitationLabel] = useState("");

  // Step 2 — generate
  const [useOpenai, setUseOpenai] = useState(true);
  const [simulatedQuestionCount, setSimulatedQuestionCount] = useState(6);

  // Step 3 — review
  const [voiceId, setVoiceId] = useState("");
  const [voiceConsentConfirmed, setVoiceConsentConfirmed] = useState(false);
  const [editing, setEditing] = useState<Record<string, string>>({});

  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const tracker = useTaskTracker();

  const chunkById = useMemo(() => new Map(chunks.map((c) => [c.id, c])), [chunks]);

  // Auto-save script segment text edits with 1.5s debounce.
  // Drafts mirror to sessionStorage so a tab close right after typing is safe.
  const scriptSaver = useCallback(
    async (id: string, text: string) => {
      await updateScriptSegment(sessionId, id, { text });
    },
    [sessionId],
  );
  const scriptAutoSave = useAutoSave<string>(scriptSaver, 1500, `chimera-script-draft:${sessionId}`);

  // Auto-save Q&A inline edits (question + answer) with 1.5s debounce
  const qaSaver = useCallback(
    async (id: string, payload: { question?: string; answer?: string }) => {
      await updateSimulatedQa(sessionId, id, payload);
    },
    [sessionId],
  );
  const qaAutoSave = useAutoSave<{ question?: string; answer?: string }>(
    qaSaver,
    1500,
    `chimera-qa-draft:${sessionId}`,
  );

  async function load() {
    try {
      const [d, c, p, s, q, v] = await Promise.all([
        fetchKnowledgeDocuments(sessionId),
        fetchKnowledgeChunks(sessionId),
        fetchPanels(sessionId),
        fetchScripts(sessionId, profile, language),
        fetchSimulatedQas(sessionId, profile, language),
        fetchVoiceConsents(sessionId),
      ]);
      setDocuments(d);
      setChunks(c);
      setPanels(p);
      setScripts(s);
      setSimulatedQas(q);
      setVoiceConsents(v);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load");
    }
  }

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId, profile, language]);

  async function withBusy(label: string, fn: () => Promise<void>, runningLabel?: string) {
    setBusy(true);
    setMessage(null);
    setError(null);
    try {
      await tracker.track(runningLabel ?? label, fn);
      setMessage(label);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Operation failed");
    } finally {
      setBusy(false);
    }
  }

  async function handleSubmitText(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!text.trim()) {
      setError("Paste some knowledge text first");
      return;
    }
    await withBusy(
      "Knowledge saved",
      async () => {
        await createKnowledgeText(sessionId, {
          kind,
          title: title || undefined,
          text,
          language,
          panel_id: panelId || null,
          trust_level: trustLevel,
          use_for_script: useForScript,
          use_for_live_qa: useForLiveQa,
          visibility,
          source_url: sourceUrl || undefined,
          citation_label: citationLabel || undefined,
        });
        setText("");
        setTitle("");
        await load();
      },
      "Saving knowledge…",
    );
  }

  async function handleSubmitFile(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!file) {
      setError("Select a PDF, text, or poster image");
      return;
    }
    await withBusy(
      "Imported and chunked",
      async () => {
        const result = await uploadKnowledgeFile(sessionId, {
          file,
          kind,
          language,
          title: title || undefined,
          panel_id: panelId || null,
          auto_extract: true,
          trust_level: trustLevel,
          use_for_script: useForScript,
          use_for_live_qa: useForLiveQa,
          visibility,
          source_url: sourceUrl || undefined,
          citation_label: citationLabel || undefined,
        });
        setFile(null);
        setTitle("");
        setMessage(
          `Imported ${result.extracted_chars} chars / ${result.chunks_created} chunks via ${result.extraction_method}`,
        );
        await load();
      },
      "Extracting knowledge…",
    );
  }

  async function handleGenerate() {
    await withBusy(
      `Generated ${profileLabels[profile]} / ${language}`,
      async () => {
        await generateScripts(sessionId, {
          profile,
          language,
          replace_existing: true,
          use_openai: useOpenai,
          simulated_question_count: simulatedQuestionCount,
        });
        await load();
      },
      `Generating ${profileLabels[profile]} / ${language}…`,
    );
  }

  async function handleApprove(segment: ScriptSegment) {
    await withBusy(
      "Approved",
      async () => {
        await updateScriptSegment(sessionId, segment.id, { status: "approved" });
        await load();
      },
      "Approving…",
    );
  }

  async function handleTts(segment: ScriptSegment) {
    await withBusy(
      "TTS generated",
      async () => {
        const tts = await generateSegmentTts(sessionId, segment.id, {
          voice_id: voiceId || undefined,
          consent_confirmed: voiceConsentConfirmed,
          consent_label: "Presenter confirmed rights and consent for ElevenLabs voice use",
        });
        await load();
        window.open(tts.audio_url, "_blank", "noopener,noreferrer");
      },
      `Synthesizing voice (#${segment.segment_order})…`,
    );
  }

  async function handleApproveAll() {
    const pending = scripts.filter((s) => s.status !== "approved");
    if (pending.length === 0) return;
    await withBusy(
      `Approved ${pending.length} segments`,
      async () => {
        await Promise.all(pending.map((s) => updateScriptSegment(sessionId, s.id, { status: "approved" })));
        await load();
      },
      `Approving ${pending.length} segments…`,
    );
  }

  async function handleQaStatus(item: SimulatedQa, status: "approved" | "rejected") {
    await withBusy(
      `Q&A ${status}`,
      async () => {
        await updateSimulatedQa(sessionId, item.id, { status });
        await load();
      },
      `Marking Q&A ${status}…`,
    );
  }

  // Stepper status
  const docCount = documents.length;
  const chunkCount = chunks.length;
  const scriptCount = scripts.length;
  const approvedCount = scripts.filter((s) => s.status === "approved").length;
  const allApproved = scriptCount > 0 && approvedCount === scriptCount;

  const stepIngest = docCount > 0 ? "done" : "active";
  const stepGenerate = scriptCount > 0 ? "done" : docCount > 0 ? "active" : "";
  const stepReview = allApproved ? "done" : scriptCount > 0 ? "active" : "";

  return (
    <SessionWorkspace
      sessionId={sessionId}
      eyebrow="Knowledge"
      actions={
        <span className="badge muted">
          {docCount} DOCS · {chunkCount} CHUNKS · {approvedCount}/{scriptCount} APPROVED
        </span>
      }
    >
      {error && <div className="alert error">{error}</div>}
      {message && <div className="alert info">{message}</div>}

      <div className="stepper">
        <div className={`step ${stepIngest}`}>
          <span className="num">1</span>INGEST
        </div>
        <div className={`step ${stepGenerate}`}>
          <span className="num">2</span>GENERATE
        </div>
        <div className={`step ${stepReview}`}>
          <span className="num">3</span>REVIEW
        </div>
      </div>

      {/* === STEP 1: ingest === */}
      <section className="panel">
        <div className="panel-title">
          <span className="marker" />
          STEP 01 // INGEST KNOWLEDGE
          <span className="panel-subtitle">// upload paper / poster / notes — auto chunk + embed</span>
        </div>

        <div className="grid two">
          <div className="card">
            <h3>A. Upload file</h3>
            <p className="hint">PDF (auto text extraction) · Text/MD · Poster image (vision OCR)</p>
            <form onSubmit={handleSubmitFile} className="stack">
              <div className="field-row">
                <div className="field">
                  <label>Kind</label>
                  <select value={kind} onChange={(e) => setKind(e.target.value as Kind)}>
                    {Object.keys(KIND_HINTS).map((k) => (
                      <option key={k} value={k}>
                        {k}
                      </option>
                    ))}
                  </select>
                </div>
                <div className="field">
                  <label>Language</label>
                  <select value={language} onChange={(e) => setLanguage(e.target.value as ScriptLanguage)}>
                    <option value="ja">日本語</option>
                    <option value="en">English</option>
                  </select>
                </div>
              </div>
              <p className="hint">{KIND_HINTS[kind]}</p>
              <div className="field">
                <label>Title (optional)</label>
                <input value={title} onChange={(e) => setTitle(e.target.value)} />
              </div>
              <div className="field">
                <label>File</label>
                <input
                  type="file"
                  accept=".pdf,.txt,.md,.png,.jpg,.jpeg,.webp"
                  onChange={(e) => setFile(e.target.files?.[0] ?? null)}
                />
                {file && (
                  <p className="hint" style={{ marginTop: 4 }}>
                    {file.name} ({Math.round(file.size / 1024)} KB)
                  </p>
                )}
              </div>
              <button className="button" type="submit" disabled={busy || !file}>
                Upload & extract →
              </button>
            </form>
          </div>

          <div className="card">
            <h3>B. Paste text</h3>
            <p className="hint">Quick path for notes you typed yourself.</p>
            <form onSubmit={handleSubmitText} className="stack">
              <div className="field">
                <label>Title (optional)</label>
                <input value={title} onChange={(e) => setTitle(e.target.value)} />
              </div>
              <div className="field">
                <label>Text</label>
                <textarea
                  rows={6}
                  value={text}
                  onChange={(e) => setText(e.target.value)}
                  placeholder="Paste paper abstract, notes, limitations..."
                />
              </div>
              <button className="button" type="submit" disabled={busy || !text.trim()}>
                Save →
              </button>
            </form>
          </div>
        </div>

        <div style={{ marginTop: 16 }}>
          <button
            type="button"
            className="button ghost"
            onClick={() => setShowAdvancedIngest((v) => !v)}
          >
            {showAdvancedIngest ? "▼" : "▶"} advanced metadata
          </button>
          {showAdvancedIngest && (
            <div className="card" style={{ marginTop: 8 }}>
              <div className="grid two">
                <div className="field">
                  <label>Trust level</label>
                  <select value={trustLevel} onChange={(e) => setTrustLevel(e.target.value as typeof trustLevel)}>
                    <option value="primary">primary (paper / poster)</option>
                    <option value="author_note">author_note</option>
                    <option value="reference">reference</option>
                    <option value="weak">weak</option>
                  </select>
                </div>
                <div className="field">
                  <label>Visibility</label>
                  <select value={visibility} onChange={(e) => setVisibility(e.target.value as typeof visibility)}>
                    <option value="runtime">runtime (used by Unity)</option>
                    <option value="presenter">presenter only</option>
                    <option value="admin_only">admin only</option>
                  </select>
                </div>
                <div className="field">
                  <label>Panel (optional)</label>
                  <select value={panelId} onChange={(e) => setPanelId(e.target.value)}>
                    <option value="">— anywhere —</option>
                    {panels.map((panel) => (
                      <option key={panel.id} value={panel.id}>
                        {panel.label}
                      </option>
                    ))}
                  </select>
                </div>
                <div className="field">
                  <label>Citation label</label>
                  <input value={citationLabel} onChange={(e) => setCitationLabel(e.target.value)} />
                </div>
                <div className="field">
                  <label>Source URL</label>
                  <input value={sourceUrl} onChange={(e) => setSourceUrl(e.target.value)} />
                </div>
                <div className="field">
                  <label>Used for</label>
                  <div className="row">
                    <label className="hint" style={{ display: "flex", gap: 4, alignItems: "center" }}>
                      <input
                        type="checkbox"
                        checked={useForScript}
                        onChange={(e) => setUseForScript(e.target.checked)}
                      />
                      script
                    </label>
                    <label className="hint" style={{ display: "flex", gap: 4, alignItems: "center" }}>
                      <input
                        type="checkbox"
                        checked={useForLiveQa}
                        onChange={(e) => setUseForLiveQa(e.target.checked)}
                      />
                      live Q&A
                    </label>
                  </div>
                </div>
              </div>
            </div>
          )}
        </div>

        {documents.length > 0 && (
          <div style={{ marginTop: 20 }}>
            <h3>Registered ({documents.length})</h3>
            <div className="list" style={{ maxHeight: 220, overflow: "auto" }}>
              {documents.map((doc) => (
                <div className="card" key={doc.id} style={{ padding: "8px 12px" }}>
                  <div className="row" style={{ justifyContent: "space-between", gap: 8 }}>
                    <span className="mono" style={{ color: "var(--phos)" }}>
                      {doc.kind}
                    </span>
                    <span className="hint" style={{ flex: 1, minWidth: 0, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                      {doc.title || "(untitled)"}
                    </span>
                    <span className={`badge ${doc.trust_level === "primary" ? "" : "muted"}`}>
                      {doc.trust_level}
                    </span>
                    <span className="badge muted">{doc.language}</span>
                    <button
                      type="button"
                      className="button danger"
                      style={{ padding: "2px 8px" }}
                      disabled={busy}
                      onClick={async () => {
                        if (!window.confirm(`Delete "${doc.title || doc.kind}" and its chunks?`)) return;
                        await withBusy(
                          "Knowledge deleted",
                          async () => {
                            await deleteKnowledgeDocument(sessionId, doc.id);
                            await load();
                          },
                          "Deleting…",
                        );
                      }}
                    >
                      Delete
                    </button>
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}
      </section>

      {/* === STEP 2: generate === */}
      <section className="panel">
        <div className="panel-title">
          <span className="marker" />
          STEP 02 // GENERATE SCRIPT + Q&A
          <span className="panel-subtitle">// produces a draft for the author to review</span>
        </div>
        <div className="grid two">
          <div className="card">
            <h3>Audience profile</h3>
            <div className="row">
              {(Object.keys(profileLabels) as AudienceProfile[]).map((p) => (
                <button
                  key={p}
                  type="button"
                  className={`button ${profile === p ? "" : "secondary"}`}
                  onClick={() => setProfile(p)}
                >
                  {profileLabels[p]}
                </button>
              ))}
            </div>
            <div className="row" style={{ marginTop: 12 }}>
              {(["ja", "en"] as ScriptLanguage[]).map((l) => (
                <button
                  key={l}
                  type="button"
                  className={`button ${language === l ? "" : "secondary"}`}
                  onClick={() => setLanguage(l)}
                >
                  {l === "ja" ? "日本語" : "English"}
                </button>
              ))}
            </div>
          </div>
          <div className="card">
            <h3>Settings</h3>
            <div className="field">
              <label>Simulated questions</label>
              <input
                type="number"
                min={0}
                max={12}
                value={simulatedQuestionCount}
                onChange={(e) => setSimulatedQuestionCount(Math.min(12, Math.max(0, Number(e.target.value))))}
              />
            </div>
            <label className="hint" style={{ display: "flex", gap: 6, alignItems: "center" }}>
              <input type="checkbox" checked={useOpenai} onChange={(e) => setUseOpenai(e.target.checked)} />
              Use OpenAI agent (recommended; falls back to deterministic if no key)
            </label>
          </div>
        </div>
        <div className="row" style={{ marginTop: 16, justifyContent: "flex-end" }}>
          <button
            type="button"
            className="button"
            onClick={handleGenerate}
            disabled={busy || documents.length === 0}
          >
            {busy ? (
              <>
                <span className="spinner" /> Generating…
              </>
            ) : (
              <>
                {scriptCount > 0 ? "Regenerate" : "Generate"} {profileLabels[profile]} / {language} →
              </>
            )}
          </button>
        </div>
        {documents.length === 0 && (
          <p className="hint" style={{ marginTop: 12 }}>
            Add at least one knowledge source above before generating.
          </p>
        )}
      </section>

      {/* === STEP 3: review === */}
      <section className="panel">
        <div className="panel-title">
          <span className="marker" />
          STEP 03 // REVIEW & APPROVE
          <span className="panel-subtitle">// edit · approve · synthesize voice</span>
        </div>

        <div className="grid two">
          <div className="stack">
            <div className="row" style={{ justifyContent: "space-between" }}>
              <h3 style={{ margin: 0 }}>Script segments ({scriptCount})</h3>
              <button
                type="button"
                className="button secondary"
                onClick={handleApproveAll}
                disabled={busy || scriptCount === 0 || allApproved}
              >
                Approve all
              </button>
            </div>
            {scriptCount === 0 ? (
              <p className="hint">Generate first ↑</p>
            ) : (
              <div className="list">
                {scripts.map((segment) => {
                  // Local draft (sessionStorage) > in-memory edit > server value
                  const draft = scriptAutoSave.readDraft(segment.id);
                  const editable = editing[segment.id] ?? draft ?? segment.text;
                  return (
                    <div className="card" key={segment.id}>
                      <div className="row" style={{ justifyContent: "space-between" }}>
                        <span className="mono" style={{ color: "var(--phos)" }}>
                          {segment.segment_type} · #{segment.segment_order}
                        </span>
                        <div className="row" style={{ gap: 6 }}>
                          <SaveBadge state={scriptAutoSave.statuses[segment.id]} />
                          <span
                            className={`badge ${
                              segment.status === "approved"
                                ? ""
                                : segment.status === "needs_review"
                                ? "amber"
                                : "muted"
                            }`}
                          >
                            {segment.status}
                          </span>
                        </div>
                      </div>
                      <textarea
                        rows={3}
                        value={editable}
                        onChange={(e) => {
                          setEditing({ ...editing, [segment.id]: e.target.value });
                          scriptAutoSave.schedule(segment.id, e.target.value);
                        }}
                        onBlur={() => scriptAutoSave.flush(segment.id)}
                        style={{ marginTop: 8 }}
                      />
                      <div className="row" style={{ marginTop: 8, gap: 6 }}>
                        <button
                          type="button"
                          className="button"
                          disabled={busy || segment.status === "approved"}
                          onClick={async () => {
                            await scriptAutoSave.flush(segment.id);
                            await handleApprove(segment);
                          }}
                        >
                          Approve
                        </button>
                        <button
                          type="button"
                          className="button secondary"
                          disabled={busy || segment.status !== "approved"}
                          onClick={() => handleTts(segment)}
                        >
                          TTS
                        </button>
                        {segment.evidence_chunk_ids.length > 0 && (
                          <span className="hint" style={{ marginLeft: "auto" }}>
                            {segment.evidence_chunk_ids
                              .map((id) => chunkById.get(id)?.page_number)
                              .filter(Boolean)
                              .map((p) => `p.${p}`)
                              .join(", ") || `evidence: ${segment.evidence_chunk_ids.length}`}
                          </span>
                        )}
                      </div>
                    </div>
                  );
                })}
              </div>
            )}
          </div>

          <div className="stack">
            <h3 style={{ margin: 0 }}>Simulated Q&A ({simulatedQas.length})</h3>
            {simulatedQas.length === 0 ? (
              <p className="hint">Generate first ↑</p>
            ) : (
              <div className="list">
                {simulatedQas.map((item) => (
                  <QaCard
                    key={item.id}
                    item={item}
                    busy={busy}
                    autoSave={qaAutoSave}
                    onStatus={handleQaStatus}
                  />
                ))}
              </div>
            )}
          </div>
        </div>

        {/* Voice settings collapsed */}
        <details style={{ marginTop: 16 }}>
          <summary className="hint" style={{ cursor: "pointer" }}>
            ▶ ElevenLabs voice for TTS
          </summary>
          <div className="card" style={{ marginTop: 8 }}>
            <div className="grid two">
              <div className="field">
                <label>ElevenLabs voice_id</label>
                <input value={voiceId} onChange={(e) => setVoiceId(e.target.value)} />
              </div>
              <div className="field">
                <label>Consent</label>
                <label
                  className="hint"
                  style={{ display: "flex", gap: 6, alignItems: "center", marginTop: 6 }}
                >
                  <input
                    type="checkbox"
                    checked={voiceConsentConfirmed}
                    onChange={(e) => setVoiceConsentConfirmed(e.target.checked)}
                  />
                  Voice rights confirmed for this voice_id
                </label>
              </div>
            </div>
            {voiceConsents.length > 0 && (
              <p className="hint" style={{ marginTop: 8 }}>
                Confirmed voices: {voiceConsents.map((c) => c.voice_id).join(", ")}
              </p>
            )}
          </div>
        </details>
      </section>
    </SessionWorkspace>
  );
}

type QaPayload = { question?: string; answer?: string };
type AutoSaveBundle<T> = ReturnType<typeof useAutoSave<T>>;

function QaCard({
  item,
  busy,
  autoSave,
  onStatus,
}: {
  item: SimulatedQa;
  busy: boolean;
  autoSave: AutoSaveBundle<QaPayload>;
  onStatus: (item: SimulatedQa, status: "approved" | "rejected") => void;
}) {
  const draft = autoSave.readDraft(item.id);
  const [question, setQuestion] = useState(draft?.question ?? item.question);
  const [answer, setAnswer] = useState(draft?.answer ?? item.answer);

  function update(next: QaPayload) {
    if (next.question !== undefined) setQuestion(next.question);
    if (next.answer !== undefined) setAnswer(next.answer);
    autoSave.schedule(item.id, {
      question: next.question ?? question,
      answer: next.answer ?? answer,
    });
  }

  return (
    <div className="card">
      <div className="row" style={{ justifyContent: "space-between" }}>
        <span className="mono" style={{ color: "var(--phos)" }}>{item.profile}</span>
        <div className="row" style={{ gap: 6 }}>
          <SaveBadge state={autoSave.statuses[item.id]} />
          <span
            className={`badge ${
              item.status === "approved"
                ? ""
                : item.status === "rejected"
                ? "coral"
                : "muted"
            }`}
          >
            {item.status}
          </span>
        </div>
      </div>
      <div className="field" style={{ marginTop: 8 }}>
        <label>Q</label>
        <textarea
          rows={2}
          value={question}
          onChange={(e) => update({ question: e.target.value })}
          onBlur={() => autoSave.flush(item.id)}
        />
      </div>
      <div className="field">
        <label>A</label>
        <textarea
          rows={3}
          value={answer}
          onChange={(e) => update({ answer: e.target.value })}
          onBlur={() => autoSave.flush(item.id)}
        />
      </div>
      <div className="row" style={{ marginTop: 8 }}>
        <button
          type="button"
          className="button"
          disabled={busy || item.status === "approved"}
          onClick={async () => {
            await autoSave.flush(item.id);
            onStatus(item, "approved");
          }}
        >
          Approve
        </button>
        <button
          type="button"
          className="button danger"
          disabled={busy || item.status === "rejected"}
          onClick={async () => {
            await autoSave.flush(item.id);
            onStatus(item, "rejected");
          }}
        >
          Reject
        </button>
      </div>
    </div>
  );
}
