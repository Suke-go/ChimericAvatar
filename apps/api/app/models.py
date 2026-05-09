from datetime import datetime

from sqlalchemy import Boolean, DateTime, ForeignKey, Integer, String, Text, UniqueConstraint, func
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.db import Base


class AppUser(Base):
    __tablename__ = "app_users"

    user_id: Mapped[str] = mapped_column(String(64), primary_key=True)
    role: Mapped[str] = mapped_column(String(16))
    display_name: Mapped[str | None] = mapped_column(String(120), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())


class SessionModel(Base):
    __tablename__ = "sessions"

    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    owner_id: Mapped[str] = mapped_column(String(64), index=True)
    title: Mapped[str] = mapped_column(String(255))
    abstract: Mapped[str | None] = mapped_column(Text, nullable=True)
    event_name: Mapped[str | None] = mapped_column(String(255), nullable=True)
    # Presenter identity used in opening segment + TTS pronunciation
    presenter_name: Mapped[str | None] = mapped_column(String(120), nullable=True)
    presenter_name_kana: Mapped[str | None] = mapped_column(String(120), nullable=True)
    presenter_affiliation: Mapped[str | None] = mapped_column(String(255), nullable=True)
    status: Mapped[str] = mapped_column(String(32), default="draft")
    session_code: Mapped[str] = mapped_column(String(12), unique=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), server_default=func.now(), onupdate=func.now()
    )

    poster_config: Mapped["PosterConfig | None"] = relationship(back_populates="session", uselist=False)


class Asset(Base):
    __tablename__ = "assets"

    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    session_id: Mapped[str] = mapped_column(ForeignKey("sessions.id", ondelete="CASCADE"), index=True)
    kind: Mapped[str] = mapped_column(String(64))
    storage_path: Mapped[str] = mapped_column(String(512))
    file_name: Mapped[str] = mapped_column(String(255))
    mime_type: Mapped[str | None] = mapped_column(String(120), nullable=True)
    size_bytes: Mapped[int | None] = mapped_column(Integer, nullable=True)
    status: Mapped[str] = mapped_column(String(32), default="uploaded")
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())


class PosterConfig(Base):
    __tablename__ = "poster_configs"

    session_id: Mapped[str] = mapped_column(
        ForeignKey("sessions.id", ondelete="CASCADE"), primary_key=True
    )
    poster_asset_id: Mapped[str | None] = mapped_column(ForeignKey("assets.id"), nullable=True)
    poster_format: Mapped[str] = mapped_column(String(16), default="A0")
    orientation: Mapped[str] = mapped_column(String(16), default="portrait")
    physical_width_m: Mapped[float] = mapped_column()
    physical_height_m: Mapped[float] = mapped_column()
    tracking_reference_name: Mapped[str | None] = mapped_column(String(255), nullable=True)
    qr_fallback_enabled: Mapped[bool] = mapped_column(Boolean, default=True)
    qr_asset_id: Mapped[str | None] = mapped_column(ForeignKey("assets.id"), nullable=True)

    session: Mapped[SessionModel] = relationship(back_populates="poster_config")


class PosterPanel(Base):
    __tablename__ = "poster_panels"
    __table_args__ = (UniqueConstraint("session_id", "label", name="uq_poster_panels_session_label"),)

    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    session_id: Mapped[str] = mapped_column(ForeignKey("sessions.id", ondelete="CASCADE"), index=True)
    label: Mapped[str] = mapped_column(String(80))
    order_index: Mapped[int] = mapped_column(Integer)
    x: Mapped[float] = mapped_column()
    y: Mapped[float] = mapped_column()
    width: Mapped[float] = mapped_column()
    height: Mapped[float] = mapped_column()
    # Auto-filled with text whose bbox overlaps this panel, sourced from
    # poster_text_blocks. Recomputed on create/update.
    text_content: Mapped[str | None] = mapped_column(Text, nullable=True)


