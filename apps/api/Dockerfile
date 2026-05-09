FROM python:3.11-slim AS runtime

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_NO_CACHE_DIR=1 \
    PORT=8000

WORKDIR /app

# Install system deps required by PyMuPDF + psycopg + nianticlabs/spz
# (cmake / git / libzstd-dev / zlib1g-dev for the SPZ C++ build).
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
    build-essential \
    cmake \
    git \
    libpq5 \
    libzstd-dev \
    python3-dev \
    zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*

COPY pyproject.toml ./
RUN pip install --upgrade pip && pip install ".[postgres,migrations,spz]"

COPY app ./app
COPY migrations ./migrations
COPY alembic.ini ./
# Bundled base humanoid VRM (CC0). avatar_build.py reads this when the
# user uploads a PLY without a custom VRM. AVATAR_BASE_DIR below stays
# relative to WORKDIR (/app) so the path resolves to /app/avatar-base/.
COPY avatar-base ./avatar-base

RUN useradd --uid 1000 --create-home chimera && \
    mkdir -p /data/uploads && chown -R chimera:chimera /data /app
USER chimera

ENV UPLOAD_ROOT=/data/uploads \
    AVATAR_BASE_DIR=/app/avatar-base \
    AVATAR_DEFAULT_VRM_FILENAME=fem_vroid.vrm

EXPOSE 8000
# Schema bootstrap + alembic upgrade are handled by app startup
# (`_bootstrap_postgres_if_fresh` in app/main.py) so we don't need to chain
# `alembic upgrade head` here. The startup hook detects fresh vs existing
# databases and picks the right path (avoids the circular-FK bug in the
# auto-generated initial migration's table ordering).
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000", "--proxy-headers", "--forwarded-allow-ips", "*"]
