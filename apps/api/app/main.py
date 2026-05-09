import json
from datetime import UTC, datetime
from uuid import uuid4

from fastapi import BackgroundTasks, Depends, FastAPI, File, Form, HTTPException, Query, Request, UploadFile, WebSocket, WebSocketDisconnect, status
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse
from sqlalchemy.orm import Session
from sqlalchemy import inspect, select, text

from app.auth import CurrentUser, get_current_user
from app.core.config import get_settings
from app.db import Base, engine, get_db
from app.models import (
    Asset,
    AvatarConfig,
    PosterConfig,
    PosterPanel,
    PresentationCue,
    ScriptSegment,
    SessionModel,
    SimulatedQa,
    TtsAsset,
)
from app.schemas import (
    AssetRead,
    AssetDownloadUrlRead,
    AvatarBuildJobRead,
    AvatarConfigRead,
    AvatarConfigUpdate,
    AvatarSourceUploadRead,
    AvatarUploadRead,
    CueListUpdate,
    CueRead,
    CurrentUserRead,
    KnowledgeChunkRead,
    KnowledgeDocumentDetailRead,
    KnowledgeDocumentRead,
    KnowledgeTextCreate,
    KnowledgeUploadRead,
    PosterConfigRead,
    PosterConfigUpdate,
    PosterPanelCreate,
    PosterPanelRead,
    PosterPanelUpdate,
    PosterTextBlockRead,
    RetrievalPreviewRead,
    RetrievalPreviewRequest,
    ScriptGenerateRequest,
    ScriptSegmentRead,
    ScriptSegmentUpdate,
    SessionCreate,
    SessionRead,
    SessionUpdate,
    SimulatedQaRead,
    SimulatedQaUpdate,
    TtsAssetRead,
    TtsBatchGenerateRequest,
    TtsBatchResultRead,
    TtsGenerateRequest,
    VoiceConsentRead,
    VoiceSampleCreate,
    VoiceSampleRead,
)
from app.service import (
    avatar_build_job_to_read,
    avatar_config_to_read,
    create_session,
    enqueue_avatar_build,
    get_avatar_build_job,
    list_avatar_build_jobs,
    list_poster_text_blocks,
    poster_text_block_to_read,
    rebind_chunks_to_panel,
    recompute_panel_text,
    create_knowledge_text,
    create_runtime_token,
    delete_knowledge_document,
    signed_asset_url,
    verify_runtime_token,
    embed_document_chunks,
    embed_simulated_qa_questions,
    ensure_avatar_config,
    generate_script_agent,
    generate_simulated_qas_agent,
    get_knowledge_document,
    ensure_session_access,
    list_accessible_sessions,
    list_document_chunks,
    list_knowledge_chunks,
    list_knowledge_documents,
    list_panels,
    list_script_segments,
    list_simulated_qas,
    list_voice_consents,
    get_or_create_voice_sample,
    signed_voice_sample_url,
    knowledge_chunk_to_read,
    replace_cues,
    retrieval_preview,
    script_segment_to_read,
    simulated_qa_to_read,
    synthesize_all_session_tts,
    synthesize_segment_tts,
    update_avatar_config,
    upload_avatar_source,
    upload_gvrm_archive,
    upload_knowledge_asset,
    update_poster_config,
    update_script_segment,
    update_simulated_qa,
    upload_poster_asset,
)
from app.storage import LocalStorage, get_storage, verify_local_download_token

settings = get_settings()


# Starlette 1.0.0 lowered Request.form()'s `max_part_size` default to 1MB,
# which makes FastAPI auto-parsed UploadFile bodies (e.g. avatar PLY/VRM,
# knowledge PDF) 413 the moment a single part crosses 1MB. We need 256MB
# (= MAX_AVATAR_UPLOAD_BYTES) for avatar artifacts. Patch the default.
def _install_form_max_part_size_patch() -> None:
    import starlette.requests as _sr

    _real_form = _sr.Request.form
    cap = settings.max_avatar_upload_bytes

    def _form(self, *, max_files=1000, max_fields=1000, max_part_size=cap):  # noqa: ANN001
        return _real_form(
            self,
            max_files=max_files,
            max_fields=max_fields,
            max_part_size=max_part_size,
        )

    _sr.Request.form = _form  # type: ignore[method-assign]


_install_form_max_part_size_patch()


app = FastAPI(title=settings.api_title)
app.add_middleware(
    CORSMiddleware,
    allow_origins=settings.cors_allow_origins,
    allow_credentials=False,
    allow_methods=["*"],
    allow_headers=["*"],
)


def _ensure_sqlite_column(table_name: str, column_name: str, definition: str) -> None:
    inspector = inspect(engine)
    columns = {column["name"] for column in inspector.get_columns(table_name)}
    if column_name in columns:
        return
    with engine.begin() as connection:
        connection.execute(text(f"alter table {table_name} add column {column_name} {definition}"))


def _ensure_postgres_schema() -> None:
    settings_local = get_settings()
    embedding_dim = settings_local.openai_embedding_dimensions
    with engine.begin() as connection:
        connection.execute(text("create extension if not exists vector"))
        connection.execute(
            text(
                f"alter table knowledge_chunks "
                f"add column if not exists embedding vector({embedding_dim})"
            )
        )
        connection.execute(
            text(
                "create index if not exists knowledge_chunks_embedding_ivfflat "
                "on knowledge_chunks using ivfflat (embedding vector_cosine_ops)"
            )
        )


