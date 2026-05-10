"""Tests for the in-app voice cloning service helper.

Run with::

    cd apps/api
    .venv/Scripts/pytest tests/test_voice_clone.py -v

Mocks httpx.AsyncClient to avoid contacting ElevenLabs during tests, and
uses a real SQLite (file:memory:?cache=shared not needed — tmp_path file)
so VoiceConsent / AvatarConfig writes exercise the actual SQLAlchemy path.
"""

from __future__ import annotations

import asyncio
import io
from contextlib import asynccontextmanager
from types import SimpleNamespace
from unittest.mock import patch

import pytest
from fastapi import HTTPException, UploadFile
from starlette.datastructures import Headers
from sqlalchemy import create_engine
from sqlalchemy.orm import Session, sessionmaker

from app.auth import CurrentUser
from app.db import Base
from app.models import AvatarConfig, SessionModel, VoiceConsent
from app.service import (
    VOICE_CLONE_MAX_SAMPLE_BYTES,
    VOICE_CLONE_MAX_TOTAL_BYTES,
    clone_voice_for_session,
)


# --- Fixtures ---------------------------------------------------------------


@pytest.fixture()
def db(tmp_path):
    """Real SQLite session bound to a fresh schema. Each test gets its own
    DB file so we never have to worry about leaked state between tests."""
    url = f"sqlite:///{tmp_path / 'test.db'}"
    engine = create_engine(url, future=True)
    Base.metadata.create_all(engine)
    Session_ = sessionmaker(bind=engine, expire_on_commit=False, future=True)
    session = Session_()
    try:
        yield session
    finally:
        session.close()
        engine.dispose()


@pytest.fixture()
def session_row(db: Session):
    """A SessionModel + AvatarConfig pair for clone tests."""
    sess = SessionModel(
        id="sess-1",
        owner_id="user-1",
        title="Test session",
        session_code="TEST01",
        physical_width_m=0.0,
        physical_height_m=0.0,
    ) if False else SessionModel(
        id="sess-1",
        owner_id="user-1",
        title="Test session",
        session_code="TEST01",
    )
    db.add(sess)
    db.add(AvatarConfig(session_id="sess-1", voice_id=None))
    db.commit()
    return sess


@pytest.fixture()
def current_user():
    return CurrentUser(user_id="user-1", role="presenter")


def _upload(filename: str, content: bytes, mime: str) -> UploadFile:
    """Build a Starlette UploadFile in-process for tests."""
    return UploadFile(
        filename=filename,
        file=io.BytesIO(content),
        headers=Headers({"content-type": mime}),
    )


def _patch_settings(monkeypatch, **overrides) -> None:
    base = dict(elevenlabs_api_key="fake-key")
    base.update(overrides)
    settings = SimpleNamespace(**base)
    from app.core import config as config_module
    from app import service as service_module

    monkeypatch.setattr(config_module, "get_settings", lambda: settings)
    monkeypatch.setattr(service_module, "get_settings", lambda: settings)


@asynccontextmanager
async def _async_noop():
    yield


def _make_async_client(post_fn):
    """Patch factory: returns an httpx.AsyncClient stand-in whose .post
    calls ``post_fn(url, headers, data, files)`` and returns its result."""

    class _FakeAsyncClient:
        def __init__(self, *args, **kwargs):
            pass

        async def __aenter__(self):
            return self

        async def __aexit__(self, *exc):
            return False

        async def post(self, url, *, headers=None, data=None, files=None):
            return post_fn(url, headers=headers, data=data, files=files)

    return _FakeAsyncClient


class _Resp:
    def __init__(self, status_code: int, body: dict | None = None, text: str | None = None):
        self.status_code = status_code
        self._body = body
        self.text = text or ("" if body is None else __import__("json").dumps(body))

    def json(self):
        if self._body is not None:
            return self._body
        import json
        return json.loads(self.text or "{}")


# --- Tests ------------------------------------------------------------------


