"""Server-side PLY + base VRM → .gvrm packaging.

This is the worker that satisfies an `AvatarBuildJob`. The .gvrm format
(per naruya/gaussian-vrm) is a zip of:
    - model.vrm   : base humanoid VRM (GLB)
    - model.ply   : Gaussian splat point cloud
    - data.json   : metadata + per-splat skinning weights

For the first iteration we package the bytes the user uploaded and emit a
`stub-v1` data.json. The runtime client (Unity) detects this flag and
computes splatBoneIndices / splatRelativePoses from the bone-bind poses
itself on first load. A future revision can replace the stub with a real
nearest-bone assignment computed via numpy here.
"""

from __future__ import annotations

import gc
import io
import json
import logging
import zipfile
from pathlib import Path
from uuid import uuid4

from sqlalchemy.orm import Session

from app.core.config import get_settings
from app.avatar_preprocess import BUILD_VERSION, preprocess_gvrm
from app.models import Asset, AvatarBuildJob, AvatarConfig
from app.spz_encoder import (
    decode_spz_to_ply,
    encode_ply_to_spz,
    is_available as spz_is_available,
)
from app.storage import storage_for_asset, storage_for_kind

logger = logging.getLogger(__name__)

STUB_DATA_JSON: dict = {
    # Required gaussian-vrm fields (kept compatible with the schema)
    "modelScale": 1.0,
    "boneOperations": [],
    "gsPosition": [0.0, 0.0, 0.0],
    "gsQuaternion": [0.0, 0.0, 0.0, 1.0],
    "splatVertexIndices": [],
    "splatBoneIndices": [],
    "splatRelativePoses": [],
    # Build metadata — Unity loader sees `_buildVersion = "stub-v1"` and
    # falls back to runtime nearest-bone assignment using model.vrm + model.ply
    "_buildVersion": "stub-v1",
    "_note": (
        "Server packaged source PLY + base VRM. Skinning weights are not "
        "precomputed; the runtime client should derive them from bone bind "
        "poses on load. Replace with full server-side math in a follow-up."
    ),
}


def build_gvrm_zip(
    vrm_bytes: bytes,
    splat_bytes: bytes,
    data_json: dict,
    splat_format: str = "ply",
) -> bytes:
    """Build a .gvrm zip. `splat_format` decides whether the splat payload is
    stored as `model.ply` or `model.spz`. The Unity loader keys on the entry
    name (and falls back to magic-byte sniff)."""
    if splat_format not in {"ply", "spz"}:
        raise ValueError(f"Unsupported splat_format: {splat_format}")
    splat_entry_name = "model.spz" if splat_format == "spz" else "model.ply"
    # Mirror format into data.json so loaders that read it first know what to expect.
    data_json = {**data_json, "_splatFormat": splat_format, "_splatEntry": splat_entry_name}
    buf = io.BytesIO()
    # ZIP_STORED (no compression). PLY splat data is mostly random floats
    # (positions / quaternions / SH coefficients) — zlib gains 5-15% at the
    # cost of 30-60s of single-CPU compression on Fly shared. The slowdown
    # blocks the event loop, fails health checks, and risks Fly killing
    # the machine mid-build. The .gvrm grows ~10MB but the local Fly volume
    # has 3GB so size isn't the constraint.
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_STORED) as zf:
        zf.writestr("model.vrm", vrm_bytes)
        zf.writestr(splat_entry_name, splat_bytes)
        zf.writestr("data.json", json.dumps(data_json, ensure_ascii=False, indent=2))
    return buf.getvalue()


def _set_progress(db: Session, job: AvatarBuildJob, progress: int, log_line: str | None = None) -> None:
    job.progress = max(0, min(100, progress))
    if log_line:
        existing = job.log or ""
        job.log = (existing + "\n" if existing else "") + log_line
    db.commit()