def _ensure_local_sqlite_schema() -> None:
    _ensure_sqlite_column("knowledge_documents", "trust_level", "varchar(32) not null default 'primary'")
    _ensure_sqlite_column("knowledge_documents", "use_for_script", "boolean not null default 1")
    _ensure_sqlite_column("knowledge_documents", "use_for_live_qa", "boolean not null default 1")
    _ensure_sqlite_column("knowledge_documents", "visibility", "varchar(32) not null default 'runtime'")
    _ensure_sqlite_column("knowledge_documents", "source_url", "varchar(512)")
    _ensure_sqlite_column("knowledge_documents", "citation_label", "varchar(255)")
    _ensure_sqlite_column("knowledge_documents", "reviewed_at", "datetime")
    _ensure_sqlite_column("knowledge_chunks", "section_title", "varchar(255)")
    _ensure_sqlite_column("knowledge_chunks", "page_number", "integer")
    _ensure_sqlite_column("knowledge_chunks", "token_count", "integer not null default 0")
    _ensure_sqlite_column("knowledge_chunks", "embedding_model", "varchar(80)")
    _ensure_sqlite_column("knowledge_chunks", "embedding_dimensions", "integer")
    _ensure_sqlite_column("knowledge_chunks", "embedding_status", "varchar(32) not null default 'pending'")
    _ensure_sqlite_column("knowledge_chunks", "embedding_json", "text")
    _ensure_sqlite_column("knowledge_chunks", "retrieval_enabled", "boolean not null default 1")
    _ensure_sqlite_column("knowledge_chunks", "script_enabled", "boolean not null default 1")
    _ensure_sqlite_column("knowledge_chunks", "qa_enabled", "boolean not null default 1")
    _ensure_sqlite_column("knowledge_chunks", "quality_status", "varchar(32) not null default 'unreviewed'")
    _ensure_sqlite_column("poster_panels", "text_content", "text")
    _ensure_sqlite_column("sessions", "presenter_name", "varchar(120)")
    _ensure_sqlite_column("sessions", "presenter_name_kana", "varchar(120)")
    _ensure_sqlite_column("sessions", "presenter_affiliation", "varchar(255)")
    if "simulated_qas" in {t for t in inspect(engine).get_table_names()}:
        _ensure_sqlite_column("simulated_qas", "question_embedding_json", "text")
        _ensure_sqlite_column("simulated_qas", "embedding_model", "varchar(80)")
        _ensure_sqlite_column("simulated_qas", "embedding_status", "varchar(32) not null default 'pending'")


@app.on_event("startup")
def on_startup() -> None:
    if settings.app_environment != "local":
        if settings.debug_auth_enabled:
            raise RuntimeError("DEBUG_AUTH_ENABLED must be false outside local environment")
        if settings.asset_url_secret == "local-dev-asset-secret-change-me":
            raise RuntimeError("ASSET_URL_SECRET must be set outside local environment")
        if "*" in settings.cors_allow_origins:
            raise RuntimeError("CORS allowlist must not contain '*' outside local environment")
        if settings.storage_backend == "supabase":
            if not settings.supabase_url or not (settings.supabase_secret_key or settings.supabase_service_role_key):
                raise RuntimeError(
                    "STORAGE_BACKEND=supabase requires SUPABASE_URL and SUPABASE_SECRET_KEY "
                    "(preferred) or legacy SUPABASE_SERVICE_ROLE_KEY"
                )
    settings.upload_root.mkdir(parents=True, exist_ok=True)
    dialect = engine.dialect.name
    if dialect == "sqlite":
        # Local dev: create tables directly. Production-grade migration is via
        # `alembic upgrade head` (run by the Docker entrypoint).
        Base.metadata.create_all(bind=engine)
        _ensure_local_sqlite_schema()
    elif dialect == "postgresql":
        # First-time bootstrap. The auto-generated initial alembic migration
        # has a circular FK ordering issue (`script_segments` <-> `tts_assets`
        # cross-FK confuses topological sort), so on a fresh DB we use
        # SQLAlchemy `create_all` which handles the cycle via use_alter, then
        # stamp alembic head. Subsequent migrations apply normally via
        # `alembic upgrade head` in the Docker entrypoint.
        _bootstrap_postgres_if_fresh()
        # pgvector extension + dynamic-dim embedding column aren't
        # expressible via SQLAlchemy standard column types.
        _ensure_postgres_schema()


def _bootstrap_postgres_if_fresh() -> None:
    """Bootstrap or upgrade the Postgres schema at app startup.

    Two cases:
    1. **Fresh DB** (no alembic_version): SQLAlchemy `create_all` builds the
       schema (handles the script_segments<->tts_assets circular FK via
       use_alter), then alembic stamps head so future migrations apply
       normally.
    2. **Existing DB**: run `alembic upgrade head` for incremental migrations.

    Replaces the Dockerfile entrypoint's `alembic upgrade head &&` step,
    which fails on case 1 due to a known bug in the auto-generated initial
    migration's table ordering.
    """
    import logging
    from alembic.config import Config as _AlembicConfig
    from alembic import command as _alembic_command

    logger = logging.getLogger(__name__)
    inspector = inspect(engine)
    cfg = _AlembicConfig("alembic.ini")
    if "alembic_version" not in inspector.get_table_names():
        logger.warning(
            "Fresh Postgres detected. Running create_all + alembic stamp head."
        )
        Base.metadata.create_all(bind=engine)
        _alembic_command.stamp(cfg, "head")
        logger.warning("Postgres bootstrap complete.")
    else:
        logger.info("Existing Postgres detected. Applying alembic upgrade head.")
        _alembic_command.upgrade(cfg, "head")


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}


@app.get(f"{settings.api_prefix}/me", response_model=CurrentUserRead)
def read_me(current_user: CurrentUser = Depends(get_current_user)) -> CurrentUserRead:
    return CurrentUserRead(user_id=current_user.user_id, role=current_user.role)