def test_clone_requires_api_key(monkeypatch, db, session_row, current_user):
    _patch_settings(monkeypatch, elevenlabs_api_key=None)
    with pytest.raises(HTTPException) as exc:
        asyncio.run(
            clone_voice_for_session(
                db,
                session_id="sess-1",
                current_user=current_user,
                name="Voice 1",
                description=None,
                consent_label="I consent.",
                files=[_upload("a.mp3", b"\x00\x01\x02", "audio/mpeg")],
            )
        )
    assert exc.value.status_code == 503


def test_clone_rejects_empty_consent(monkeypatch, db, session_row, current_user):
    _patch_settings(monkeypatch)
    with pytest.raises(HTTPException) as exc:
        asyncio.run(
            clone_voice_for_session(
                db,
                session_id="sess-1",
                current_user=current_user,
                name="Voice 1",
                description=None,
                consent_label="",
                files=[_upload("a.mp3", b"x" * 1024, "audio/mpeg")],
            )
        )
    assert exc.value.status_code == 422
    assert "consent" in (exc.value.detail or "").lower()


def test_clone_rejects_unsupported_mime(monkeypatch, db, session_row, current_user):
    _patch_settings(monkeypatch)
    with pytest.raises(HTTPException) as exc:
        asyncio.run(
            clone_voice_for_session(
                db,
                session_id="sess-1",
                current_user=current_user,
                name="Voice 1",
                description=None,
                consent_label="I consent",
                files=[_upload("doc.pdf", b"%PDF-1.4", "application/pdf")],
            )
        )
    assert exc.value.status_code == 422
    assert "mime" in (exc.value.detail or "").lower()


def test_clone_rejects_oversize_sample(monkeypatch, db, session_row, current_user):
    _patch_settings(monkeypatch)
    payload = b"\x00" * (VOICE_CLONE_MAX_SAMPLE_BYTES + 1)
    with pytest.raises(HTTPException) as exc:
        asyncio.run(
            clone_voice_for_session(
                db,
                session_id="sess-1",
                current_user=current_user,
                name="V",
                description=None,
                consent_label="I consent",
                files=[_upload("big.mp3", payload, "audio/mpeg")],
            )
        )
    assert exc.value.status_code == 413


def test_clone_success_writes_consent_and_voice_id(monkeypatch, db, session_row, current_user):
    _patch_settings(monkeypatch)

    captured = {}

    def post(url, *, headers, data, files):
        captured["url"] = url
        captured["headers"] = headers
        captured["data"] = dict(data) if data else {}
        # files is a list of (field, (filename, content, mime)) tuples; copy.
        captured["files"] = [(name, (filename, content, mime)) for name, (filename, content, mime) in files]
        return _Resp(200, {"voice_id": "voice-XYZ"})

    import httpx

    with patch.object(httpx, "AsyncClient", _make_async_client(post)):
        voice_id, consent, updated_config = asyncio.run(
            clone_voice_for_session(
                db,
                session_id="sess-1",
                current_user=current_user,
                name="Presenter A",
                description="Cloned for CHI demo",
                consent_label="I, the presenter, consent to TTS use of my voice.",
                files=[_upload("take1.webm", b"OggS\x00" + b"\x00" * 4096, "audio/webm")],
            )
        )

    # Upstream contract
    assert captured["url"] == "https://api.elevenlabs.io/v1/voices/add"
    assert captured["headers"]["xi-api-key"] == "fake-key"
    assert captured["data"]["name"] == "Presenter A"
    assert captured["data"]["remove_background_noise"] == "false"
    assert captured["data"]["description"] == "Cloned for CHI demo"
    assert len(captured["files"]) == 1
    field, (filename, _content, mime) = captured["files"][0]
    assert field == "files"
    assert filename == "take1.webm"
    assert mime == "audio/webm"

    # Persistence contract
    assert voice_id == "voice-XYZ"
    persisted = db.scalars(
        __import__("sqlalchemy").select(VoiceConsent).where(VoiceConsent.voice_id == voice_id)
    ).first()
    assert persisted is not None
    assert persisted.consent_label.startswith("I, the presenter")
    assert updated_config is not None
    assert updated_config.voice_id == "voice-XYZ"