class PosterTextBlock(Base):
    """Text + page-relative bbox extracted from a poster PDF on upload.
    Normalized to [0, 1] coordinates so they can be matched against PosterPanel
    rectangles regardless of rendered DPI."""
    __tablename__ = "poster_text_blocks"

    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    session_id: Mapped[str] = mapped_column(ForeignKey("sessions.id", ondelete="CASCADE"), index=True)
    asset_id: Mapped[str] = mapped_column(ForeignKey("assets.id", ondelete="CASCADE"), index=True)
    page_number: Mapped[int] = mapped_column(Integer, default=1)
    order_index: Mapped[int] = mapped_column(Integer, default=0)
    x: Mapped[float] = mapped_column()
    y: Mapped[float] = mapped_column()
    width: Mapped[float] = mapped_column()
    height: Mapped[float] = mapped_column()
    text: Mapped[str] = mapped_column(Text)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())


class PresentationCue(Base):
    __tablename__ = "presentation_cues"

    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    session_id: Mapped[str] = mapped_column(ForeignKey("sessions.id", ondelete="CASCADE"), index=True)
    segment_id: Mapped[str | None] = mapped_column(nullable=True)
    cue_type: Mapped[str] = mapped_column(String(32))
    start_ms: Mapped[int] = mapped_column(Integer)
    duration_ms: Mapped[int | None] = mapped_column(Integer, nullable=True)
    payload_json: Mapped[str] = mapped_column(Text, default="{}")
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())


class KnowledgeDocument(Base):
    __tablename__ = "knowledge_documents"

    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    session_id: Mapped[str] = mapped_column(ForeignKey("sessions.id", ondelete="CASCADE"), index=True)
    asset_id: Mapped[str | None] = mapped_column(ForeignKey("assets.id"), nullable=True)
    kind: Mapped[str] = mapped_column(String(32))
    title: Mapped[str | None] = mapped_column(String(255), nullable=True)
    text: Mapped[str | None] = mapped_column(Text, nullable=True)
    language: Mapped[str] = mapped_column(String(8), default="ja")
    status: Mapped[str] = mapped_column(String(32), default="ready")
    trust_level: Mapped[str] = mapped_column(String(32), default="primary")
    use_for_script: Mapped[bool] = mapped_column(Boolean, default=True)
    use_for_live_qa: Mapped[bool] = mapped_column(Boolean, default=True)
    visibility: Mapped[str] = mapped_column(String(32), default="runtime")
    source_url: Mapped[str | None] = mapped_column(String(512), nullable=True)
    citation_label: Mapped[str | None] = mapped_column(String(255), nullable=True)
    reviewed_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())


class KnowledgeChunk(Base):
    __tablename__ = "knowledge_chunks"

    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    session_id: Mapped[str] = mapped_column(ForeignKey("sessions.id", ondelete="CASCADE"), index=True)
    document_id: Mapped[str] = mapped_column(ForeignKey("knowledge_documents.id", ondelete="CASCADE"), index=True)
    panel_id: Mapped[str | None] = mapped_column(ForeignKey("poster_panels.id"), nullable=True)
    chunk_order: Mapped[int] = mapped_column(Integer)
    text: Mapped[str] = mapped_column(Text)
    language: Mapped[str] = mapped_column(String(8), default="ja")
    section_title: Mapped[str | None] = mapped_column(String(255), nullable=True)
    page_number: Mapped[int | None] = mapped_column(Integer, nullable=True)
    token_count: Mapped[int] = mapped_column(Integer, default=0)
    embedding_model: Mapped[str | None] = mapped_column(String(80), nullable=True)
    embedding_dimensions: Mapped[int | None] = mapped_column(Integer, nullable=True)
    embedding_status: Mapped[str] = mapped_column(String(32), default="pending")
    embedding_json: Mapped[str | None] = mapped_column(Text, nullable=True)
    retrieval_enabled: Mapped[bool] = mapped_column(Boolean, default=True)
    script_enabled: Mapped[bool] = mapped_column(Boolean, default=True)
    qa_enabled: Mapped[bool] = mapped_column(Boolean, default=True)
    quality_status: Mapped[str] = mapped_column(String(32), default="unreviewed")
    metadata_json: Mapped[str] = mapped_column(Text, default="{}")
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())


class EvidenceClaim(Base):
    __tablename__ = "evidence_claims"

    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    session_id: Mapped[str] = mapped_column(ForeignKey("sessions.id", ondelete="CASCADE"), index=True)
    claim: Mapped[str] = mapped_column(Text)
    support_status: Mapped[str] = mapped_column(String(32))
    evidence_chunk_ids_json: Mapped[str] = mapped_column(Text, default="[]")
    limitation: Mapped[str | None] = mapped_column(Text, nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())


