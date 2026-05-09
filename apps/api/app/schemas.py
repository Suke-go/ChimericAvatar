from datetime import datetime
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field, model_validator


class SessionCreate(BaseModel):
    title: str = Field(min_length=1, max_length=255)
    abstract: str | None = None
    event_name: str | None = None
    presenter_name: str | None = Field(default=None, max_length=120)
    presenter_name_kana: str | None = Field(default=None, max_length=120)
    presenter_affiliation: str | None = Field(default=None, max_length=255)


class SessionUpdate(BaseModel):
    title: str | None = None
    abstract: str | None = None
    event_name: str | None = None
    presenter_name: str | None = Field(default=None, max_length=120)
    presenter_name_kana: str | None = Field(default=None, max_length=120)
    presenter_affiliation: str | None = Field(default=None, max_length=255)
    status: Literal["draft", "review", "published", "archived"] | None = None


class SessionRead(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: str
    owner_id: str
    title: str
    abstract: str | None
    event_name: str | None
    presenter_name: str | None
    presenter_name_kana: str | None
    presenter_affiliation: str | None
    status: str
    session_code: str


class PosterConfigUpdate(BaseModel):
    orientation: Literal["portrait", "landscape"]
    qr_fallback_enabled: bool = True


class PosterConfigRead(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    session_id: str
    poster_asset_id: str | None
    poster_format: str
    orientation: str
    physical_width_m: float
    physical_height_m: float
    tracking_reference_name: str | None
    qr_fallback_enabled: bool


class AssetRead(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: str
    kind: str
    storage_path: str
    file_name: str
    mime_type: str | None
    size_bytes: int | None
    status: str


class PosterPanelCreate(BaseModel):
    label: str = Field(min_length=1, max_length=80)
    order_index: int = Field(ge=0)
    x: float = Field(ge=0.0, le=1.0)
    y: float = Field(ge=0.0, le=1.0)
    width: float = Field(gt=0.0, le=1.0)
    height: float = Field(gt=0.0, le=1.0)

    @model_validator(mode="after")
    def validate_bounds(self) -> "PosterPanelCreate":
        if self.x + self.width > 1.0 or self.y + self.height > 1.0:
            raise ValueError("Panel bounds must stay within normalized poster coordinates")
        return self


class PosterPanelUpdate(BaseModel):
    label: str | None = Field(default=None, min_length=1, max_length=80)
    order_index: int | None = Field(default=None, ge=0)
    x: float | None = Field(default=None, ge=0.0, le=1.0)
    y: float | None = Field(default=None, ge=0.0, le=1.0)
    width: float | None = Field(default=None, gt=0.0, le=1.0)
    height: float | None = Field(default=None, gt=0.0, le=1.0)


class PosterPanelRead(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: str
    session_id: str
    label: str
    order_index: int
    x: float
    y: float
    width: float
    height: float
    text_content: str | None = None


class PosterTextBlockRead(BaseModel):
    id: str
    page_number: int
    order_index: int
    x: float
    y: float
    width: float
    height: float
    text: str


class CuePayload(BaseModel):
    type: Literal[
        "panel_focus",
        "pointer_move",
        "caption_show",
        "caption_hide",
        "audio_play",
        "qr_show",
        "ui_panel_show",
        # Avatar arm pointing at a panel. Payload:
        #   panelId: str           — target panel (used by Unity to compute IK target)
        #   hand: "auto"|"left"|"right" — which arm to use (auto = closer side)
        #   intensity: 0.0-1.0     — how strong the gesture (default 0.7)
        "avatar_point",
        # Avatar gestures without a specific target:
        #   style: "nod"|"shake"|"open_palms"|"thinking"|"explain"
        "avatar_gesture",
    ]
    start_ms: int = Field(ge=0)
    duration_ms: int | None = Field(default=None, ge=0)
    payload: dict[str, Any] = Field(default_factory=dict)


class CueListUpdate(BaseModel):
    cues: list[CuePayload]


class CueRead(BaseModel):
    id: str
    cue_type: str
    start_ms: int
    duration_ms: int | None
    payload: dict[str, Any]


class CurrentUserRead(BaseModel):
    user_id: str
    role: str


class AssetDownloadUrlRead(BaseModel):
    url: str
    expires_at: int
    mime_type: str | None = None


class KnowledgeTextCreate(BaseModel):
    kind: Literal["paper", "poster", "author_notes", "limitation", "expected_qa", "reference"]
    title: str | None = Field(default=None, max_length=255)
    text: str = Field(min_length=1)
    language: Literal["ja", "en"] = "ja"
    panel_id: str | None = None
    trust_level: Literal["primary", "author_note", "reference", "weak"] = "primary"
    use_for_script: bool = True
    use_for_live_qa: bool = True
    visibility: Literal["admin_only", "presenter", "runtime"] = "runtime"
    source_url: str | None = Field(default=None, max_length=512)
    citation_label: str | None = Field(default=None, max_length=255)


class KnowledgeDocumentRead(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: str
    session_id: str
    asset_id: str | None
    kind: str
    title: str | None
    language: str
    status: str
    trust_level: str
    use_for_script: bool
    use_for_live_qa: bool
    visibility: str
    source_url: str | None
    citation_label: str | None


class KnowledgeDocumentDetailRead(KnowledgeDocumentRead):
    text: str | None
    chunk_count: int


class KnowledgeChunkRead(BaseModel):
    id: str
    document_id: str
    panel_id: str | None
    chunk_order: int
    text: str
    language: str
    section_title: str | None
    page_number: int | None
    token_count: int
    embedding_model: str | None
    embedding_dimensions: int | None
    embedding_status: str
    retrieval_enabled: bool
    script_enabled: bool
    qa_enabled: bool
    quality_status: str
    metadata: dict[str, Any]


class ScriptGenerateRequest(BaseModel):
    profile: Literal["beginner", "master", "professional"]
    language: Literal["ja", "en"] = "ja"
    replace_existing: bool = True
    use_openai: bool = True
    simulated_question_count: int = Field(default=6, ge=0, le=12)


class ScriptSegmentUpdate(BaseModel):
    text: str | None = Field(default=None, min_length=1)
    status: Literal["draft", "approved", "needs_review"] | None = None


class ScriptSegmentRead(BaseModel):
    id: str
    session_id: str
    profile: str
    language: str
    panel_id: str | None
    segment_order: int
    segment_type: str
    text: str
    evidence_chunk_ids: list[str]
    status: str
    tts_asset_id: str | None
    duration_estimate_sec: int | None


class VoiceSettingsPayload(BaseModel):
    stability: float | None = Field(default=None, ge=0.0, le=1.0)
    similarity_boost: float | None = Field(default=None, ge=0.0, le=1.0)
    style: float | None = Field(default=None, ge=0.0, le=1.0)
    use_speaker_boost: bool | None = None


class TtsGenerateRequest(BaseModel):
    voice_id: str | None = None
    model_id: str | None = None
    consent_confirmed: bool = False
    consent_label: str | None = Field(default=None, max_length=255)
    voice_settings: VoiceSettingsPayload | None = None


class TtsBatchGenerateRequest(BaseModel):
    profile: Literal["beginner", "master", "professional"] | None = None
    language: Literal["ja", "en"] | None = None
    voice_id: str | None = None
    model_id: str | None = None
    consent_confirmed: bool = False
    consent_label: str | None = Field(default=None, max_length=255)
    voice_settings: VoiceSettingsPayload | None = None


class TtsBatchResultRead(BaseModel):
    synthesized: list[dict[str, Any]]
    skipped: list[dict[str, Any]]
    failed: list[dict[str, Any]]


class TtsAssetRead(BaseModel):
    id: str
    segment_id: str
    asset_id: str
    provider: str
    voice_id: str
    model_id: str
    language: str
    audio_url: str
    expires_at: int


class VoiceSampleCreate(BaseModel):
    voice_id: str = Field(min_length=1, max_length=128)
    text: str | None = Field(
        default=None,
        max_length=500,
        description="Sample text to read. Falls back to a Japanese default phrase if omitted.",
    )
    language: str = Field(default="ja", pattern="^(ja|en)$")


class VoiceSampleRead(BaseModel):
    id: str
    voice_id: str
    text: str
    language: str
    model_id: str
    audio_url: str
    expires_at: int
    cached: bool  # True when this request was served from cache (no ElevenLabs call)
    created_at: datetime


class KnowledgeUploadRead(BaseModel):
    document: KnowledgeDocumentRead
    chunks_created: int
    extracted_chars: int
    extraction_method: str


class VoiceConsentRead(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: str
    session_id: str
    voice_id: str
    provider: str
    confirmed_by_user_id: str
    consent_label: str
    notes: str | None


class RetrievalPreviewRequest(BaseModel):
    question: str = Field(min_length=1)
    profile: Literal["beginner", "master", "professional"] = "master"
    language: Literal["ja", "en"] = "ja"
    active_panel_id: str | None = None
    top_k: int = Field(default=6, ge=1, le=12)


class RetrievalPreviewChunkRead(BaseModel):
    chunk_id: str
    document_id: str
    panel_id: str | None
    score: float
    text: str
    page_number: int | None = None
    metadata: dict[str, Any]


class RetrievalQaMatchRead(BaseModel):
    qa_id: str
    question: str
    answer: str
    score: float
    panel_id: str | None
    evidence_chunk_ids: list[str]


class RetrievalPreviewRead(BaseModel):
    answerability: Literal["answerable", "weak", "escalate"]
    reason: str
    draft_answer: str
    chunks: list[RetrievalPreviewChunkRead]
    qa_match: RetrievalQaMatchRead | None = None


class AvatarConfigUpdate(BaseModel):
    runtime_type: Literal["default-vrm", "gvrm", "scaniverse-source"] | None = None
    display_name: str | None = Field(default=None, max_length=255)
    attribution: str | None = Field(default=None, max_length=512)
    license_note: str | None = Field(default=None, max_length=512)
    placement_position: list[float] | None = Field(default=None, min_length=3, max_length=3)
    placement_rotation: list[float] | None = Field(default=None, min_length=3, max_length=3)
    placement_scale: float | None = Field(default=None, gt=0.0, le=10.0)
    avatar_height_scale: float | None = Field(default=None, ge=0.5, le=2.0)
    voice_id: str | None = Field(default=None, max_length=128)
    base_vrm_preset: Literal["fem_vroid", "masc_vroid"] | None = Field(
        default=None,
        description="Pick a bundled CC0 base VRM. None falls back to the server default.",
    )
    default_animation: str | None = Field(default=None, max_length=80)
    behavior_idle: str | None = Field(default=None, max_length=80)
    behavior_explain: str | None = Field(default=None, max_length=80)
    behavior_listening: str | None = Field(default=None, max_length=80)
    behavior_thinking: str | None = Field(default=None, max_length=80)


class AvatarConfigRead(BaseModel):
    session_id: str
    runtime_type: str
    gvrm_asset_id: str | None
    # Short-lived signed download URL for the .gvrm artifact, computed
    # server-side when gvrm_asset_id is set. Lets the dashboard preview
    # fetch the bundle without a separate manifest call. None when no
    # build has succeeded yet.
    gvrm_url: str | None = None
    gvrm_url_expires_at: int | None = None
    # Build-version flag baked into data.json. "server-v1" means the
    # binding is fully computed; "stub-v1" means the runtime has to
    # compute splat→bone bindings on first load. Used by the UI to flag
    # degraded builds.
    gvrm_build_version: str | None = None
    # Server's CURRENT preprocess algorithm version. The dashboard
    # compares `gvrm_build_version != expected_build_version` and shows
    # a "rebuild recommended" banner when stale. We surface it on the
    # config (rather than a separate /server-info endpoint) so the
    # banner can render off a single fetch.
    expected_build_version: str | None = None
    source_ply_asset_id: str | None
    source_vrm_asset_id: str | None
    display_name: str | None
    attribution: str | None
    license_note: str | None
    placement_position: list[float]
    placement_rotation: list[float]
    placement_scale: float
    avatar_height_scale: float
    voice_id: str | None
    base_vrm_preset: str | None
    default_animation: str
    behavior_idle: str
    behavior_explain: str
    behavior_listening: str
    behavior_thinking: str
    metadata: dict[str, Any] | None


class AvatarUploadRead(BaseModel):
    config: AvatarConfigRead
    asset: AssetRead
    metadata: dict[str, Any]


class AvatarSourceUploadRead(BaseModel):
    config: AvatarConfigRead
    asset: AssetRead
    role: Literal["scaniverse-ply", "scaniverse-spz", "base-vrm"]


class AvatarBuildJobRead(BaseModel):
    id: str
    session_id: str
    source_ply_asset_id: str | None
    source_vrm_asset_id: str | None
    output_asset_id: str | None
    status: Literal["queued", "running", "succeeded", "failed"]
    progress: int
    log: str | None
    error: str | None
    requested_by: str | None
    created_at: str | None
    updated_at: str | None


class SimulatedQaUpdate(BaseModel):
    status: Literal["draft", "approved", "rejected"] | None = None
    question: str | None = Field(default=None, min_length=1)
    answer: str | None = Field(default=None, min_length=1)


class SimulatedQaRead(BaseModel):
    id: str
    session_id: str
    profile: str
    language: str
    panel_id: str | None
    question: str
    answer: str
    evidence_chunk_ids: list[str]
    status: str
    source: str