def test_clone_does_not_set_voice_id_when_disabled(monkeypatch, db, session_row, current_user):
    """Audition mode: consent + voice_id returned but session config untouched."""
    _patch_settings(monkeypatch)

    def post(url, *, headers, data, files):
        return _Resp(200, {"voice_id": "voice-AUDITION"})

    import httpx

    with patch.object(httpx, "AsyncClient", _make_async_client(post)):
        voice_id, _consent, updated_config = asyncio.run(
            clone_voice_for_session(
                db,
                session_id="sess-1",
                current_user=current_user,
                name="Voice",
                description=None,
                consent_label="OK",
                files=[_upload("a.mp3", b"x" * 1024, "audio/mpeg")],
                set_as_session_voice=False,
            )
        )
    assert voice_id == "voice-AUDITION"
    assert updated_config is None
    config = db.get(AvatarConfig, "sess-1")
    assert config.voice_id is None  # untouched


def test_clone_propagates_elevenlabs_4xx(monkeypatch, db, session_row, current_user):
    """Plan-doesn't-allow-cloning style errors should reach the operator
    verbatim, not get reduced to a generic 502."""
    _patch_settings(monkeypatch)

    def post(url, *, headers, data, files):
        return _Resp(403, text='{"detail":{"status":"plan_does_not_allow_voice_cloning"}}')

    import httpx

    with patch.object(httpx, "AsyncClient", _make_async_client(post)):
        with pytest.raises(HTTPException) as exc:
            asyncio.run(
                clone_voice_for_session(
                    db,
                    session_id="sess-1",
                    current_user=current_user,
                    name="V",
                    description=None,
                    consent_label="OK",
                    files=[_upload("a.mp3", b"x" * 1024, "audio/mpeg")],
                )
            )
    assert exc.value.status_code == 403
    assert "plan_does_not_allow_voice_cloning" in str(exc.value.detail)


def test_clone_handles_missing_voice_id_in_response(monkeypatch, db, session_row, current_user):
    _patch_settings(monkeypatch)

    def post(url, *, headers, data, files):
        return _Resp(200, {})  # well-formed JSON but missing voice_id

    import httpx

    with patch.object(httpx, "AsyncClient", _make_async_client(post)):
        with pytest.raises(HTTPException) as exc:
            asyncio.run(
                clone_voice_for_session(
                    db,
                    session_id="sess-1",
                    current_user=current_user,
                    name="V",
                    description=None,
                    consent_label="OK",
                    files=[_upload("a.mp3", b"x" * 1024, "audio/mpeg")],
                )
            )
    assert exc.value.status_code == 502
    assert "no voice_id" in str(exc.value.detail).lower()


def test_clone_creates_avatar_config_when_missing(monkeypatch, db, current_user):
    """A session that hasn't visited the Avatar tab yet has no AvatarConfig
    row. The clone path should create one rather than orphaning the new
    voice_id."""
    _patch_settings(monkeypatch)
    # Session row with no AvatarConfig
    db.add(
        SessionModel(
            id="sess-2",
            owner_id="user-1",
            title="Bare",
            session_code="BARE01",
        )
    )
    db.commit()

    def post(url, *, headers, data, files):
        return _Resp(200, {"voice_id": "voice-NEW"})

    import httpx

    with patch.object(httpx, "AsyncClient", _make_async_client(post)):
        voice_id, _consent, updated_config = asyncio.run(
            clone_voice_for_session(
                db,
                session_id="sess-2",
                current_user=current_user,
                name="V",
                description=None,
                consent_label="OK",
                files=[_upload("a.mp3", b"x" * 1024, "audio/mpeg")],
            )
        )
    assert voice_id == "voice-NEW"
    assert updated_config is not None
    fresh = db.get(AvatarConfig, "sess-2")
    assert fresh.voice_id == "voice-NEW"