class ScriptSegment(Base):
    __tablename__ = "script_segments"

    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    session_id: Mapped[str] = mapped_column(ForeignKey("sessions.id", ondelete="CASCADE"), index=True)
    profile: Mapped[str] = mapped_column(String(32), index=True)
    language: Mapped[str] = mapped_column(String(8), index=True)
    panel_id: Mapped[str | None] = mapped_column(ForeignKey("poster_panels.id"), nullable=True)
    segment_order: Mapped[int] = mapped_column(Integer)
    segment_type: Mapped[str] = mapped_column(String(32))
    text: Mapped[str] = mapped_column(Text)
    evidence_chunk_ids_json: Mapped[str] = mapped_column(Text, default="[]")
    status: Mapped[str] = mapped_column(String(32), default="draft")
    tts_asset_id: Mapped[str | None] = mapped_column(ForeignKey("tts_assets.id"), nullable=True)
    duration_estimate_sec: Mapped[int | None] = mapped_column(Integer, nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), server_default=func.now(), onupdate=func.now()
    )


class TtsAsset(Base):
    __tablename__ = "tts_assets"

    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    session_id: Mapped[str] = mapped_column(ForeignKey("sessions.id", ondelete="CASCADE"), index=True)
    segment_id: Mapped[str] = mapped_column(ForeignKey("script_segments.id", ondelete="CASCADE"), index=True)
    asset_id: Mapped[str] = mapped_column(ForeignKey("assets.id"), index=True)
    provider: Mapped[str] = mapped_column(String(32), default="elevenlabs")
    voice_id: Mapped[str] = mapped_column(String(128))
    model_id: Mapped[str] = mapped_column(String(80))
    language: Mapped[str] = mapped_column(String(8))
    duration_sec: Mapped[float | None] = mapped_column(nullable=True)
    cache_key: Mapped[str] = mapped_column(String(64), index=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())


class VoiceConsent(Base):
    __tablename__ = "voice_consents"

    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    session_id: Mapped[str] = mapped_column(ForeignKey("sessions.id", ondelete="CASCADE"), index=True)
    voice_id: Mapped[str] = mapped_column(String(128), index=True)
    provider: Mapped[str] = mapped_column(String(32), default="elevenlabs")
    confirmed_by_user_id: Mapped[str] = mapped_column(String(64))
    consent_label: Mapped[str] = mapped_column(String(255))
    notes: Mapped[str | None] = mapped_column(Text, nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())


class VoiceSample(Base):
    """Cached ElevenLabs TTS preview keyed by (voice_id, text, lang, model).

    Decoupled from `tts_assets` so script-segment TTS caching stays
    untouched and previews can be reused across sessions: same voice + same
    text returns the same audio without burning ElevenLabs credits. Storage
    metadata (path / size) lives directly here rather than in the `assets`
    table because voice samples are GLOBAL — no session_id, no
    CASCADE-on-session-delete behavior."""

    __tablename__ = "voice_samples"

    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    cache_key: Mapped[str] = mapped_column(String(64), unique=True, index=True)
    voice_id: Mapped[str] = mapped_column(String(128), index=True)
    text: Mapped[str] = mapped_column(Text)
    language: Mapped[str] = mapped_column(String(8), default="ja")
    model_id: Mapped[str] = mapped_column(String(80))
    storage_path: Mapped[str] = mapped_column(String(512))
    size_bytes: Mapped[int] = mapped_column(Integer)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())


class AvatarBuildJob(Base):
    __tablename__ = "avatar_build_jobs"

    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    session_id: Mapped[str] = mapped_column(ForeignKey("sessions.id", ondelete="CASCADE"), index=True)
    source_ply_asset_id: Mapped[str | None] = mapped_column(ForeignKey("assets.id"), nullable=True)
    source_vrm_asset_id: Mapped[str | None] = mapped_column(ForeignKey("assets.id"), nullable=True)
    output_asset_id: Mapped[str | None] = mapped_column(ForeignKey("assets.id"), nullable=True)
    status: Mapped[str] = mapped_column(String(32), default="queued")  # queued|running|succeeded|failed
    progress: Mapped[int] = mapped_column(Integer, default=0)
    log: Mapped[str | None] = mapped_column(Text, nullable=True)
    error: Mapped[str | None] = mapped_column(Text, nullable=True)
    requested_by: Mapped[str | None] = mapped_column(String(64), nullable=True)
    # Server-side preprocess version baked into the produced .gvrm's data.json.
    # The dashboard compares this against the server's CURRENT BUILD_VERSION so
    # we can warn when an .gvrm is stale relative to the running preprocess
    # algorithm and recommend a rebuild.
    build_version: Mapped[str | None] = mapped_column(String(32), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), server_default=func.now(), onupdate=func.now()
    )