def process_avatar_build(db: Session, session_id: str, job_id: str) -> AvatarBuildJob:
    job = db.get(AvatarBuildJob, job_id)
    if job is None or job.session_id != session_id:
        raise ValueError("Build job not found")

    if job.status not in {"queued", "running"}:
        # Idempotency: never re-process a finished job
        return job

    job.status = "running"
    _set_progress(db, job, 5, "fetching sources")

    try:
        config = db.get(AvatarConfig, session_id)
        ply_asset_id = job.source_ply_asset_id
        vrm_asset_id = job.source_vrm_asset_id
        fallback_vrm_bytes = None
        fallback_data_json: dict | None = None
        if ply_asset_id:
            ply_asset = db.get(Asset, ply_asset_id)
            if not ply_asset:
                raise ValueError("Source scan asset is missing from storage records")

            _set_progress(db, job, 20, f"reading scan: {ply_asset.file_name}")
            ply_bytes = storage_for_asset(ply_asset).read_bytes(ply_asset.storage_path)
        elif config and config.gvrm_asset_id:
            gvrm_asset = db.get(Asset, config.gvrm_asset_id)
            if not gvrm_asset:
                raise ValueError("Existing .gvrm asset is missing from storage records")
            _set_progress(db, job, 20, f"reading existing .gvrm: {gvrm_asset.file_name}")
            gvrm_bytes = storage_for_asset(gvrm_asset).read_bytes(gvrm_asset.storage_path)
            with zipfile.ZipFile(io.BytesIO(gvrm_bytes), "r") as zf:
                try:
                    ply_bytes = zf.read("model.ply")
                except KeyError:
                    try:
                        spz_bytes = zf.read("model.spz")
                    except KeyError as exc:
                        raise ValueError("Existing .gvrm is missing model.ply or model.spz") from exc
                    if not spz_is_available():
                        raise ValueError("Existing .gvrm contains model.spz, but spz decode is unavailable")
                    ply_bytes = decode_spz_to_ply(spz_bytes)
                try:
                    fallback_vrm_bytes = zf.read("model.vrm")
                except KeyError as exc:
                    raise ValueError("Existing .gvrm is missing model.vrm") from exc
                try:
                    fallback_data_json = json.loads(zf.read("data.json").decode("utf-8"))
                except KeyError as exc:
                    raise ValueError("Existing .gvrm is missing data.json") from exc
        else:
            raise ValueError("PLY/SPZ scan must be uploaded before build")

        # VRM source: user-supplied if uploaded, otherwise the bundled CC0
        # neutral humanoid (fem_vroid.vrm). The bundled file lives at
        # <repo>/apps/api/avatar-base/fem_vroid.vrm and is shipped via
        # AVATAR_BASE_DIR / AVATAR_DEFAULT_VRM_FILENAME settings.
        vrm_asset = db.get(Asset, vrm_asset_id) if vrm_asset_id else None
        if vrm_asset:
            _set_progress(db, job, 45, f"reading custom base VRM: {vrm_asset.file_name}")
            vrm_bytes = storage_for_asset(vrm_asset).read_bytes(vrm_asset.storage_path)
        elif fallback_vrm_bytes is not None:
            _set_progress(db, job, 45, "using VRM from existing .gvrm")
            vrm_bytes = fallback_vrm_bytes
        else:
            settings = get_settings()
            avatar_config = config
            preset = avatar_config.base_vrm_preset if avatar_config else None
            preset_filename = (
                f"{preset}.vrm" if preset and not preset.endswith(".vrm")
                else preset
            ) if preset else settings.avatar_default_vrm_filename
            default_vrm_path = settings.avatar_base_dir / preset_filename
            if not default_vrm_path.is_absolute():
                default_vrm_path = Path.cwd() / default_vrm_path
            if not default_vrm_path.is_file():
                raise ValueError(
                    f"Base VRM not found at {default_vrm_path}. "
                    f"Ship a CC0 humanoid VRM (e.g. madjin/vrm-samples fem_vroid.vrm) "
                    f"or upload a custom VRM in the dashboard's Advanced section."
                )
            _set_progress(db, job, 45, f"using bundled VRM: {default_vrm_path.name}")
            vrm_bytes = default_vrm_path.read_bytes()

        # Quick header sanity to fail fast on garbage uploads
        if len(vrm_bytes) < 4 or vrm_bytes[:4] != b"glTF":
            raise ValueError("Base VRM file is not a valid GLB (missing glTF magic)")
        is_ply = len(ply_bytes) >= 3 and ply_bytes[:3] == b"ply"
        is_spz = len(ply_bytes) >= 4 and ply_bytes[:4] == b"NGSP"
        if not (is_ply or is_spz):
            raise ValueError("Scan file is not a valid PLY or SPZ")

        # SPZ encoding intentionally NOT used here. Niantic's binding outputs
        # SPZ format version 4, but the in-browser preview viewer
        # (mkkellogg/gaussian-splats-3d 0.4.7) only decodes versions 1-2 →
        # "version not supported" error. Raw PLY round-trips through both
        # the dashboard preview (PLY path with per-bone scene grouping +
        # GPU LBS shader) and Unity (aras-p PlyLoader) cleanly. avatar-gvrm
        # is back on local Fly storage so size isn't capped.
        splat_bytes = ply_bytes
        splat_format = "spz" if is_spz else "ply"

        # Bake per-session height multiplier into data.json `modelScale` so
        # both Unity and the dashboard preview honor the user's chosen size
        # without renderer-specific code.
        height_scale = float(config.avatar_height_scale) if config is not None else 1.0

        if fallback_data_json is not None and not ply_asset_id:
            _set_progress(db, job, 60, "preserving existing splat payload + bindings")
            data_json = dict(fallback_data_json)
            old_built_height = float(
                data_json.get("_builtHeightScale")
                or data_json.get("modelScale")
                or 1.0
            )
            if old_built_height > 1e-6:
                data_json["modelScale"] = float(data_json.get("modelScale") or 1.0) * (
                    height_scale / old_built_height
                )
            data_json["_builtHeightScale"] = height_scale
            data_json["_buildVersion"] = BUILD_VERSION
            data_json["_rebuildSource"] = "existing-gvrm-preserved"
            splat_bytes = ply_bytes
            splat_format = "ply"
            ply_bytes = None  # type: ignore[assignment]
            gc.collect()
        else:
            _set_progress(db, job, 60, "computing splat ↔ bone bindings + alignment")
            # Server-side preprocess: capsule-per-bone + nearest-skinned-vertex,
            # ported from naruya/gaussian-vrm preprocess.js (MIT). Returns BOTH
            # the data.json AND an aligned PLY (splats translated into VRM
            # frame). The aligned PLY MUST be what we bundle in .gvrm so the
            # browser/Unity renderer reads splats in the same coords the
            # bindings were computed against — otherwise the body floats
            # disconnected from the rig.
            if is_spz and spz_is_available():
                preprocess_input = decode_spz_to_ply(ply_bytes)
            elif is_spz:
                raise ValueError(
                    "SPZ-format scan uploaded but the spz binding is unavailable on this "
                    "host. Re-upload as PLY (Scaniverse → Export → PLY)."
                )
            else:
                preprocess_input = ply_bytes
            # Free the raw upload's reference; preprocess_gvrm only needs
            # `preprocess_input` from here on.
            ply_bytes = None  # type: ignore[assignment]
            gc.collect()

            data_json, aligned_ply_bytes = preprocess_gvrm(
                preprocess_input, vrm_bytes, height_scale=height_scale,
            )
            preprocess_input = None  # type: ignore[assignment]
            gc.collect()

            # Bundle the ALIGNED PLY (not the raw upload). splat_bytes was
            # initialized to the raw upload up top — replace with the aligned
            # version so the .gvrm carries splats already in VRM frame.
            splat_bytes = aligned_ply_bytes
            splat_format = "ply"

        _set_progress(db, job, 75, "packaging .gvrm zip (splat_format=ply, aligned)")
        zip_bytes = build_gvrm_zip(vrm_bytes, splat_bytes, data_json, splat_format=splat_format)

        # Output size logged for telemetry. avatar-gvrm now stores on the
        # local Fly volume (LOCAL_AVATAR_KINDS), so the Supabase 50MB cap
        # no longer constrains us. Fly's 3GB volume holds ~30 builds at
        # 100MB each — plenty for a research demo deployment.
        gvrm_mb = len(zip_bytes) / (1024 * 1024)
        logger.info(".gvrm size: %.1fMB (splat_format=%s)", gvrm_mb, splat_format)

        _set_progress(db, job, 80, f"uploading .gvrm ({len(zip_bytes) // 1024}KB)")
        new_asset_id = str(uuid4())
        gvrm_storage = storage_for_kind("avatar-gvrm")
        storage_path, size_bytes = gvrm_storage.save_bytes(
            session_id=session_id,
            folder="avatar",
            stored_name=f"{new_asset_id}.gvrm",
            content=zip_bytes,
            content_type="application/zip",
        )
        new_asset = Asset(
            id=new_asset_id,
            session_id=session_id,
            kind="avatar-gvrm",
            storage_path=storage_path,
            file_name="server-built.gvrm",
            mime_type="application/zip",
            size_bytes=size_bytes,
            status="ready",
        )
        db.add(new_asset)
        db.flush()

        _set_progress(db, job, 90, "swapping avatar config")
        config = db.get(AvatarConfig, session_id)
        if config is not None:
            previous_gvrm = config.gvrm_asset_id
            config.gvrm_asset_id = new_asset_id
            config.runtime_type = "gvrm"
            config.metadata_json = json.dumps(data_json, ensure_ascii=False)
            db.flush()

            # Source PLY/VRM uploads are consumed into the .gvrm to avoid
            # storing duplicate scan data. The previous .gvrm is also removed
            # after the new one is ready. Any FK referencing deleted assets
            # must be nulled before the Asset DELETE, otherwise Postgres raises
            # ForeignKeyViolation.
            from sqlalchemy import select as _sel
            asset_ids_to_drop: list[str] = []
            for field_name in ("source_ply_asset_id", "source_vrm_asset_id"):
                old_id = getattr(config, field_name)
                if old_id:
                    asset_ids_to_drop.append(old_id)
                    setattr(config, field_name, None)
            if previous_gvrm and previous_gvrm != new_asset_id:
                asset_ids_to_drop.append(previous_gvrm)
            if asset_ids_to_drop:
                jobs = list(
                    db.scalars(
                        _sel(AvatarBuildJob).where(AvatarBuildJob.session_id == session_id)
                    )
                )
                for j in jobs:
                    if j.source_ply_asset_id in asset_ids_to_drop:
                        j.source_ply_asset_id = None
                    if j.source_vrm_asset_id in asset_ids_to_drop:
                        j.source_vrm_asset_id = None
                    if j.output_asset_id in asset_ids_to_drop and j.id != job.id:
                        j.output_asset_id = None
                db.flush()
                for old_id in asset_ids_to_drop:
                    old = db.get(Asset, old_id)
                    if old is not None:
                        try:
                            storage_for_asset(old).delete_path(old.storage_path)
                        except Exception as exc:
                            logger.warning("source cleanup failed: %s", exc)
                        db.delete(old)

        job.output_asset_id = new_asset_id
        job.build_version = BUILD_VERSION
        job.status = "succeeded"
        _set_progress(db, job, 100, "done — sources consumed into .gvrm")
        return job
    except Exception as exc:
        logger.exception("avatar build failed: %s", exc)
        job.status = "failed"
        job.error = str(exc)[:500]
        db.commit()
        return job


def run_build_in_background(session_id: str, job_id: str) -> None:
    """Thin wrapper used as a FastAPI BackgroundTask. Opens its own DB session
    because the request-scoped one is already closed by the time we run."""
    from app.db import SessionLocal
    db = SessionLocal()
    try:
        process_avatar_build(db, session_id, job_id)
    finally:
        db.close()
