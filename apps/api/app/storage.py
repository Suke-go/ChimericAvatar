from __future__ import annotations

import hashlib
import hmac
import time
from functools import lru_cache
from pathlib import Path
from typing import Protocol
from urllib.parse import quote

import httpx
from fastapi import HTTPException, UploadFile, status

from app.core.config import get_settings


class ObjectStorage(Protocol):
    """Storage backend used by upload/download flows.

    Implementations save raw bytes/files and produce short-lived URLs that the
    runtime client can use without our authentication.
    """

    backend_id: str

    def save_upload(
        self,
        session_id: str,
        folder: str,
        upload: UploadFile,
        stored_name: str,
        max_bytes: int,
    ) -> tuple[str, int]: ...

    def save_bytes(
        self,
        session_id: str,
        folder: str,
        stored_name: str,
        content: bytes,
        content_type: str = "application/octet-stream",
    ) -> tuple[str, int]: ...

    def delete_path(self, storage_path: str) -> None: ...

    def read_bytes(self, storage_path: str) -> bytes: ...

    def signed_download_url(
        self,
        asset_id: str,
        storage_path: str,
        ttl_seconds: int | None = None,
    ) -> tuple[str, int]: ...


class LocalStorage:
    """Filesystem-backed storage. Used in local development and demo deployments
    that mount a persistent volume. Signed URLs route through FastAPI's HMAC
    download endpoint."""

    backend_id = "local"

    def __init__(self) -> None:
        self.root = get_settings().upload_root
        self.root.mkdir(parents=True, exist_ok=True)

    def save_upload(self, session_id, folder, upload, stored_name, max_bytes):
        target_dir = self.root / "sessions" / session_id / folder
        target_dir.mkdir(parents=True, exist_ok=True)
        target = (target_dir / stored_name).resolve()
        root = self.root.resolve()
        if root not in target.parents:
            raise HTTPException(status_code=400, detail="Invalid upload path")
        total = 0
        with target.open("wb") as handle:
            while True:
                chunk = upload.file.read(1024 * 1024)
                if not chunk:
                    break
                total += len(chunk)
                if total > max_bytes:
                    target.unlink(missing_ok=True)
                    raise HTTPException(status_code=413, detail="File too large")
                handle.write(chunk)
        return str(target.relative_to(root)).replace("\\", "/"), total

    def save_bytes(self, session_id, folder, stored_name, content, content_type="application/octet-stream"):
        # Local backend ignores content_type (filesystem doesn't store it).
        target_dir = self.root / "sessions" / session_id / folder
        target_dir.mkdir(parents=True, exist_ok=True)
        target = (target_dir / stored_name).resolve()
        root = self.root.resolve()
        if root not in target.parents:
            raise HTTPException(status_code=400, detail="Invalid storage path")
        target.write_bytes(content)
        return str(target.relative_to(root)).replace("\\", "/"), len(content)

    def delete_path(self, storage_path: str) -> None:
        target = self.resolve_public_path(storage_path)
        if target.is_file():
            target.unlink(missing_ok=True)

    def read_bytes(self, storage_path: str) -> bytes:
        target = self.resolve_public_path(storage_path)
        return target.read_bytes()

    def resolve_public_path(self, storage_path: str) -> Path:
        target = (self.root / Path(storage_path)).resolve()
        root = self.root.resolve()
        if root not in target.parents and target != root:
            raise HTTPException(status_code=400, detail="Invalid storage path")
        return target

    def signed_download_url(self, asset_id, storage_path, ttl_seconds=None):
        settings = get_settings()
        expires_at = int(time.time()) + (ttl_seconds or settings.asset_url_ttl_seconds)
        message = f"{asset_id}.{expires_at}".encode("utf-8")
        signature = hmac.new(
            settings.asset_url_secret.encode("utf-8"), message, hashlib.sha256
        ).hexdigest()
        token = f"{expires_at}.{signature}"
        url = f"{settings.api_public_base_url.rstrip('/')}/api/v1/assets/{asset_id}/download?token={token}"
        return url, expires_at


