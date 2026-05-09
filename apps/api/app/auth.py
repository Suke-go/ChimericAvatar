from __future__ import annotations

from dataclasses import dataclass
from functools import lru_cache

import jwt
from fastapi import Depends, Header, HTTPException, status
from jwt import PyJWKClient
from sqlalchemy.orm import Session

from app.core.config import get_settings
from app.db import get_db
from app.models import AppUser

# Algorithms Supabase Auth signs JWTs with. Legacy projects use HS256 with a
# shared JWT secret. New (Nov 2025+) projects sign with asymmetric keys
# (RS256 by default, optionally ES256 / EdDSA) discoverable via JWKS.
SUPPORTED_ASYMMETRIC_ALGORITHMS = ["RS256", "RS384", "RS512", "ES256", "ES384", "EdDSA"]


@dataclass
class CurrentUser:
    user_id: str
    role: str


def _get_or_sync_user(db: Session, user_id: str, role: str) -> CurrentUser:
    if role not in {"admin", "member"}:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Invalid role")
    user = db.get(AppUser, user_id)
    if user is None:
        user = AppUser(user_id=user_id, role=role)
        db.add(user)
        db.commit()
        db.refresh(user)
    elif user.role != role:
        user.role = role
        db.commit()
        db.refresh(user)
    return CurrentUser(user_id=user.user_id, role=user.role)


def _resolve_jwks_url() -> str | None:
    settings = get_settings()
    if settings.supabase_jwks_url:
        return settings.supabase_jwks_url
    if settings.supabase_url:
        return f"{settings.supabase_url.rstrip('/')}/auth/v1/.well-known/jwks.json"
    return None


@lru_cache(maxsize=1)
def _jwks_client() -> PyJWKClient | None:
    url = _resolve_jwks_url()
    if not url:
        return None
    # PyJWKClient caches keys in-process; ttl + lifespan default fits our
    # short-lived FastAPI process model.
    return PyJWKClient(url, cache_keys=True, lifespan=3600)


def _decode_jwt(token: str) -> dict:
    settings = get_settings()
    audience = settings.supabase_jwt_audience or None
    issuer = settings.supabase_jwt_issuer or None

    # Inspect the token header to see which algorithm was used by Supabase Auth.
    try:
        unverified_header = jwt.get_unverified_header(token)
    except jwt.PyJWTError as exc:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid token header") from exc
    algorithm = unverified_header.get("alg")

    options: dict = {}
    if not audience:
        options["verify_aud"] = False
    if not issuer:
        options["verify_iss"] = False

    # New asymmetric path: discover the public key via JWKS and verify locally.
    if algorithm in SUPPORTED_ASYMMETRIC_ALGORITHMS:
        client = _jwks_client()
        if client is None:
            raise HTTPException(
                status_code=status.HTTP_401_UNAUTHORIZED,
                detail="Server has no JWKS configured for asymmetric Supabase JWTs (set SUPABASE_URL or SUPABASE_JWKS_URL)",
            )
        try:
            signing_key = client.get_signing_key_from_jwt(token).key
            return jwt.decode(
                token,
                signing_key,
                algorithms=SUPPORTED_ASYMMETRIC_ALGORITHMS,
                audience=audience,
                issuer=issuer,
                options=options,
            )
        except jwt.PyJWTError as exc:
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid token") from exc

    # Legacy path: HS256 with the shared JWT secret.
    if algorithm == "HS256":
        if not settings.supabase_jwt_secret:
            raise HTTPException(
                status_code=status.HTTP_401_UNAUTHORIZED,
                detail="Legacy HS256 JWT received but SUPABASE_JWT_SECRET is not configured",
            )
        try:
            return jwt.decode(
                token,
                settings.supabase_jwt_secret,
                algorithms=["HS256"],
                audience=audience,
                issuer=issuer,
                options=options,
            )
        except jwt.PyJWTError as exc:
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid token") from exc

    raise HTTPException(
        status_code=status.HTTP_401_UNAUTHORIZED,
        detail=f"Unsupported JWT algorithm: {algorithm}",
    )


def _supabase_auth_configured() -> bool:
    settings = get_settings()
    return bool(_resolve_jwks_url() or settings.supabase_jwt_secret)


def get_current_user(
    db: Session = Depends(get_db),
    authorization: str | None = Header(default=None),
    x_debug_user: str | None = Header(default=None),
    x_debug_role: str | None = Header(default=None),
) -> CurrentUser:
    settings = get_settings()

    if settings.debug_auth_enabled and x_debug_user:
        return _get_or_sync_user(db, x_debug_user, x_debug_role or "member")

    if authorization and authorization.startswith("Bearer ") and _supabase_auth_configured():
        token = authorization.removeprefix("Bearer ").strip()
        payload = _decode_jwt(token)
        user_id = str(payload.get("sub") or "")
        # Supabase puts custom claims under app_metadata; admins must have
        # app_metadata.app_role = "admin" set via the Supabase dashboard or
        # admin API.
        app_metadata = payload.get("app_metadata") or {}
        role = str(payload.get("app_role") or app_metadata.get("app_role") or "member")
        if not user_id:
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Missing subject")
        return _get_or_sync_user(db, user_id, role)

    raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Authentication required")


def require_admin(current_user: CurrentUser = Depends(get_current_user)) -> CurrentUser:
    if current_user.role != "admin":
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Admin access required")
    return current_user
