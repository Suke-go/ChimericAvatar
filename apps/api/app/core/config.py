from functools import lru_cache
from pathlib import Path
from typing import Literal

from pydantic import Field, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=".env",
        env_file_encoding="utf-8",
        extra="ignore",
        # Disable pydantic-settings v2 auto JSON decoding for list/dict fields
        # so the field_validator below can accept comma-separated strings.
        enable_decoding=False,
    )

    api_title: str = "Chimera Presenter API"
    api_prefix: str = "/api/v1"
    api_public_base_url: str = "http://localhost:8000"
    app_environment: Literal["local", "staging", "production"] = "local"
    database_url: str = "sqlite:///./chimera_lab.db"
    upload_root: Path = Field(default=Path("data/uploads"))
    cors_allow_origins: list[str] = Field(
        default_factory=lambda: ["http://localhost:3000", "http://127.0.0.1:3000"]
    )
    debug_auth_enabled: bool = False
    supabase_url: str | None = None
    # New API key system (Nov 2025+): sb_secret_... replaces service_role.
    # Legacy fields kept for projects created before Nov 2025.
    supabase_secret_key: str | None = None
    supabase_publishable_key: str | None = None
    supabase_service_role_key: str | None = None
    # New JWT signing keys (asymmetric / JWKS) discovered via SUPABASE_URL.
    # Legacy HS256 jwt_secret kept for older projects.
    supabase_jwt_secret: str | None = None
    supabase_jwks_url: str | None = None
    supabase_jwt_audience: str = "authenticated"
    supabase_jwt_issuer: str | None = None
    supabase_storage_bucket: str = "session-assets-private"
    storage_backend: Literal["local", "supabase"] = "local"
    asset_url_secret: str = "local-dev-asset-secret-change-me"
    # 30 minutes — long enough for browser cache to be reused on reload but
    # still short enough that revoked posters become inaccessible quickly.
    asset_url_ttl_seconds: int = 1800
    max_poster_upload_bytes: int = 50 * 1024 * 1024
    openai_model: str = "gpt-5.4-mini"
    openai_reasoning_effort: str = "low"
    openai_embedding_model: str = "text-embedding-3-large"
    openai_embedding_dimensions: int = 1536
    elevenlabs_api_key: str | None = None
    elevenlabs_default_voice_id: str | None = None
    elevenlabs_model_id: str = "eleven_multilingual_v2"
    elevenlabs_streaming_model_id: str = "eleven_flash_v2_5"
    elevenlabs_output_format: str = "mp3_44100_128"
    max_knowledge_text_chars: int = 300_000
    knowledge_chunk_chars: int = 1200
    knowledge_chunk_overlap_chars: int = 160
    openai_api_key: str | None = None
    openai_ocr_model: str = "gpt-5.4-mini"
    openai_script_temperature: float = 0.2
    max_knowledge_upload_bytes: int = 80 * 1024 * 1024
    max_avatar_upload_bytes: int = 256 * 1024 * 1024
    # Default base humanoid VRM used when the user uploads a PLY/SPZ scan
    # without supplying their own VRM. The bundled file should be placed at
    # `apps/api/avatar-base/fem_vroid.vrm` (CC0, madjin/vrm-samples).
    avatar_base_dir: Path = Field(default=Path("avatar-base"))
    avatar_default_vrm_filename: str = "fem_vroid.vrm"

    @field_validator("cors_allow_origins", mode="before")
    @classmethod
    def _split_origins(cls, value):
        # Allow CORS_ALLOW_ORIGINS in .env to be either JSON ("[...]") or
        # comma-separated ("https://a.com,https://b.com"). Pydantic v2's
        # default complex-field decoder only accepts JSON, which trips up
        # operators copy-pasting Vercel URLs.
        if isinstance(value, str):
            value = value.strip()
            if not value:
                return []
            if value.startswith("["):
                return value
            return [item.strip() for item in value.split(",") if item.strip()]
        return value


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    return Settings()
