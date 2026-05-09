"""add build_version to avatar_build_jobs

Revision ID: a1c4f9e8d2b3
Revises: 98d2531ced05
Create Date: 2026-05-08 12:00:00.000000

Adds `build_version` column so the dashboard can detect stale .gvrm builds
relative to the current server-side preprocess algorithm version
(`avatar_preprocess.BUILD_VERSION`) and recommend a manual rebuild.
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


# revision identifiers, used by Alembic.
revision: str = 'a1c4f9e8d2b3'
down_revision: Union[str, Sequence[str], None] = '98d2531ced05'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    with op.batch_alter_table("avatar_build_jobs") as batch_op:
        batch_op.add_column(
            sa.Column("build_version", sa.String(length=32), nullable=True)
        )


def downgrade() -> None:
    with op.batch_alter_table("avatar_build_jobs") as batch_op:
        batch_op.drop_column("build_version")
