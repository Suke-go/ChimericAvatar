from collections.abc import Generator

from sqlalchemy import create_engine, event
from sqlalchemy.engine import Engine
from sqlalchemy.orm import DeclarativeBase, Session, sessionmaker

from app.core.config import get_settings


class Base(DeclarativeBase):
    pass


settings = get_settings()


def _normalize_database_url(url: str) -> str:
    # Supabase / Render hand out postgres://; SQLAlchemy 2 wants postgresql://.
    if url.startswith("postgres://"):
        return "postgresql+psycopg://" + url[len("postgres://") :]
    if url.startswith("postgresql://") and "+psycopg" not in url and "+asyncpg" not in url:
        return "postgresql+psycopg://" + url[len("postgresql://") :]
    return url


_database_url = _normalize_database_url(settings.database_url)
connect_args = {"check_same_thread": False} if _database_url.startswith("sqlite") else {}
engine_kwargs: dict = {"connect_args": connect_args}
if _database_url.startswith("postgresql"):
    # Free-tier Supabase uses pgbouncer in transaction mode; keep pool short.
    engine_kwargs.update(pool_pre_ping=True, pool_size=5, max_overflow=5)

engine = create_engine(_database_url, **engine_kwargs)
SessionLocal = sessionmaker(bind=engine, autoflush=False, autocommit=False, expire_on_commit=False)


# SQLite skips foreign-key enforcement by default, so ON DELETE CASCADE on
# child tables (knowledge_chunks, simulated_qas, etc.) silently no-ops.
# Postgres always enforces, so this hook only fires on the local-dev path.
if _database_url.startswith("sqlite"):
    @event.listens_for(Engine, "connect")
    def _enable_sqlite_foreign_keys(dbapi_connection, _conn_record):
        cursor = dbapi_connection.cursor()
        cursor.execute("PRAGMA foreign_keys=ON")
        cursor.close()


def get_db() -> Generator[Session, None, None]:
    db = SessionLocal()
    try:
        yield db
    finally:
        db.close()
