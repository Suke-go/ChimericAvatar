export type UserRole = "admin" | "member";

export type CurrentUser = {
  user_id: string;
  role: UserRole;
};

export type SessionItem = {
  id: string;
  owner_id: string;
  title: string;
  abstract: string | null;
  event_name: string | null;
  presenter_name: string | null;
  presenter_name_kana: string | null;
  presenter_affiliation: string | null;
  status: string;
  session_code: string;
};

export type PosterConfig = {
  session_id: string;
  poster_asset_id: string | null;
  poster_format: string;
  orientation: "portrait" | "landscape";
  physical_width_m: number;
  physical_height_m: number;
  tracking_reference_name: string | null;
  qr_fallback_enabled: boolean;
};

export type PosterPanel = {
  id: string;
  session_id: string;
  label: string;
  order_index: number;
  x: number;
  y: number;
  width: number;
  height: number;
  text_content?: string | null;
};

export type PosterTextBlock = {
  id: string;
  page_number: number;
  order_index: number;
  x: number;
  y: number;
  width: number;
  height: number;
  text: string;
};

export type PreviewCue = {
  id?: string;
  cue_type?: string;
  type?: string;
  start_ms: number;
  duration_ms?: number | null;
  payload: Record<string, unknown>;
};

export type AssetDownloadUrl = {
  url: string;
  expires_at: number;
  mime_type?: string | null;
};

export type RuntimeManifest = {
  schemaVersion: "1.0";
  sessionCode: string;
  issuedAt: string;
  expiresAt: string;
};

export type KnowledgeDocument = {
  id: string;
  session_id: string;
  asset_id: string | null;
  kind: string;
  title: string | null;
  language: "ja" | "en";
  status: string;
  trust_level: "primary" | "author_note" | "reference" | "weak";
  use_for_script: boolean;
  use_for_live_qa: boolean;
  visibility: "admin_only" | "presenter" | "runtime";
  source_url: string | null;
  citation_label: string | null;
};

export type KnowledgeDocumentDetail = KnowledgeDocument & {
  text: string | null;
  chunk_count: number;
};

export type KnowledgeChunk = {
  id: string;
  document_id: string;
  panel_id: string | null;
  chunk_order: number;
  text: string;
  language: "ja" | "en";
  section_title: string | null;
  page_number: number | null;
  token_count: number;
  embedding_model: string | null;
  embedding_dimensions: number | null;
  embedding_status: string;
  retrieval_enabled: boolean;
  script_enabled: boolean;
  qa_enabled: boolean;
  quality_status: string;
  metadata: Record<string, unknown>;
};

export type AudienceProfile = "beginner" | "master" | "professional";
export type ScriptLanguage = "ja" | "en";

export type ScriptSegment = {
  id: string;
  session_id: string;
  profile: AudienceProfile;
  language: ScriptLanguage;
  panel_id: string | null;
  segment_order: number;
  segment_type: string;
  text: string;
  evidence_chunk_ids: string[];
  status: "draft" | "approved" | "needs_review";
  tts_asset_id: string | null;
  duration_estimate_sec: number | null;
};

export type TtsAsset = {
  id: string;
  segment_id: string;
  asset_id: string;
  provider: string;
  voice_id: string;
  model_id: string;
  language: ScriptLanguage;
  audio_url: string;
  expires_at: number;
};

export type KnowledgeUploadResult = {
  document: KnowledgeDocument;
  chunks_created: number;
  extracted_chars: number;
  extraction_method: string;
};

export type VoiceConsent = {
  id: string;
  session_id: string;
  voice_id: string;
  provider: string;
  confirmed_by_user_id: string;
  consent_label: string;
  notes: string | null;
};

export type RetrievalPreview = {
  answerability: "answerable" | "weak" | "escalate";
  reason: string;
  draft_answer: string;
  chunks: Array<{
    chunk_id: string;
    document_id: string;
    panel_id: string | null;
    score: number;
    text: string;
    metadata: Record<string, unknown>;
  }>;
};

export type AvatarRuntimeType = "default-vrm" | "gvrm" | "scaniverse-source";

export type AvatarConfig = {
  session_id: string;
  runtime_type: AvatarRuntimeType;
  gvrm_asset_id: string | null;
  /** Short-lived signed URL for the .gvrm artifact, or null if no build has succeeded. */
  gvrm_url: string | null;
  gvrm_url_expires_at: number | null;
  /** Version of the preprocess algorithm baked into this .gvrm. */
  gvrm_build_version: string | null;
  /** Server's CURRENT preprocess version. Mismatch with gvrm_build_version
   * means the .gvrm is stale and the user should rebuild for correct rendering. */
  expected_build_version: string | null;
  source_ply_asset_id: string | null;
  source_vrm_asset_id: string | null;
  display_name: string | null;
  attribution: string | null;
  license_note: string | null;
  placement_position: [number, number, number];
  placement_rotation: [number, number, number];
  placement_scale: number;
  avatar_height_scale: number;
  voice_id: string | null;
  /** "fem_vroid" / "masc_vroid" / null. Picks a bundled base VRM; null = server default. */
  base_vrm_preset: "fem_vroid" | "masc_vroid" | null;
  default_animation: string;
  behavior_idle: string;
  behavior_explain: string;
  behavior_listening: string;
  behavior_thinking: string;
  metadata: Record<string, unknown> | null;
};

export type AvatarAsset = {
  id: string;
  kind: string;
  storage_path: string;
  file_name: string;
  mime_type: string | null;
  size_bytes: number | null;
  status: string;
};

export type AvatarUploadResult = {
  config: AvatarConfig;
  asset: AvatarAsset;
  metadata: Record<string, unknown>;
};

export type AvatarSourceUploadResult = {
  config: AvatarConfig;
  asset: AvatarAsset;
  role: "scaniverse-ply" | "scaniverse-spz" | "base-vrm";
};

export type AvatarBuildJob = {
  id: string;
  session_id: string;
  source_ply_asset_id: string | null;
  source_vrm_asset_id: string | null;
  output_asset_id: string | null;
  status: "queued" | "running" | "succeeded" | "failed";
  progress: number;
  log: string | null;
  error: string | null;
  requested_by: string | null;
  created_at: string | null;
  updated_at: string | null;
};

export type SimulatedQa = {
  id: string;
  session_id: string;
  profile: AudienceProfile;
  language: ScriptLanguage;
  panel_id: string | null;
  question: string;
  answer: string;
  evidence_chunk_ids: string[];
  status: "draft" | "approved" | "rejected";
  source: string;
};
