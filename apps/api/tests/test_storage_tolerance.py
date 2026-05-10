"""Tests for the missing-asset tolerance contract.

Run with::

    cd apps/api
    .venv/Scripts/pytest tests/test_storage_tolerance.py -v

These tests cover the Tier 1 / Tier 3 storage primitives that the manifest
builder and `/poster/image-url` lean on. Tier 2 (the manifest builder
itself) is not covered here because doing so would require spinning the
full FastAPI app + DB; once `try_sign_asset` returns ``None`` for missing
objects, the manifest's collection of `missing` entries is mechanical.
"""

from __future__ import annotations

import json
from types import SimpleNamespace
from unittest.mock import patch

import httpx
import pytest

from app.storage import (
    ASSET_STATUS_MISSING,
    LocalStorage,
    StorageObjectMissing,
    SupabaseStorage,
    try_sign_asset,
)


class _FakeAsset:
    """Stand-in for the SQLAlchemy Asset row. Tests don't need a real DB to
    verify the auto-heal write — try_sign_asset only touches `.status` and
    calls `db.commit()`."""

    def __init__(self, asset_id: str, kind: str, storage_path: str, status: str = "ready") -> None:
        self.id = asset_id
        self.kind = kind
        self.storage_path = storage_path
        self.status = status


class _FakeDb:
    """Just enough Session API for try_sign_asset's commit/rollback path."""

    def __init__(self) -> None:
        self.committed = 0
        self.rolled_back = 0

    def commit(self) -> None:
        self.committed += 1

    def rollback(self) -> None:
        self.rolled_back += 1


def _patch_settings(monkeypatch, settings) -> None:
    """Patch every module that captured ``get_settings`` at import time.

    `app.storage` does ``from app.core.config import get_settings`` at the
    top, so monkeypatching only ``app.core.config.get_settings`` doesn't
    affect the symbol bound inside `storage.py`. Patch both.
    """
    from app.core import config as config_module
    from app import storage as storage_module

    monkeypatch.setattr(config_module, "get_settings", lambda: settings)
    monkeypatch.setattr(storage_module, "get_settings", lambda: settings)


def test_local_storage_raises_missing_when_file_is_gone(tmp_path, monkeypatch):
    # LocalStorage signs a URL that the FastAPI download route would later
    # serve, but issuing a signed URL for a path that doesn't exist on disk
    # is precisely the case the runtime client only learns about after the
    # download fails. Surface it up front.
    settings = SimpleNamespace(
        upload_root=tmp_path,
        asset_url_secret="test-secret",
        asset_url_ttl_seconds=60,
        api_public_base_url="http://localhost:8000",
    )
    _patch_settings(monkeypatch, settings)

    storage = LocalStorage()
    with pytest.raises(StorageObjectMissing) as exc:
        storage.signed_download_url("asset-xyz", "sessions/foo/poster/missing.png")
    assert exc.value.storage_path == "sessions/foo/poster/missing.png"


def test_local_storage_signs_when_file_exists(tmp_path, monkeypatch):
    settings = SimpleNamespace(
        upload_root=tmp_path,
        asset_url_secret="test-secret",
        asset_url_ttl_seconds=60,
        api_public_base_url="http://localhost:8000",
    )
    _patch_settings(monkeypatch, settings)

    rel = "sessions/foo/poster/poster.png"
    target = tmp_path / rel
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(b"\x89PNG\r\n\x1a\n")

    storage = LocalStorage()
    url, expires_at = storage.signed_download_url("asset-xyz", rel)
    assert "asset-xyz" in url
    assert "token=" in url
    assert expires_at > 0


def _supabase_settings(monkeypatch) -> None:
    """Minimal settings to construct a SupabaseStorage without touching env."""
    settings = SimpleNamespace(
        supabase_url="https://example.supabase.co",
        supabase_secret_key="sb_secret_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        supabase_service_role_key=None,
        supabase_storage_bucket="session-assets-private",
        asset_url_ttl_seconds=60,
    )
    _patch_settings(monkeypatch, settings)


