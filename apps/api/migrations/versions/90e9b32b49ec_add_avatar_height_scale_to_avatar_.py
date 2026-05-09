"""add avatar_height_scale to avatar_configs

Revision ID: 90e9b32b49ec
Revises: 8a28d65d2f13
Create Date: 2026-05-07 21:04:46.995759

"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


# revision identifiers, used by Alembic.
revision: str = '90e9b32b49ec'
down_revision: Union[str, Sequence[str], None] = '8a28d65d2f13'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    """Add avatar_height_scale column with default 1.0 (= no resize)."""
    with op.batch_alter_table("avatar_configs") as batch_op:
        batch_op.add_column(
            sa.Column(
                "avatar_height_scale",
                sa.Float(),
                nullable=False,
                server_default="1.0",
            )
        )


def downgrade() -> None:
    with op.batch_alter_table("avatar_configs") as batch_op:
        batch_op.drop_column("avatar_height_scale")
