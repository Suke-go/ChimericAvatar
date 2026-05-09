"""add voice_samples + voice_id on avatar_configs

Revision ID: 53d47fe56988
Revises: 90e9b32b49ec
Create Date: 2026-05-08 02:52:59.710802

"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


# revision identifiers, used by Alembic.
revision: str = '53d47fe56988'
down_revision: Union[str, Sequence[str], None] = '90e9b32b49ec'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    """Add voice_id to avatar_configs + create voice_samples table."""
    with op.batch_alter_table("avatar_configs") as batch_op:
        batch_op.add_column(sa.Column("voice_id", sa.String(length=128), nullable=True))

    op.create_table(
        "voice_samples",
        sa.Column("id", sa.String(length=36), nullable=False),
        sa.Column("cache_key", sa.String(length=64), nullable=False),
        sa.Column("voice_id", sa.String(length=128), nullable=False),
        sa.Column("text", sa.Text(), nullable=False),
        sa.Column("language", sa.String(length=8), nullable=False, server_default="ja"),
        sa.Column("model_id", sa.String(length=80), nullable=False),
        sa.Column("storage_path", sa.String(length=512), nullable=False),
        sa.Column("size_bytes", sa.Integer(), nullable=False),
        sa.Column(
            "created_at",
            sa.DateTime(timezone=True),
            server_default=sa.func.now(),
            nullable=False,
        ),
        sa.PrimaryKeyConstraint("id"),
        sa.UniqueConstraint("cache_key", name="uq_voice_samples_cache_key"),
    )
    op.create_index("ix_voice_samples_voice_id", "voice_samples", ["voice_id"])


def downgrade() -> None:
    op.drop_index("ix_voice_samples_voice_id", table_name="voice_samples")
    op.drop_table("voice_samples")
    with op.batch_alter_table("avatar_configs") as batch_op:
        batch_op.drop_column("voice_id")