def verify_local_download_token(asset_id: str, token: str) -> None:
    """Used by the FastAPI /assets/{id}/download route when LocalStorage is active."""
    settings = get_settings()
    try:
        expires_text, signature = token.split(".", 1)
        expires_at = int(expires_text)
    except ValueError as exc:
        raise HTTPException(status_code=401, detail="Invalid asset token") from exc
    if expires_at < int(time.time()):
        raise HTTPException(status_code=401, detail="Asset token expired")
    message = f"{asset_id}.{expires_at}".encode("utf-8")
    expected = hmac.new(
        settings.asset_url_secret.encode("utf-8"), message, hashlib.sha256
    ).hexdigest()
    if not hmac.compare_digest(signature, expected):
        raise HTTPException(status_code=401, detail="Invalid asset token")


class SupabaseStorage:
    """Supabase Storage adapter using the service-role key. Buckets must exist
    and stay private; we mint signed URLs at read time so clients never hit the
    bucket directly with the service key."""

    backend_id = "supabase"

    def __init__(self) -> None:
        settings = get_settings()
        # Prefer the new sb_secret_... key. Fall back to legacy service_role
        # JWT key for projects created before Supabase's Nov 2025 key migration.
        secret = settings.supabase_secret_key or settings.supabase_service_role_key
        if not settings.supabase_url or not secret:
            raise RuntimeError(
                "STORAGE_BACKEND=supabase requires SUPABASE_URL and either "
                "SUPABASE_SECRET_KEY (sb_secret_...) or SUPABASE_SERVICE_ROLE_KEY"
            )
        self._validate_key_shape(secret, settings.supabase_secret_key is not None)
        self._base = settings.supabase_url.rstrip("/")
        self._key = secret
        self._bucket = settings.supabase_storage_bucket

    @staticmethod
    def _validate_key_shape(value: str, is_new_format: bool) -> None:
        # Catch the common copy/paste mistake of pasting the dashboard label
        # ("Secret keys") instead of the actual sb_secret_... value.
        stripped = value.strip()
        if not stripped or " " in stripped or len(stripped) < 30:
            raise RuntimeError(
                "Supabase secret looks invalid (too short or contains whitespace). "
                "Open Supabase Dashboard → Project Settings → API Keys → Secret keys, "
                "Reveal the key, and paste the full sb_secret_... value."
            )
        if is_new_format and not stripped.startswith("sb_secret_"):
            raise RuntimeError(
                "SUPABASE_SECRET_KEY does not start with 'sb_secret_'. "
                "Either set the new sb_secret_... key here, or use SUPABASE_SERVICE_ROLE_KEY for legacy projects."
            )

    def _headers(self, content_type: str | None = None) -> dict[str, str]:
        h = {
            "authorization": f"Bearer {self._key}",
            "apikey": self._key,
        }
        if content_type:
            h["content-type"] = content_type
        return h

    def _object_url(self, storage_path: str) -> str:
        encoded = quote(storage_path, safe="/")
        return f"{self._base}/storage/v1/object/{self._bucket}/{encoded}"

    def _put(self, storage_path: str, content: bytes, content_type: str) -> int:
        import logging
        import time as _time
        logger = logging.getLogger(__name__)
        size_kb = len(content) / 1024
        logger.info(
            "supabase upload start: path=%s size=%.1fKB ctype=%s",
            storage_path, size_kb, content_type,
        )
        url = self._object_url(storage_path)
        # Free-tier Supabase Storage can be slow on larger uploads; allow up to
        # 5 minutes total for the request, but still fail fast on connect.
        timeout = httpx.Timeout(300.0, connect=15.0)
        max_attempts = 3
        last_err: Exception | None = None
        for attempt in range(1, max_attempts + 1):
            try:
                upload_headers = {
                    **self._headers(content_type),
                    "x-upsert": "true",
                    "content-length": str(len(content)),
                }
                with httpx.Client(timeout=timeout) as client:
                    response = client.post(
                        url,
                        content=content,
                        headers=upload_headers,
                    )
                if response.status_code >= 400:
                    body = response.text[:200]
                    # Translate common Supabase Free-tier limits into actionable hints.
                    if response.status_code == 413 or "Payload too large" in body or "exceeded" in body.lower():
                        raise HTTPException(
                            status_code=413,
                            detail=(
                                f"Upload rejected by Supabase ({size_kb:.0f}KB > bucket file size limit). "
                                f"Free tier caps single uploads at 50MB. "
                                f"Compress the file (.ply → .spz is ~10× smaller) or raise the bucket's "
                                f"`File size limit` in Supabase Dashboard → Storage → bucket → Settings."
                            ),
                        )
                    if response.status_code == 401 or "Invalid Compact JWS" in body or "JWT" in body:
                        raise HTTPException(
                            status_code=502,
                            detail=(
                                "Supabase rejected the request as unauthorized. Check that "
                                "SUPABASE_SECRET_KEY (sb_secret_...) is set correctly on the server."
                            ),
                        )
                    raise HTTPException(
                        status_code=502,
                        detail=f"Supabase upload failed: {response.status_code} {body}",
                    )
                logger.info("supabase upload OK: path=%s attempt=%d", storage_path, attempt)
                return len(content)
            except (httpx.RemoteProtocolError, httpx.ReadError, httpx.WriteError, httpx.ConnectError, httpx.ReadTimeout, httpx.WriteTimeout) as exc:
                last_err = exc
                logger.warning(
                    "supabase upload transient error attempt=%d/%d: %s",
                    attempt, max_attempts, exc,
                )
                if attempt < max_attempts:
                    _time.sleep(2 ** (attempt - 1))  # 1s, 2s
                    continue
            except httpx.HTTPError as exc:
                last_err = exc
                break
        raise HTTPException(
            status_code=502,
            detail=f"Supabase upload failed after {max_attempts} attempts: {last_err}",
        )

    def save_upload(self, session_id, folder, upload, stored_name, max_bytes):
        chunks = bytearray()
        total = 0
        while True:
            chunk = upload.file.read(1024 * 1024)
            if not chunk:
                break
            total += len(chunk)
            if total > max_bytes:
                raise HTTPException(status_code=413, detail="File too large")
            chunks.extend(chunk)
        storage_path = f"sessions/{session_id}/{folder}/{stored_name}"
        content_type = upload.content_type or "application/octet-stream"
        size = self._put(storage_path, bytes(chunks), content_type)
        return storage_path, size

    def save_bytes(self, session_id, folder, stored_name, content, content_type="application/octet-stream"):
        storage_path = f"sessions/{session_id}/{folder}/{stored_name}"
        size = self._put(storage_path, content, content_type)
        return storage_path, size

    def delete_path(self, storage_path: str) -> None:
        url = self._object_url(storage_path)
        try:
            with httpx.Client(timeout=30.0) as client:
                response = client.delete(url, headers=self._headers())
        except httpx.HTTPError:
            return
        if response.status_code >= 400 and response.status_code != 404:
            # Don't fail callers for cleanup errors; log and move on.
            import logging

            logging.getLogger(__name__).warning(
                "supabase delete failed: %s %s", response.status_code, response.text[:200]
            )

    def read_bytes(self, storage_path: str) -> bytes:
        # Pull the raw object back via the service-role key. Used by build
        # workers that need to repackage user-uploaded source files.
        url = self._object_url(storage_path)
        timeout = httpx.Timeout(120.0, connect=15.0)
        try:
            with httpx.Client(timeout=timeout) as client:
                response = client.get(url, headers=self._headers())
        except httpx.HTTPError as exc:
            raise HTTPException(status_code=502, detail=f"Supabase read failed: {exc}") from exc
        if response.status_code >= 400:
            raise HTTPException(
                status_code=502,
                detail=f"Supabase read failed: {response.status_code} {response.text[:200]}",
            )
        return response.content

    def signed_download_url(self, asset_id, storage_path, ttl_seconds=None):
        settings = get_settings()
        ttl = ttl_seconds or settings.asset_url_ttl_seconds
        sign_url = f"{self._base}/storage/v1/object/sign/{self._bucket}/{quote(storage_path, safe='/')}"
        try:
            with httpx.Client(timeout=20.0) as client:
                response = client.post(
                    sign_url,
                    json={"expiresIn": ttl},
                    headers=self._headers("application/json"),
                )
        except httpx.HTTPError as exc:
            raise HTTPException(status_code=502, detail=f"Supabase sign failed: {exc}") from exc
        if response.status_code >= 400:
            raise HTTPException(
                status_code=502,
                detail=f"Supabase sign failed: {response.status_code} {response.text[:200]}",
            )
        body = response.json()
        token = body.get("signedURL") or body.get("signedUrl")
        if not token:
            raise HTTPException(status_code=502, detail="Supabase did not return a signed URL")
        # Supabase returns a path like /object/sign/<bucket>/<path>?token=...
        if token.startswith("/"):
            url = f"{self._base}/storage/v1{token}"
        else:
            url = f"{self._base}/storage/v1/{token.lstrip('/')}"
        expires_at = int(time.time()) + ttl
        return url, expires_at


