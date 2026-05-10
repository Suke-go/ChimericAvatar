"use client";

import { authHeaders } from "./auth-store";
import { xhrUpload } from "./xhr-upload";
import {
  AssetDownloadUrl,
  AudienceProfile,
  AvatarBuildJob,
  AvatarConfig,
  AvatarRuntimeType,
  AvatarSourceUploadResult,
  AvatarUploadResult,
  CurrentUser,
  KnowledgeChunk,
  KnowledgeDocument,
  KnowledgeDocumentDetail,
  KnowledgeUploadResult,
  PosterConfig,
  PosterPanel,
  PosterTextBlock,
  PreviewCue,
  RuntimeEntry,
  RuntimeManifest,
  ScriptLanguage,
  ScriptSegment,
  SessionItem,
  RetrievalPreview,
  SimulatedQa,
  TtsAsset,
  VoiceConsent,
} from "./types";

const API_BASE_URL = process.env.NEXT_PUBLIC_API_BASE_URL ?? "http://127.0.0.1:8000";

async function apiFetch<T>(path: string, init: RequestInit = {}): Promise<T> {
  const auth = await authHeaders();
  const headers = new Headers(init.headers);
  headers.set("Content-Type", headers.get("Content-Type") ?? "application/json");
  for (const [k, v] of Object.entries(auth)) {
    if (v) headers.set(k, v);
  }

  const response = await fetch(`${API_BASE_URL}${path}`, {
    ...init,
    headers,
    cache: "no-store",
  });

  if (!response.ok) {
    const detail = await response.text();
    throw new Error(detail || `Request failed: ${response.status}`);
  }

  return (await response.json()) as T;
}

export async function fetchMe(): Promise<CurrentUser> {
  return apiFetch<CurrentUser>("/api/v1/me");
}

export async function fetchSessions(): Promise<SessionItem[]> {
  return apiFetch<SessionItem[]>("/api/v1/sessions");
}