@app.get(f"{settings.api_prefix}/sessions", response_model=list[SessionRead])
def read_sessions(
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> list[SessionRead]:
    return [SessionRead.model_validate(item) for item in list_accessible_sessions(db, current_user)]


@app.post(f"{settings.api_prefix}/sessions", response_model=SessionRead)
def create_session_endpoint(
    payload: SessionCreate,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> SessionRead:
    session = create_session(
        db,
        current_user,
        payload.title,
        payload.abstract,
        payload.event_name,
        presenter_name=payload.presenter_name,
        presenter_name_kana=payload.presenter_name_kana,
        presenter_affiliation=payload.presenter_affiliation,
    )
    return SessionRead.model_validate(session)


@app.get(f"{settings.api_prefix}/sessions/{{session_id}}", response_model=SessionRead)
def read_session(
    session_id: str,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> SessionRead:
    session = ensure_session_access(db, session_id, current_user)
    return SessionRead.model_validate(session)


@app.patch(f"{settings.api_prefix}/sessions/{{session_id}}", response_model=SessionRead)
def update_session(
    session_id: str,
    payload: SessionUpdate,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> SessionRead:
    session = ensure_session_access(db, session_id, current_user)
    for field, value in payload.model_dump(exclude_unset=True).items():
        setattr(session, field, value)
    db.commit()
    db.refresh(session)
    return SessionRead.model_validate(session)


@app.get(f"{settings.api_prefix}/sessions/{{session_id}}/poster", response_model=PosterConfigRead)
def read_poster(
    session_id: str,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> PosterConfigRead:
    ensure_session_access(db, session_id, current_user)
    poster = db.get(PosterConfig, session_id)
    if poster is None:
        raise HTTPException(status_code=404, detail="Poster config not found")
    return PosterConfigRead.model_validate(poster)


@app.patch(f"{settings.api_prefix}/sessions/{{session_id}}/poster", response_model=PosterConfigRead)
def patch_poster(
    session_id: str,
    payload: PosterConfigUpdate,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> PosterConfigRead:
    ensure_session_access(db, session_id, current_user)
    poster = update_poster_config(db, session_id, payload.orientation, payload.qr_fallback_enabled)
    return PosterConfigRead.model_validate(poster)


@app.post(f"{settings.api_prefix}/sessions/{{session_id}}/poster/upload", response_model=AssetRead)
def post_poster_upload(
    session_id: str,
    file: UploadFile = File(...),
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> AssetRead:
    ensure_session_access(db, session_id, current_user)
    asset = upload_poster_asset(db, session_id, file)
    return AssetRead.model_validate(asset)


@app.get(f"{settings.api_prefix}/sessions/{{session_id}}/avatar", response_model=AvatarConfigRead)
def read_avatar(
    session_id: str,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> AvatarConfigRead:
    ensure_session_access(db, session_id, current_user)
    config = ensure_avatar_config(db, session_id)
    return AvatarConfigRead(**avatar_config_to_read(config, db=db))


@app.patch(f"{settings.api_prefix}/sessions/{{session_id}}/avatar", response_model=AvatarConfigRead)
def patch_avatar(
    session_id: str,
    payload: AvatarConfigUpdate,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> AvatarConfigRead:
    ensure_session_access(db, session_id, current_user)
    config = update_avatar_config(db, session_id, payload.model_dump(exclude_unset=True))
    return AvatarConfigRead(**avatar_config_to_read(config, db=db))


@app.post(f"{settings.api_prefix}/sessions/{{session_id}}/avatar/upload-gvrm", response_model=AvatarUploadRead)
def post_avatar_gvrm_upload(
    session_id: str,
    file: UploadFile = File(...),
    display_name: str | None = Form(None),
    attribution: str | None = Form(None),
    license_note: str | None = Form(None),
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> AvatarUploadRead:
    ensure_session_access(db, session_id, current_user)
    config, asset, metadata = upload_gvrm_archive(db, session_id, file, display_name, attribution, license_note)
    return AvatarUploadRead(
        config=AvatarConfigRead(**avatar_config_to_read(config, db=db)),
        asset=AssetRead.model_validate(asset),
        metadata=metadata,
    )


@app.post(f"{settings.api_prefix}/sessions/{{session_id}}/avatar/upload-source", response_model=AvatarSourceUploadRead)
def post_avatar_source_upload(
    session_id: str,
    file: UploadFile = File(...),
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> AvatarSourceUploadRead:
    ensure_session_access(db, session_id, current_user)
    config, asset, role = upload_avatar_source(db, session_id, file)
    return AvatarSourceUploadRead(
        config=AvatarConfigRead(**avatar_config_to_read(config, db=db)),
        asset=AssetRead.model_validate(asset),
        role=role,
    )


@app.post(
    f"{settings.api_prefix}/sessions/{{session_id}}/avatar/build",
    response_model=AvatarBuildJobRead,
)
def post_avatar_build(
    session_id: str,
    background_tasks: BackgroundTasks,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> AvatarBuildJobRead:
    ensure_session_access(db, session_id, current_user)
    job = enqueue_avatar_build(db, session_id, current_user)
    # Process the build after the response is sent so the dashboard sees the
    # initial "queued" state immediately and can poll for progress.
    from app.avatar_build import run_build_in_background
    background_tasks.add_task(run_build_in_background, session_id, job.id)
    return AvatarBuildJobRead(**avatar_build_job_to_read(job))


@app.get(
    f"{settings.api_prefix}/sessions/{{session_id}}/avatar/builds",
    response_model=list[AvatarBuildJobRead],
)
def read_avatar_builds(
    session_id: str,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> list[AvatarBuildJobRead]:
    ensure_session_access(db, session_id, current_user)
    return [AvatarBuildJobRead(**avatar_build_job_to_read(job)) for job in list_avatar_build_jobs(db, session_id)]


@app.get(
    f"{settings.api_prefix}/sessions/{{session_id}}/avatar/builds/{{job_id}}",
    response_model=AvatarBuildJobRead,
)
def read_avatar_build(
    session_id: str,
    job_id: str,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> AvatarBuildJobRead:
    ensure_session_access(db, session_id, current_user)
    job = get_avatar_build_job(db, session_id, job_id)
    return AvatarBuildJobRead(**avatar_build_job_to_read(job))


@app.get(f"{settings.api_prefix}/sessions/{{session_id}}/poster/image-url", response_model=AssetDownloadUrlRead)
def read_poster_image_url(
    session_id: str,
    request: Request,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> AssetDownloadUrlRead:
    ensure_session_access(db, session_id, current_user)
    poster = db.get(PosterConfig, session_id)
    if poster is None or not poster.poster_asset_id:
        raise HTTPException(status_code=404, detail="Poster image not found")
    url, expires_at = signed_asset_url(db, poster.poster_asset_id)
    asset = db.get(Asset, poster.poster_asset_id)
    return AssetDownloadUrlRead(
        url=url,
        expires_at=expires_at,
        mime_type=asset.mime_type if asset else None,
    )


# Avatar artifacts (PLY/SPZ/VRM/.gvrm) are always local-backed (see
# storage.LOCAL_AVATAR_KINDS) so this download route must exist regardless
# of the configured STORAGE_BACKEND. Non-avatar local assets also use it.
@app.get(f"{settings.api_prefix}/assets/{{asset_id}}/download", name="download_asset")
def download_asset(
    asset_id: str,
    token: str,
    db: Session = Depends(get_db),
) -> FileResponse:
    verify_local_download_token(asset_id, token)
    asset = db.get(Asset, asset_id)
    if asset is None:
        raise HTTPException(status_code=404, detail="Asset not found")
    local = LocalStorage()
    file_path = local.resolve_public_path(asset.storage_path)
    return FileResponse(
        path=file_path,
        media_type=asset.mime_type or "application/octet-stream",
        filename=asset.file_name,
        content_disposition_type="inline",
    )


def _signed_asset_payload(request: Request, asset: Asset) -> dict:
    url, expires_at = get_storage().signed_download_url(asset.id, asset.storage_path)
    return {
        "id": asset.id,
        "url": url,
        "expiresAt": datetime.fromtimestamp(expires_at, UTC).isoformat().replace("+00:00", "Z"),
        "mimeType": asset.mime_type or "application/octet-stream",
        "sizeBytes": asset.size_bytes or 0,
    }


def _panel_manifest(panel: PosterPanel, poster: PosterConfig) -> dict:
    center_x_m = (panel.x + panel.width / 2.0 - 0.5) * poster.physical_width_m
    center_y_m = (0.5 - (panel.y + panel.height / 2.0)) * poster.physical_height_m
    return {
        "id": panel.id,
        "label": panel.label,
        "orderIndex": panel.order_index,
        "bbox": {
            "x": panel.x,
            "y": panel.y,
            "width": panel.width,
            "height": panel.height,
        },
        "localBoundsM": {
            "center": [round(center_x_m, 6), round(center_y_m, 6), 0],
            "size": [
                round(panel.width * poster.physical_width_m, 6),
                round(panel.height * poster.physical_height_m, 6),
            ],
        },
    }


def _websocket_url(request: Request, path: str) -> str:
    base = str(request.base_url).rstrip("/")
    if base.startswith("https://"):
        return "wss://" + base.removeprefix("https://") + path
    return "ws://" + base.removeprefix("http://") + path


def _build_runtime_manifest(db: Session, request: Request, session: SessionModel) -> dict:
    poster = db.get(PosterConfig, session.id)
    if poster is None:
        raise HTTPException(status_code=409, detail="Poster config is required before publishing")
    if not poster.poster_asset_id:
        raise HTTPException(status_code=409, detail="Poster image is required before publishing")
    poster_asset = db.get(Asset, poster.poster_asset_id)
    if poster_asset is None:
        raise HTTPException(status_code=409, detail="Poster asset is missing")
    panels = list_panels(db, session.id)
    if not panels:
        raise HTTPException(status_code=409, detail="At least one poster panel is required before publishing")
    cues = (
        db.query(PresentationCue)
        .filter(PresentationCue.session_id == session.id)
        .order_by(PresentationCue.start_ms.asc())
        .all()
    )
    segments = (
        db.query(ScriptSegment)
        .filter(ScriptSegment.session_id == session.id)
        .order_by(ScriptSegment.profile.asc(), ScriptSegment.language.asc(), ScriptSegment.segment_order.asc())
        .all()
    )
    approved_segments = [segment for segment in segments if segment.status == "approved"]
    if not approved_segments:
        raise HTTPException(status_code=409, detail="At least one approved script segment is required before publishing")
    issued_at = datetime.now(UTC)
    return {
        "schemaVersion": "1.0",
        "sessionCode": session.session_code,
        "issuedAt": issued_at.isoformat().replace("+00:00", "Z"),
        "expiresAt": datetime.fromtimestamp(
            int(issued_at.timestamp()) + settings.asset_url_ttl_seconds,
            UTC,
        ).isoformat().replace("+00:00", "Z"),
        "tracking": {
            "type": "xreal-image-tracking",
            "referenceImageName": poster.tracking_reference_name or f"poster_{session.session_code.lower()}_v1",
            "posterFormat": poster.poster_format,
            "orientation": poster.orientation,
            "physicalSizeM": [poster.physical_width_m, poster.physical_height_m],
            "qrFallback": {
                "enabled": poster.qr_fallback_enabled,
                "markerName": f"poster_{session.session_code.lower()}_qr_v1",
                "placementHint": "poster_corner",
            },
            "anchorOffset": {
                "position": [0, 0, 0],
                "rotation": [0, 0, 0],
            },
        },
        "assets": {
            "posterImage": _signed_asset_payload(request, poster_asset),
        },
        "posterPanels": [_panel_manifest(panel, poster) for panel in panels],
        "presentationCues": [
            {
                key: value
                for key, value in {
                    "id": cue.id,
                    "segmentId": cue.segment_id,
                    "type": cue.cue_type,
                    "startMs": cue.start_ms,
                    "durationMs": cue.duration_ms,
                    "payload": json.loads(cue.payload_json or "{}"),
                }.items()
                if value is not None
            }
            for cue in cues
        ],
        "avatar": _avatar_manifest(db, request, session.id),
        "scripts": _script_manifest(db, request, segments),
        "qa": _qa_manifest(request, session, db),
        "features": {
            "liveQa": True,
            "dashboardRemoteControl": False,
            "handTracking": False,
        },
    }


def _script_manifest(db: Session, request: Request, segments: list[ScriptSegment]) -> list[dict]:
    grouped: dict[tuple[str, str], list[ScriptSegment]] = {}
    for segment in segments:
        if segment.status != "approved":
            continue
        grouped.setdefault((segment.profile, segment.language), []).append(segment)
    scripts: list[dict] = []
    for (profile, language), items in grouped.items():
        script_segments: list[dict] = []
        for segment in items:
            payload = {
                "id": segment.id,
                "panelId": segment.panel_id,
                "order": segment.segment_order,
                "type": segment.segment_type,
                "text": segment.text,
                "durationEstimateSec": segment.duration_estimate_sec,
                "evidenceChunkIds": json.loads(segment.evidence_chunk_ids_json or "[]"),
            }
            if segment.tts_asset_id:
                tts = db.get(TtsAsset, segment.tts_asset_id)
                asset = db.get(Asset, tts.asset_id) if tts else None
                if tts and asset:
                    payload["audio"] = _signed_asset_payload(request, asset)
                    payload["voice"] = {
                        "provider": tts.provider,
                        "voiceId": tts.voice_id,
                        "modelId": tts.model_id,
                    }
            script_segments.append(payload)
        scripts.append({"profile": profile, "language": language, "segments": script_segments})
    return scripts


def _avatar_manifest(db: Session, request: Request, session_id: str) -> dict:
    config = ensure_avatar_config(db, session_id)
    read = avatar_config_to_read(config, db=db)
    manifest: dict = {
        "runtimeType": read["runtime_type"],
        "displayName": read["display_name"],
        "attribution": read["attribution"],
        "licenseNote": read["license_note"],
        "defaultAnimation": read["default_animation"],
        "behaviors": {
            "idle": read["behavior_idle"],
            "explain": read["behavior_explain"],
            "listening": read["behavior_listening"],
            "thinking": read["behavior_thinking"],
        },
        "placement": {
            "relativeTo": "poster_anchor",
            "position": read["placement_position"],
            "rotation": read["placement_rotation"],
            "scale": read["placement_scale"],
        },
    }
    if config.runtime_type == "gvrm" and config.gvrm_asset_id:
        gvrm_asset = db.get(Asset, config.gvrm_asset_id)
        if gvrm_asset is not None:
            manifest["assetBundle"] = _signed_asset_payload(request, gvrm_asset)
            manifest["format"] = "gaussian-vrm"
            if read["metadata"] is not None:
                manifest["metadata"] = read["metadata"]
    elif config.runtime_type == "scaniverse-source":
        sources: dict = {}
        if config.source_ply_asset_id:
            ply_asset = db.get(Asset, config.source_ply_asset_id)
            if ply_asset is not None:
                role_key = "spz" if ply_asset.kind == "avatar-scaniverse-spz" else "ply"
                sources[role_key] = _signed_asset_payload(request, ply_asset)
        if config.source_vrm_asset_id:
            vrm_asset = db.get(Asset, config.source_vrm_asset_id)
            if vrm_asset is not None:
                sources["baseVrm"] = _signed_asset_payload(request, vrm_asset)
        if sources:
            manifest["sources"] = sources
            manifest["format"] = "scaniverse-source"
    return manifest


def _qa_manifest(request: Request, session: SessionModel, db: Session) -> dict:
    runtime_token, runtime_token_expires = create_runtime_token(session.session_code)
    ws_path = f"/api/v1/runtime/{session.session_code}/questions"
    return {
        "websocketUrl": _websocket_url(request, ws_path),
        "runtimeToken": runtime_token,
        "runtimeTokenExpiresAt": datetime.fromtimestamp(runtime_token_expires, UTC).isoformat().replace("+00:00", "Z"),
        "defaultProfile": "master",
        "defaultLanguage": "ja",
        "supportedProfiles": ["beginner", "master", "professional"],
        "supportedLanguages": ["ja", "en"],
        "allowEscalation": True,
        "approvedSimulatedQas": _simulated_qa_manifest(db, session.id),
    }


def _simulated_qa_manifest(db: Session, session_id: str) -> list[dict]:
    items = (
        db.query(SimulatedQa)
        .filter(SimulatedQa.session_id == session_id, SimulatedQa.status == "approved")
        .order_by(SimulatedQa.profile.asc(), SimulatedQa.language.asc(), SimulatedQa.created_at.asc())
        .all()
    )
    return [
        {
            "id": item.id,
            "profile": item.profile,
            "language": item.language,
            "panelId": item.panel_id,
            "question": item.question,
            "answer": item.answer,
            "evidenceChunkIds": json.loads(item.evidence_chunk_ids_json or "[]"),
        }
        for item in items
    ]


@app.post(f"{settings.api_prefix}/sessions/{{session_id}}/publish", response_model=SessionRead)
def publish_session(
    session_id: str,
    request: Request,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> SessionRead:
    session = ensure_session_access(db, session_id, current_user)
    _build_runtime_manifest(db, request, session)
    session.status = "published"
    db.commit()
    db.refresh(session)
    return SessionRead.model_validate(session)


@app.get(f"{settings.api_prefix}/sessions/{{session_id}}/manifest")
def read_session_manifest(
    session_id: str,
    request: Request,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> dict:
    session = ensure_session_access(db, session_id, current_user)
    return _build_runtime_manifest(db, request, session)


@app.get(f"{settings.api_prefix}/runtime/{{session_code}}/manifest")
def read_runtime_manifest(
    session_code: str,
    request: Request,
    db: Session = Depends(get_db),
) -> dict:
    stmt = select(SessionModel).where(SessionModel.session_code == session_code)
    session = db.scalars(stmt).first()
    if session is None:
        raise HTTPException(status_code=404, detail="Session not found")
    if session.status != "published":
        raise HTTPException(status_code=403, detail="Session is not published")
    return _build_runtime_manifest(db, request, session)


@app.get(f"{settings.api_prefix}/sessions/{{session_id}}/poster/panels", response_model=list[PosterPanelRead])
def read_poster_panels(
    session_id: str,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> list[PosterPanelRead]:
    ensure_session_access(db, session_id, current_user)
    return [PosterPanelRead.model_validate(panel) for panel in list_panels(db, session_id)]


@app.post(f"{settings.api_prefix}/sessions/{{session_id}}/poster/panels", response_model=PosterPanelRead)
def create_poster_panel(
    session_id: str,
    payload: PosterPanelCreate,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> PosterPanelRead:
    ensure_session_access(db, session_id, current_user)
    panel = PosterPanel(id=str(uuid4()), session_id=session_id, **payload.model_dump())
    db.add(panel)
    db.flush()
    recompute_panel_text(db, panel)
    rebind_chunks_to_panel(db, panel)
    db.commit()
    db.refresh(panel)
    return PosterPanelRead.model_validate(panel)


def _validate_panel_bounds(x: float, y: float, width: float, height: float) -> None:
    if x + width > 1.0 or y + height > 1.0:
        raise HTTPException(status_code=422, detail="Panel bounds must stay within normalized poster coordinates")


@app.patch(f"{settings.api_prefix}/sessions/{{session_id}}/poster/panels/{{panel_id}}", response_model=PosterPanelRead)
def patch_poster_panel(
    session_id: str,
    panel_id: str,
    payload: PosterPanelUpdate,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> PosterPanelRead:
    ensure_session_access(db, session_id, current_user)
    panel = db.get(PosterPanel, panel_id)
    if panel is None or panel.session_id != session_id:
        raise HTTPException(status_code=404, detail="Panel not found")
    patch = payload.model_dump(exclude_unset=True)
    next_x = patch.get("x", panel.x)
    next_y = patch.get("y", panel.y)
    next_width = patch.get("width", panel.width)
    next_height = patch.get("height", panel.height)
    _validate_panel_bounds(next_x, next_y, next_width, next_height)
    for field, value in patch.items():
        setattr(panel, field, value)
    # Recompute captured text whenever geometry changes
    geometry_changed = any(field in patch for field in ("x", "y", "width", "height"))
    if geometry_changed:
        recompute_panel_text(db, panel)
        rebind_chunks_to_panel(db, panel)
    db.commit()
    db.refresh(panel)
    return PosterPanelRead.model_validate(panel)


@app.get(f"{settings.api_prefix}/sessions/{{session_id}}/poster/text-blocks", response_model=list[PosterTextBlockRead])
def read_poster_text_blocks(
    session_id: str,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> list[PosterTextBlockRead]:
    ensure_session_access(db, session_id, current_user)
    return [PosterTextBlockRead(**poster_text_block_to_read(b)) for b in list_poster_text_blocks(db, session_id)]


@app.delete(f"{settings.api_prefix}/sessions/{{session_id}}/poster/panels/{{panel_id}}", status_code=204)
def delete_poster_panel(
    session_id: str,
    panel_id: str,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> None:
    ensure_session_access(db, session_id, current_user)
    panel = db.get(PosterPanel, panel_id)
    if panel is None or panel.session_id != session_id:
        raise HTTPException(status_code=404, detail="Panel not found")
    # Detach any auto-bound knowledge chunks before deleting (FK has no cascade).
    from app.models import KnowledgeChunk as _KC
    db.query(_KC).filter(_KC.panel_id == panel_id).update({"panel_id": None})
    db.delete(panel)
    db.commit()
    return None


@app.get(f"{settings.api_prefix}/sessions/{{session_id}}/preview/cues", response_model=list[CueRead])
def read_preview_cues(
    session_id: str,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> list[CueRead]:
    ensure_session_access(db, session_id, current_user)
    items = (
        db.query(PresentationCue)
        .filter(PresentationCue.session_id == session_id)
        .order_by(PresentationCue.start_ms.asc())
        .all()
    )
    return [
        CueRead(
            id=item.id,
            cue_type=item.cue_type,
            start_ms=item.start_ms,
            duration_ms=item.duration_ms,
            payload=json.loads(item.payload_json or "{}"),
        )
        for item in items
    ]


@app.put(f"{settings.api_prefix}/sessions/{{session_id}}/preview/cues", response_model=list[CueRead])
def put_preview_cues(
    session_id: str,
    payload: CueListUpdate,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> list[CueRead]:
    ensure_session_access(db, session_id, current_user)
    items = replace_cues(db, session_id, [item.model_dump() for item in payload.cues])
    return [
        CueRead(
            id=item.id,
            cue_type=item.cue_type,
            start_ms=item.start_ms,
            duration_ms=item.duration_ms,
            payload=json.loads(item.payload_json or "{}"),
        )
        for item in items
    ]


@app.post(f"{settings.api_prefix}/sessions/{{session_id}}/knowledge/text", response_model=KnowledgeDocumentRead)
async def post_knowledge_text(
    session_id: str,
    payload: KnowledgeTextCreate,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> KnowledgeDocumentRead:
    ensure_session_access(db, session_id, current_user)
    document = create_knowledge_text(
        db=db,
        session_id=session_id,
        kind=payload.kind,
        title=payload.title,
        text=payload.text,
        language=payload.language,
        panel_id=payload.panel_id,
        trust_level=payload.trust_level,
        use_for_script=payload.use_for_script,
        use_for_live_qa=payload.use_for_live_qa,
        visibility=payload.visibility,
        source_url=payload.source_url,
        citation_label=payload.citation_label,
    )
    await embed_document_chunks(db, session_id, document.id)
    db.refresh(document)
    return KnowledgeDocumentRead.model_validate(document)


@app.post(f"{settings.api_prefix}/sessions/{{session_id}}/knowledge/upload", response_model=KnowledgeUploadRead)
async def post_knowledge_upload(
    session_id: str,
    kind: str = Form(...),
    language: str = Form("ja"),
    title: str | None = Form(None),
    panel_id: str | None = Form(None),
    auto_extract: bool = Form(True),
    trust_level: str = Form("primary"),
    use_for_script: bool = Form(True),
    use_for_live_qa: bool = Form(True),
    visibility: str = Form("runtime"),
    source_url: str | None = Form(None),
    citation_label: str | None = Form(None),
    file: UploadFile = File(...),
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> KnowledgeUploadRead:
    ensure_session_access(db, session_id, current_user)
    if kind not in {"paper", "poster", "author_notes", "limitation", "expected_qa", "reference"}:
        raise HTTPException(status_code=422, detail="Invalid knowledge kind")
    if language not in {"ja", "en"}:
        raise HTTPException(status_code=422, detail="Invalid language")
    result = await upload_knowledge_asset(
        db=db,
        session_id=session_id,
        upload=file,
        kind=kind,
        title=title,
        language=language,
        panel_id=panel_id or None,
        auto_extract=auto_extract,
        trust_level=trust_level,
        use_for_script=use_for_script,
        use_for_live_qa=use_for_live_qa,
        visibility=visibility,
        source_url=source_url,
        citation_label=citation_label,
    )
    await embed_document_chunks(db, session_id, result["document"].id)
    db.refresh(result["document"])
    return KnowledgeUploadRead(
        document=KnowledgeDocumentRead.model_validate(result["document"]),
        chunks_created=result["chunks_created"],
        extracted_chars=result["extracted_chars"],
        extraction_method=result["extraction_method"],
    )


@app.get(f"{settings.api_prefix}/sessions/{{session_id}}/knowledge/documents", response_model=list[KnowledgeDocumentRead])
def read_knowledge_documents(
    session_id: str,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> list[KnowledgeDocumentRead]:
    ensure_session_access(db, session_id, current_user)
    return [KnowledgeDocumentRead.model_validate(item) for item in list_knowledge_documents(db, session_id)]


@app.get(
    f"{settings.api_prefix}/sessions/{{session_id}}/knowledge/documents/{{document_id}}",
    response_model=KnowledgeDocumentDetailRead,
)
def read_knowledge_document_detail(
    session_id: str,
    document_id: str,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> KnowledgeDocumentDetailRead:
    ensure_session_access(db, session_id, current_user)
    document = get_knowledge_document(db, session_id, document_id)
    chunks = list_document_chunks(db, session_id, document_id)
    return KnowledgeDocumentDetailRead(
        id=document.id,
        session_id=document.session_id,
        asset_id=document.asset_id,
        kind=document.kind,
        title=document.title,
        language=document.language,
        status=document.status,
        trust_level=document.trust_level,
        use_for_script=document.use_for_script,
        use_for_live_qa=document.use_for_live_qa,
        visibility=document.visibility,
        source_url=document.source_url,
        citation_label=document.citation_label,
        text=document.text,
        chunk_count=len(chunks),
    )


@app.delete(
    f"{settings.api_prefix}/sessions/{{session_id}}/knowledge/documents/{{document_id}}",
    status_code=204,
)
def delete_knowledge_document_endpoint(
    session_id: str,
    document_id: str,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> None:
    ensure_session_access(db, session_id, current_user)
    delete_knowledge_document(db, session_id, document_id)
    return None


@app.get(f"{settings.api_prefix}/sessions/{{session_id}}/knowledge/chunks", response_model=list[KnowledgeChunkRead])
def read_knowledge_chunks(
    session_id: str,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> list[KnowledgeChunkRead]:
    ensure_session_access(db, session_id, current_user)
    return [KnowledgeChunkRead(**knowledge_chunk_to_read(item)) for item in list_knowledge_chunks(db, session_id)]


@app.post(f"{settings.api_prefix}/sessions/{{session_id}}/knowledge/retrieval-preview", response_model=RetrievalPreviewRead)
async def post_retrieval_preview(
    session_id: str,
    payload: RetrievalPreviewRequest,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> RetrievalPreviewRead:
    ensure_session_access(db, session_id, current_user)
    result = await retrieval_preview(
        db=db,
        session_id=session_id,
        question=payload.question,
        profile=payload.profile,
        language=payload.language,
        active_panel_id=payload.active_panel_id,
        top_k=payload.top_k,
    )
    return RetrievalPreviewRead(**result)


@app.post(f"{settings.api_prefix}/sessions/{{session_id}}/scripts/generate", response_model=list[ScriptSegmentRead])
async def post_generate_scripts(
    session_id: str,
    payload: ScriptGenerateRequest,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> list[ScriptSegmentRead]:
    session = ensure_session_access(db, session_id, current_user)
    segments = await generate_script_agent(
        db=db,
        session=session,
        profile=payload.profile,
        language=payload.language,
        replace_existing=payload.replace_existing,
        use_openai=payload.use_openai,
    )
    await generate_simulated_qas_agent(
        db=db,
        session=session,
        profile=payload.profile,
        language=payload.language,
        count=payload.simulated_question_count,
        replace_existing=payload.replace_existing,
        use_openai=payload.use_openai,
    )
    return [ScriptSegmentRead(**script_segment_to_read(segment)) for segment in segments]


@app.get(f"{settings.api_prefix}/sessions/{{session_id}}/scripts", response_model=list[ScriptSegmentRead])
def read_scripts(
    session_id: str,
    profile: str | None = None,
    language: str | None = None,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> list[ScriptSegmentRead]:
    ensure_session_access(db, session_id, current_user)
    return [
        ScriptSegmentRead(**script_segment_to_read(segment))
        for segment in list_script_segments(db, session_id, profile, language)
    ]


@app.patch(f"{settings.api_prefix}/sessions/{{session_id}}/scripts/{{segment_id}}", response_model=ScriptSegmentRead)
def patch_script_segment(
    session_id: str,
    segment_id: str,
    payload: ScriptSegmentUpdate,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> ScriptSegmentRead:
    ensure_session_access(db, session_id, current_user)
    segment = update_script_segment(db, session_id, segment_id, payload.text, payload.status)
    return ScriptSegmentRead(**script_segment_to_read(segment))


@app.get(f"{settings.api_prefix}/sessions/{{session_id}}/simulated-qas", response_model=list[SimulatedQaRead])
def read_simulated_qas(
    session_id: str,
    profile: str | None = None,
    language: str | None = None,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> list[SimulatedQaRead]:
    ensure_session_access(db, session_id, current_user)
    return [
        SimulatedQaRead(**simulated_qa_to_read(item))
        for item in list_simulated_qas(db, session_id, profile, language)
    ]


@app.patch(f"{settings.api_prefix}/sessions/{{session_id}}/simulated-qas/{{qa_id}}", response_model=SimulatedQaRead)
async def patch_simulated_qa(
    session_id: str,
    qa_id: str,
    payload: SimulatedQaUpdate,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> SimulatedQaRead:
    ensure_session_access(db, session_id, current_user)
    item = update_simulated_qa(db, session_id, qa_id, payload.status, payload.question, payload.answer)
    if item.embedding_status in {"pending", "stale"} and item.status == "approved":
        await embed_simulated_qa_questions(db, session_id)
        db.refresh(item)
    return SimulatedQaRead(**simulated_qa_to_read(item))


def _tts_asset_read(db: Session, request: Request, tts_asset: TtsAsset) -> TtsAssetRead:
    asset = db.get(Asset, tts_asset.asset_id)
    if asset is None:
        raise HTTPException(status_code=404, detail="TTS audio asset not found")
    url, expires_at = get_storage().signed_download_url(asset.id, asset.storage_path)
    return TtsAssetRead(
        id=tts_asset.id,
        segment_id=tts_asset.segment_id,
        asset_id=tts_asset.asset_id,
        provider=tts_asset.provider,
        voice_id=tts_asset.voice_id,
        model_id=tts_asset.model_id,
        language=tts_asset.language,
        audio_url=url,
        expires_at=expires_at,
    )


@app.post(f"{settings.api_prefix}/sessions/{{session_id}}/scripts/{{segment_id}}/tts", response_model=TtsAssetRead)
async def post_script_tts(
    session_id: str,
    segment_id: str,
    payload: TtsGenerateRequest,
    request: Request,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> TtsAssetRead:
    ensure_session_access(db, session_id, current_user)
    tts_asset = await synthesize_segment_tts(
        db,
        session_id,
        segment_id,
        payload.voice_id,
        payload.model_id,
        payload.consent_confirmed,
        payload.consent_label,
        current_user,
        payload.voice_settings.model_dump(exclude_none=True) if payload.voice_settings else None,
    )
    return _tts_asset_read(db, request, tts_asset)


@app.post(f"{settings.api_prefix}/sessions/{{session_id}}/scripts/generate-all-tts", response_model=TtsBatchResultRead)
async def post_batch_tts(
    session_id: str,
    payload: TtsBatchGenerateRequest,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> TtsBatchResultRead:
    ensure_session_access(db, session_id, current_user)
    result = await synthesize_all_session_tts(
        db,
        session_id,
        payload.profile,
        payload.language,
        payload.voice_id,
        payload.model_id,
        payload.consent_confirmed,
        payload.consent_label,
        current_user,
        payload.voice_settings.model_dump(exclude_none=True) if payload.voice_settings else None,
    )
    return TtsBatchResultRead(**result)


@app.websocket(f"{settings.api_prefix}/runtime/{{session_code}}/questions")
async def runtime_qa_websocket(
    websocket: WebSocket,
    session_code: str,
    token: str = Query(...),
    profile: str = Query("master"),
    language: str = Query("ja"),
) -> None:
    try:
        verify_runtime_token(session_code, token)
    except HTTPException as exc:
        await websocket.close(code=4401)
        return
    db = next(get_db())
    try:
        session = db.scalars(select(SessionModel).where(SessionModel.session_code == session_code)).first()
        if session is None:
            await websocket.close(code=4404)
            return
        if session.status != "published":
            await websocket.close(code=4403)
            return
        if profile not in {"beginner", "master", "professional"}:
            profile = "master"
        if language not in {"ja", "en"}:
            language = "ja"
        await websocket.accept()
        await websocket.send_json({
            "type": "ready",
            "sessionCode": session.session_code,
            "profile": profile,
            "language": language,
        })
        try:
            while True:
                message = await websocket.receive_json()
                if not isinstance(message, dict):
                    await websocket.send_json({"type": "error", "message": "Message must be a JSON object."})
                    continue
                kind = message.get("type")
                if kind == "ping":
                    await websocket.send_json({"type": "pong"})
                    continue
                if kind != "question":
                    await websocket.send_json({"type": "error", "message": f"Unsupported message type: {kind}"})
                    continue
                question = str(message.get("question") or "").strip()
                if not question:
                    await websocket.send_json({"type": "error", "message": "Question text is required."})
                    continue
                active_panel_id = message.get("activePanelId") or message.get("active_panel_id")
                top_k = int(message.get("topK") or message.get("top_k") or 6)
                top_k = max(1, min(top_k, 12))
                requested_profile = message.get("profile") or profile
                requested_language = message.get("language") or language
                if requested_profile not in {"beginner", "master", "professional"}:
                    requested_profile = profile
                if requested_language not in {"ja", "en"}:
                    requested_language = language
                try:
                    result = await retrieval_preview(
                        db=db,
                        session_id=session.id,
                        question=question,
                        profile=requested_profile,
                        language=requested_language,
                        active_panel_id=active_panel_id,
                        top_k=top_k,
                    )
                except HTTPException as exc:
                    await websocket.send_json({"type": "error", "status": exc.status_code, "message": str(exc.detail)})
                    continue
                await websocket.send_json({
                    "type": "answer",
                    "answerability": result["answerability"],
                    "reason": result["reason"],
                    "draftAnswer": result["draft_answer"],
                    "qaMatch": result["qa_match"],
                    "chunks": result["chunks"],
                })
        except WebSocketDisconnect:
            return
    finally:
        db.close()


@app.get(f"{settings.api_prefix}/sessions/{{session_id}}/voice-consents", response_model=list[VoiceConsentRead])
def read_voice_consents(
    session_id: str,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),
) -> list[VoiceConsentRead]:
    ensure_session_access(db, session_id, current_user)
    return [VoiceConsentRead.model_validate(item) for item in list_voice_consents(db, session_id)]


@app.post(f"{settings.api_prefix}/voice-samples", response_model=VoiceSampleRead)
async def post_voice_sample(
    payload: VoiceSampleCreate,
    db: Session = Depends(get_db),
    current_user: CurrentUser = Depends(get_current_user),  # noqa: ARG001 — auth guard only
) -> VoiceSampleRead:
    """Auditioning endpoint for ElevenLabs voice IDs. The (voice_id, text,
    language, model) tuple is hashed; identical requests reuse the cached
    audio so we don't burn ElevenLabs credits on repeat clicks."""
    sample, cached = await get_or_create_voice_sample(
        db,
        voice_id=payload.voice_id,
        text=payload.text,
        language=payload.language,
    )
    url, expires_at = signed_voice_sample_url(sample)
    return VoiceSampleRead(
        id=sample.id,
        voice_id=sample.voice_id,
        text=sample.text,
        language=sample.language,
        model_id=sample.model_id,
        audio_url=url,
        expires_at=expires_at,
        cached=cached,
        created_at=sample.created_at,
    )