class AvatarConfig(Base):
    __tablename__ = "avatar_configs"

    session_id: Mapped[str] = mapped_column(
        ForeignKey("sessions.id", ondelete="CASCADE"), primary_key=True
    )
    runtime_type: Mapped[str] = mapped_column(String(32), default="default-vrm")
    gvrm_asset_id: Mapped[str | None] = mapped_column(ForeignKey("assets.id"), nullable=True)
    source_ply_asset_id: Mapped[str | None] = mapped_column(ForeignKey("assets.id"), nullable=True)
    source_vrm_asset_id: Mapped[str | None] = mapped_column(ForeignKey("assets.id"), nullable=True)
    metadata_json: Mapped[str | None] = mapped_column(Text, nullable=True)
    display_name: Mapped[str | None] = mapped_column(String(255), nullable=True)
    attribution: Mapped[str | None] = mapped_column(String(512), nullable=True)
    license_note: Mapped[str | None] = mapped_column(String(512), nullable=True)
    placement_position_json: Mapped[str] = mapped_column(Text, default="[0.65,-0.2,-0.25]")
    placement_rotation_json: Mapped[str] = mapped_column(Text, default="[0,-20,0]")
    placement_scale: Mapped[float] = mapped_column(default=1.0)
    # Multiplier baked into the .gvrm's data.json `modelScale` so the splat
    # avatar matches the user's actual height despite the bundled VRM having
    # fixed proportions (~165cm). 0.85 ≈ 140cm, 1.15 ≈ 190cm. Both Unity and
    # naruya/gvrm.js read modelScale, so adjustments propagate to all
    # consumers without per-renderer code.
    avatar_height_scale: Mapped[float] = mapped_column(default=1.0)
    # ElevenLabs voice ID for this session's TTS. Operators clone or pick a
    # voice in the ElevenLabs Dashboard, then paste the ID here. Falls back
    # to settings.elevenlabs_default_voice_id when null.
    voice_id: Mapped[str | None] = mapped_column(String(128), nullable=True)
    # Bundled base VRM choice. "fem_vroid" / "masc_vroid" / None (= use
    # settings.avatar_default_vrm_filename). Custom uploaded VRM still wins
    # when source_vrm_asset_id is set.
    base_vrm_preset: Mapped[str | None] = mapped_column(String(64), nullable=True)
    default_animation: Mapped[str] = mapped_column(String(80), default="Idle")
    behavior_idle: Mapped[str] = mapped_column(String(80), default="Idle")
    behavior_explain: Mapped[str] = mapped_column(String(80), default="Pointing")
    behavior_listening: Mapped[str] = mapped_column(String(80), default="Listening")
    behavior_thinking: Mapped[str] = mapped_column(String(80), default="Breathing")
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), server_default=func.now(), onupdate=func.now()
    )


class SimulatedQa(Base):
    __tablename__ = "simulated_qas"

    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    session_id: Mapped[str] = mapped_column(ForeignKey("sessions.id", ondelete="CASCADE"), index=True)
    profile: Mapped[str] = mapped_column(String(32), index=True)
    language: Mapped[str] = mapped_column(String(8), index=True)
    panel_id: Mapped[str | None] = mapped_column(ForeignKey("poster_panels.id"), nullable=True)
    question: Mapped[str] = mapped_column(Text)
    answer: Mapped[str] = mapped_column(Text)
    evidence_chunk_ids_json: Mapped[str] = mapped_column(Text, default="[]")
    status: Mapped[str] = mapped_column(String(32), default="draft")
    source: Mapped[str] = mapped_column(String(32), default="generated")
    question_embedding_json: Mapped[str | None] = mapped_column(Text, nullable=True)
    embedding_model: Mapped[str | None] = mapped_column(String(80), nullable=True)
    embedding_status: Mapped[str] = mapped_column(String(32), default="pending")
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), server_default=func.now(), onupdate=func.now()
    )