def test_supabase_storage_raises_missing_on_inner_404(monkeypatch):
    """Supabase wraps storage 404s in an HTTP 400 envelope:

        {"statusCode": "404", "error": "not_found", "message": "Object not found"}

    The original code reported this as a generic 502, which crashed the
    whole manifest. Now it should surface as ``StorageObjectMissing``.
    """
    _supabase_settings(monkeypatch)
    storage = SupabaseStorage()

    class _Resp:
        status_code = 400
        text = json.dumps(
            {"statusCode": "404", "error": "not_found", "message": "Object not found"}
        )

        def json(self):  # noqa: D401
            return json.loads(self.text)

    class _Client:
        def __init__(self, *args, **kwargs):
            pass

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            return False

        def post(self, *args, **kwargs):  # noqa: ARG002
            return _Resp()

    with patch.object(httpx, "Client", _Client):
        with pytest.raises(StorageObjectMissing):
            storage.signed_download_url("asset-1", "sessions/x/poster/foo.png")


def test_supabase_storage_keeps_502_on_real_5xx(monkeypatch):
    """Server errors that aren't object-not-found should still raise the
    original 502 HTTPException, otherwise transient bucket outages would
    silently produce empty Preview pages."""
    _supabase_settings(monkeypatch)
    storage = SupabaseStorage()

    class _Resp:
        status_code = 503
        text = '{"error":"unavailable"}'

        def json(self):
            return {}

    class _Client:
        def __init__(self, *args, **kwargs):
            pass

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            return False

        def post(self, *args, **kwargs):  # noqa: ARG002
            return _Resp()

    from fastapi import HTTPException

    with patch.object(httpx, "Client", _Client):
        with pytest.raises(HTTPException) as exc:
            storage.signed_download_url("asset-1", "sessions/x/poster/foo.png")
    assert exc.value.status_code == 502


def test_try_sign_asset_returns_none_for_none_asset():
    db = _FakeDb()
    assert try_sign_asset(db, None) is None
    assert db.committed == 0


def test_try_sign_asset_marks_missing_and_commits(monkeypatch):
    """Confirm the auto-heal: a backend that raises StorageObjectMissing
    causes try_sign_asset to flip Asset.status='missing' and commit."""
    from app import storage as storage_module

    asset = _FakeAsset(
        asset_id="asset-1",
        kind="poster",
        storage_path="sessions/x/poster/foo.png",
        status="ready",
    )

    class _RaisingBackend:
        def signed_download_url(self, asset_id, path, ttl_seconds=None):  # noqa: ARG002
            raise StorageObjectMissing(path, "test")

    monkeypatch.setattr(
        storage_module, "storage_for_asset", lambda _a: _RaisingBackend()
    )

    db = _FakeDb()
    result = try_sign_asset(db, asset)
    assert result is None
    assert asset.status == ASSET_STATUS_MISSING
    assert db.committed == 1
    assert db.rolled_back == 0


def test_try_sign_asset_returns_url_on_success(monkeypatch):
    from app import storage as storage_module

    asset = _FakeAsset(
        asset_id="asset-1",
        kind="poster",
        storage_path="sessions/x/poster/foo.png",
        status="ready",
    )

    class _OkBackend:
        def signed_download_url(self, asset_id, path, ttl_seconds=None):  # noqa: ARG002
            return ("https://example.test/signed", 1_700_000_000)

    monkeypatch.setattr(storage_module, "storage_for_asset", lambda _a: _OkBackend())

    db = _FakeDb()
    result = try_sign_asset(db, asset)
    assert result == ("https://example.test/signed", 1_700_000_000)
    assert asset.status == "ready"  # untouched on success
    assert db.committed == 0


def test_try_sign_asset_does_not_double_commit_when_already_missing(monkeypatch):
    """If an asset is already flagged missing, repeated try_sign_asset
    calls should not generate write traffic to the DB."""
    from app import storage as storage_module

    asset = _FakeAsset(
        asset_id="asset-1",
        kind="poster",
        storage_path="sessions/x/poster/foo.png",
        status=ASSET_STATUS_MISSING,
    )

    class _RaisingBackend:
        def signed_download_url(self, asset_id, path, ttl_seconds=None):  # noqa: ARG002
            raise StorageObjectMissing(path, "test")

    monkeypatch.setattr(
        storage_module, "storage_for_asset", lambda _a: _RaisingBackend()
    )

    db = _FakeDb()
    assert try_sign_asset(db, asset) is None
    assert db.committed == 0