@lru_cache(maxsize=1)
def get_storage() -> ObjectStorage:
    backend = get_settings().storage_backend
    if backend == "supabase":
        return SupabaseStorage()
    return LocalStorage()


def reset_storage_cache() -> None:
    """For tests that swap settings."""
    get_storage.cache_clear()


# Avatar SOURCE assets (Scaniverse PLY/SPZ + custom user VRM uploads) live on
# the API server's local filesystem regardless of the configured backend.
# Why: Supabase Free has a 50MB-per-file cap that 70-80MB PLYs blow through.
# These are transient — `avatar_build.py` deletes them after the .gvrm is
# produced — so single-AZ Fly volume durability is acceptable.
#
# Avatar OUTPUT (avatar-gvrm) intentionally NOT in this set: it follows the
# configured STORAGE_BACKEND so production can lean on Supabase's replicated
# durability for the long-lived artifact. SPZ-bundled .gvrm files are ~15MB
# (well under the 50MB cap); PLY-bundled .gvrm builds get rejected at build
# time with a "re-export as .spz" hint (see avatar_build.py).
#
# Trade-off: the API server must still be single-instance for source assets
# to stay reachable across requests + builds. Multi-instance API would need
# an S3/R2 adapter for the source kinds.
LOCAL_AVATAR_KINDS = {
    "avatar-scaniverse-ply",
    "avatar-scaniverse-spz",
    "avatar-base-vrm",
    # avatar-gvrm sits here too: the in-browser preview viewer (vendored
    # naruya/gvrm.js) uses mkkellogg/gaussian-splats-3d 0.4.7, which only
    # decodes SPZ format versions 1-2. The current Niantic spz binding
    # outputs version 4, so we can't use SPZ for the bundled splat data
    # without a hard format-mismatch crash. Fall back to bundling raw PLY,
    # which exceeds Supabase Free's 50MB/file cap on real Scaniverse scans
    # (~70MB), so .gvrm goes back to the local Fly volume. Single-AZ
    # durability traded for end-to-end browser preview compatibility.
    "avatar-gvrm",
}


def storage_for_kind(kind: str | None) -> ObjectStorage:
    """Pick the right backend for an asset of the given kind.
    Avatar source uploads always go local (transient, large); everything else
    (including the avatar-gvrm output) uses the configured backend."""
    if kind in LOCAL_AVATAR_KINDS:
        return LocalStorage()
    return get_storage()


def storage_for_asset(asset) -> ObjectStorage:
    return storage_for_kind(getattr(asset, "kind", None))
