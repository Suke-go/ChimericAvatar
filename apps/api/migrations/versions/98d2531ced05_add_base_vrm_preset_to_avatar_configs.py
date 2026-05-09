"""add base_vrm_preset to avatar_configs

Revision ID: 98d2531ced05
Revises: 53d47fe56988
Create Date: 2026-05-08 03:43:24.544972

"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


# revision identifiers, used by Alembic.
revision: str = '98d2531ced05'
down_revision: Union[str, Sequence[str], None] = '53d47fe56988'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    with op.batch_alter_table("avatar_configs") as batch_op:
        batch_op.add_column(sa.Column("base_vrm_preset", sa.String(length=64), nullable=True))


def downgrade() -> None:
    with op.batch_alter_table("avatar_configs") as batch_op:
        batch_op.drop_column("base_vrm_preset")