export async function createSession(payload: {
  title: string;
  abstract?: string;
  event_name?: string;
  presenter_name?: string;
  presenter_name_kana?: string;
  presenter_affiliation?: string;
}): Promise<SessionItem> {
  return apiFetch<SessionItem>("/api/v1/sessions", {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export async function updateSession(
  sessionId: string,
  payload: Partial<{
    title: string;
    abstract: string;
    event_name: string;
    presenter_name: string;
    presenter_name_kana: string;
    presenter_affiliation: string;
    status: "draft" | "review" | "published" | "archived";
  }>,
): Promise<SessionItem> {
  return apiFetch<SessionItem>(`/api/v1/sessions/${sessionId}`, {
    method: "PATCH",
    body: JSON.stringify(payload),
  });
}

export async function fetchPoster(sessionId: string): Promise<PosterConfig> {
  return apiFetch<PosterConfig>(`/api/v1/sessions/${sessionId}/poster`);
}

export async function updatePoster(
  sessionId: string,
  payload: { orientation: "portrait" | "landscape"; qr_fallback_enabled: boolean },
): Promise<PosterConfig> {
  return apiFetch<PosterConfig>(`/api/v1/sessions/${sessionId}/poster`, {
    method: "PATCH",
    body: JSON.stringify(payload),
  });
}

export async function uploadPoster(
  sessionId: string,
  file: File,
  onProgress?: (loaded: number, total: number) => void,
) {
  const formData = new FormData();
  formData.append("file", file);
  return xhrUpload(`${API_BASE_URL}/api/v1/sessions/${sessionId}/poster/upload`, formData, onProgress);
}

export async function fetchPosterImageUrl(sessionId: string): Promise<AssetDownloadUrl> {
  return apiFetch<AssetDownloadUrl>(`/api/v1/sessions/${sessionId}/poster/image-url`);
}

export async function fetchPanels(sessionId: string): Promise<PosterPanel[]> {
  return apiFetch<PosterPanel[]>(`/api/v1/sessions/${sessionId}/poster/panels`);
}

export async function createPanel(
  sessionId: string,
  payload: Omit<PosterPanel, "id" | "session_id">,
): Promise<PosterPanel> {
  return apiFetch<PosterPanel>(`/api/v1/sessions/${sessionId}/poster/panels`, {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export async function updatePanel(
  sessionId: string,
  panelId: string,
  payload: Partial<Omit<PosterPanel, "id" | "session_id">>,
): Promise<PosterPanel> {
  return apiFetch<PosterPanel>(`/api/v1/sessions/${sessionId}/poster/panels/${panelId}`, {
    method: "PATCH",
    body: JSON.stringify(payload),
  });
}

export async function fetchPosterTextBlocks(sessionId: string): Promise<PosterTextBlock[]> {
  return apiFetch<PosterTextBlock[]>(`/api/v1/sessions/${sessionId}/poster/text-blocks`);
}

export async function deletePanel(sessionId: string, panelId: string): Promise<void> {
  const auth = await authHeaders();
  const headers = new Headers();
  for (const [k, v] of Object.entries(auth)) {
    if (v) headers.set(k, v);
  }
  const response = await fetch(`${API_BASE_URL}/api/v1/sessions/${sessionId}/poster/panels/${panelId}`, {
    method: "DELETE",
    headers,
  });
  if (!response.ok) {
    throw new Error(await response.text());
  }
}

export async function fetchPreviewCues(sessionId: string): Promise<PreviewCue[]> {
  return apiFetch<PreviewCue[]>(`/api/v1/sessions/${sessionId}/preview/cues`);
}

export async function replacePreviewCues(
  sessionId: string,
  cues: Array<{ type: string; start_ms: number; duration_ms?: number | null; payload: Record<string, unknown> }>,
): Promise<PreviewCue[]> {
  return apiFetch<PreviewCue[]>(`/api/v1/sessions/${sessionId}/preview/cues`, {
    method: "PUT",
    body: JSON.stringify({ cues }),
  });
}

export async function publishSession(
  sessionId: string,
  options: { force?: boolean } = {},
): Promise<SessionItem> {
  // The API refuses to publish a session whose manifest references storage
  // objects that have gone missing (DB row exists, bucket file does not).
  // Pass `force: true` only when the operator has explicitly acknowledged
  // they're shipping a placeholder.
  const qs = options.force ? "?force=true" : "";
  return apiFetch<SessionItem>(`/api/v1/sessions/${sessionId}/publish${qs}`, {
    method: "POST",
  });
}

export async function fetchSessionManifest(sessionId: string): Promise<RuntimeManifest> {
  return apiFetch<RuntimeManifest>(`/api/v1/sessions/${sessionId}/manifest`);
}

export async function createRuntimeEntry(sessionId: string): Promise<RuntimeEntry> {
  return apiFetch<RuntimeEntry>(`/api/v1/sessions/${sessionId}/runtime-entry`, {
    method: "POST",
  });
}

export function getRuntimeManifestUrl(sessionCode: string): string {
  return `${API_BASE_URL}/api/v1/runtime/${sessionCode}/manifest`;
}

export async function createKnowledgeText(
  sessionId: string,
  payload: {
    kind: "paper" | "poster" | "author_notes" | "limitation" | "expected_qa" | "reference";
    title?: string;
    text: string;
    language: ScriptLanguage;
    panel_id?: string | null;
    trust_level?: "primary" | "author_note" | "reference" | "weak";
    use_for_script?: boolean;
    use_for_live_qa?: boolean;
    visibility?: "admin_only" | "presenter" | "runtime";
    source_url?: string;
    citation_label?: string;
  },
): Promise<KnowledgeDocument> {
  return apiFetch<KnowledgeDocument>(`/api/v1/sessions/${sessionId}/knowledge/text`, {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export async function fetchKnowledgeDocuments(sessionId: string): Promise<KnowledgeDocument[]> {
  return apiFetch<KnowledgeDocument[]>(`/api/v1/sessions/${sessionId}/knowledge/documents`);
}

export async function deleteKnowledgeDocument(sessionId: string, documentId: string): Promise<void> {
  const auth = await authHeaders();
  const headers = new Headers();
  for (const [k, v] of Object.entries(auth)) {
    if (v) headers.set(k, v);
  }
  const response = await fetch(`${API_BASE_URL}/api/v1/sessions/${sessionId}/knowledge/documents/${documentId}`, {
    method: "DELETE",
    headers,
  });
  if (!response.ok) {
    throw new Error(await response.text());
  }
}

export async function fetchKnowledgeDocument(sessionId: string, documentId: string): Promise<KnowledgeDocumentDetail> {
  return apiFetch<KnowledgeDocumentDetail>(`/api/v1/sessions/${sessionId}/knowledge/documents/${documentId}`);
}

export async function fetchKnowledgeChunks(sessionId: string): Promise<KnowledgeChunk[]> {
  return apiFetch<KnowledgeChunk[]>(`/api/v1/sessions/${sessionId}/knowledge/chunks`);
}

export async function previewRetrieval(
  sessionId: string,
  payload: {
    question: string;
    profile: AudienceProfile;
    language: ScriptLanguage;
    active_panel_id?: string | null;
    top_k?: number;
  },
): Promise<RetrievalPreview> {
  return apiFetch<RetrievalPreview>(`/api/v1/sessions/${sessionId}/knowledge/retrieval-preview`, {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export async function generateScripts(
  sessionId: string,
  payload: {
    profile: AudienceProfile;
    language: ScriptLanguage;
    replace_existing: boolean;
    use_openai?: boolean;
    simulated_question_count?: number;
  },
): Promise<ScriptSegment[]> {
  return apiFetch<ScriptSegment[]>(`/api/v1/sessions/${sessionId}/scripts/generate`, {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export async function fetchSimulatedQas(
  sessionId: string,
  profile?: AudienceProfile,
  language?: ScriptLanguage,
): Promise<SimulatedQa[]> {
  const params = new URLSearchParams();
  if (profile) params.set("profile", profile);
  if (language) params.set("language", language);
  const suffix = params.toString() ? `?${params.toString()}` : "";
  return apiFetch<SimulatedQa[]>(`/api/v1/sessions/${sessionId}/simulated-qas${suffix}`);
}

export async function updateSimulatedQa(
  sessionId: string,
  qaId: string,
  payload: { status?: "draft" | "approved" | "rejected"; question?: string; answer?: string },
): Promise<SimulatedQa> {
  return apiFetch<SimulatedQa>(`/api/v1/sessions/${sessionId}/simulated-qas/${qaId}`, {
    method: "PATCH",
    body: JSON.stringify(payload),
  });
}

export async function fetchScripts(
  sessionId: string,
  profile?: AudienceProfile,
  language?: ScriptLanguage,
): Promise<ScriptSegment[]> {
  const params = new URLSearchParams();
  if (profile) params.set("profile", profile);
  if (language) params.set("language", language);
  const suffix = params.toString() ? `?${params.toString()}` : "";
  return apiFetch<ScriptSegment[]>(`/api/v1/sessions/${sessionId}/scripts${suffix}`);
}

export async function updateScriptSegment(
  sessionId: string,
  segmentId: string,
  payload: { text?: string; status?: "draft" | "approved" | "needs_review" },
): Promise<ScriptSegment> {
  return apiFetch<ScriptSegment>(`/api/v1/sessions/${sessionId}/scripts/${segmentId}`, {
    method: "PATCH",
    body: JSON.stringify(payload),
  });
}

export async function generateSegmentTts(
  sessionId: string,
  segmentId: string,
  payload: { voice_id?: string; model_id?: string; consent_confirmed?: boolean; consent_label?: string },
): Promise<TtsAsset> {
  return apiFetch<TtsAsset>(`/api/v1/sessions/${sessionId}/scripts/${segmentId}/tts`, {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export async function uploadKnowledgeFile(
  sessionId: string,
  payload: {
    file: File;
    kind: string;
    language: ScriptLanguage;
    title?: string;
    panel_id?: string | null;
    auto_extract: boolean;
    trust_level?: "primary" | "author_note" | "reference" | "weak";
    use_for_script?: boolean;
    use_for_live_qa?: boolean;
    visibility?: "admin_only" | "presenter" | "runtime";
    source_url?: string;
    citation_label?: string;
  },
): Promise<KnowledgeUploadResult> {
  const formData = new FormData();
  formData.append("file", payload.file);
  formData.append("kind", payload.kind);
  formData.append("language", payload.language);
  formData.append("auto_extract", String(payload.auto_extract));
  formData.append("trust_level", payload.trust_level ?? "primary");
  formData.append("use_for_script", String(payload.use_for_script ?? true));
  formData.append("use_for_live_qa", String(payload.use_for_live_qa ?? true));
  formData.append("visibility", payload.visibility ?? "runtime");
  if (payload.title) formData.append("title", payload.title);
  if (payload.panel_id) formData.append("panel_id", payload.panel_id);
  if (payload.source_url) formData.append("source_url", payload.source_url);
  if (payload.citation_label) formData.append("citation_label", payload.citation_label);
  return xhrUpload(`${API_BASE_URL}/api/v1/sessions/${sessionId}/knowledge/upload`, formData);
}

export async function fetchVoiceConsents(sessionId: string): Promise<VoiceConsent[]> {
  return apiFetch<VoiceConsent[]>(`/api/v1/sessions/${sessionId}/voice-consents`);
}

export async function fetchAvatarConfig(sessionId: string): Promise<AvatarConfig> {
  return apiFetch<AvatarConfig>(`/api/v1/sessions/${sessionId}/avatar`);
}

export async function updateAvatarConfig(
  sessionId: string,
  payload: Partial<{
    runtime_type: AvatarRuntimeType;
    display_name: string;
    attribution: string;
    license_note: string;
    placement_position: [number, number, number];
    placement_rotation: [number, number, number];
    placement_scale: number;
    avatar_height_scale: number;
    voice_id: string | null;
    base_vrm_preset: "fem_vroid" | "masc_vroid" | null;
    default_animation: string;
    behavior_idle: string;
    behavior_explain: string;
    behavior_listening: string;
    behavior_thinking: string;
  }>,
): Promise<AvatarConfig> {
  return apiFetch<AvatarConfig>(`/api/v1/sessions/${sessionId}/avatar`, {
    method: "PATCH",
    body: JSON.stringify(payload),
  });
}

export type VoiceSample = {
  id: string;
  voice_id: string;
  text: string;
  language: string;
  model_id: string;
  audio_url: string;
  expires_at: number;
  /** True when the server returned cached audio (no ElevenLabs API call). */
  cached: boolean;
  created_at: string;
};

export async function generateVoiceSample(payload: {
  voice_id: string;
  text?: string;
  language?: "ja" | "en";
}): Promise<VoiceSample> {
  return apiFetch<VoiceSample>(`/api/v1/voice-samples`, {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export type VoiceCloneResult = {
  voice_id: string;
  consent: VoiceConsent;
  /** True when the API set this session's AvatarConfig.voice_id to the cloned voice. */
  avatar_voice_id_set: boolean;
  sample_count: number;
};

/**
 * Forward recorded / uploaded audio samples to the API's voice-clone endpoint,
 * which calls ElevenLabs IVC server-side. The server records consent and (by
 * default) wires the new voice ID into this session's AvatarConfig.
 */
export async function cloneVoiceForSession(
  sessionId: string,
  payload: {
    name: string;
    consent_label: string;
    description?: string;
    set_as_session_voice?: boolean;
    remove_background_noise?: boolean;
    files: File[] | Blob[];
  },
): Promise<VoiceCloneResult> {
  const auth = await authHeaders();
  const headers = new Headers();
  for (const [k, v] of Object.entries(auth)) {
    if (v) headers.set(k, v);
  }
  // Don't set Content-Type — the browser must add the multipart boundary.
  const form = new FormData();
  form.append("name", payload.name);
  form.append("consent_label", payload.consent_label);
  if (payload.description) form.append("description", payload.description);
  form.append("set_as_session_voice", String(payload.set_as_session_voice ?? true));
  form.append("remove_background_noise", String(payload.remove_background_noise ?? false));
  payload.files.forEach((file, index) => {
    // ElevenLabs accepts repeated `files` fields. The server side reads
    // them as `list[UploadFile]`. Provide a stable filename so each take
    // shows up distinctly in ElevenLabs' voice editor.
    const filename = (file as File).name ?? `clone-take-${index + 1}.webm`;
    form.append("files", file, filename);
  });
  const response = await fetch(
    `${API_BASE_URL}/api/v1/sessions/${sessionId}/voice/clone`,
    { method: "POST", headers, body: form },
  );
  if (!response.ok) {
    throw new Error((await response.text()) || `Voice clone failed: ${response.status}`);
  }
  return (await response.json()) as VoiceCloneResult;
}

export async function uploadGvrmArchive(
  sessionId: string,
  file: File,
  meta?: { display_name?: string; attribution?: string; license_note?: string },
  onProgress?: (loaded: number, total: number) => void,
): Promise<AvatarUploadResult> {
  const formData = new FormData();
  formData.append("file", file);
  if (meta?.display_name) formData.append("display_name", meta.display_name);
  if (meta?.attribution) formData.append("attribution", meta.attribution);
  if (meta?.license_note) formData.append("license_note", meta.license_note);
  return xhrUpload<AvatarUploadResult>(
    `${API_BASE_URL}/api/v1/sessions/${sessionId}/avatar/upload-gvrm`,
    formData,
    onProgress,
  );
}

export async function uploadAvatarSource(
  sessionId: string,
  file: File,
  onProgress?: (loaded: number, total: number) => void,
): Promise<AvatarSourceUploadResult> {
  const formData = new FormData();
  formData.append("file", file);
  return xhrUpload<AvatarSourceUploadResult>(
    `${API_BASE_URL}/api/v1/sessions/${sessionId}/avatar/upload-source`,
    formData,
    onProgress,
  );
}

export async function enqueueAvatarBuild(sessionId: string): Promise<AvatarBuildJob> {
  return apiFetch<AvatarBuildJob>(`/api/v1/sessions/${sessionId}/avatar/build`, { method: "POST" });
}

export async function fetchAvatarBuilds(sessionId: string): Promise<AvatarBuildJob[]> {
  return apiFetch<AvatarBuildJob[]>(`/api/v1/sessions/${sessionId}/avatar/builds`);
}
