"""Regression tests for the avatar preprocess pipeline. Run with:

    cd apps/api && OPENAI_API_KEY=sk-test .venv/Scripts/pytest tests/test_avatar_preprocess.py -v
"""

from __future__ import annotations

import io
from pathlib import Path

import numpy as np
import pytest
from plyfile import PlyData, PlyElement

from app.avatar_preprocess import (
    BONE_CONFIG,
    BUILD_VERSION,
    _build_capsules,
    _bin_vertices_to_bones,
    _assign_splats_to_bones,
    _assign_splats_to_points,
    _filter_ply_bytes,
    parse_ply_centers,
    parse_vrm_skeleton,
    preprocess_gvrm,
)


_API_DIR = Path(__file__).resolve().parent.parent
_FEM_VROID = _API_DIR / "avatar-base" / "fem_vroid.vrm"


@pytest.fixture(scope="module")
def vrm_bytes() -> bytes:
    if not _FEM_VROID.is_file():
        pytest.skip(f"bundled fem_vroid.vrm not found at {_FEM_VROID}")
    return _FEM_VROID.read_bytes()


@pytest.fixture(scope="module")
def skel(vrm_bytes):
    return parse_vrm_skeleton(vrm_bytes)


def _make_ply(points: np.ndarray) -> bytes:
    points = np.asarray(points, dtype=np.float32)
    vert = np.zeros(points.shape[0], dtype=[("x", "f4"), ("y", "f4"), ("z", "f4")])
    vert["x"] = points[:, 0]
    vert["y"] = points[:, 1]
    vert["z"] = points[:, 2]
    buf = io.BytesIO()
    PlyData([PlyElement.describe(vert, "vertex")], text=False).write(buf)
    return buf.getvalue()


def test_skeleton_parses_with_expected_bones(skel):
    # All BONE_CONFIG names should be reachable in the skeleton (HeadTop_End
    # synthesized when missing — fem_vroid.vrm doesn't ship one).
    for group in BONE_CONFIG.values():
        for name in group["names"]:
            assert name in skel.bone_index_by_name, f"missing bone: {name}"


def test_skeleton_height_is_humanoid(skel):
    # fem_vroid is ~1.58m tall — sanity that our LBS posed-vertex pass works
    # in the rotated VRM 0.x frame.
    height = skel.posed_vertices[:, 1].max() - skel.posed_vertices[:, 1].min()
    assert 1.4 < height < 1.8, f"unexpected body height {height:.2f}m"


def test_capsule_count_matches_bone_config(skel):
    # 4 arm + 4 leg + 4 torso + 1 headTop + 1 head = 14
    expected = sum(len(g["names"]) for g in BONE_CONFIG.values())
    capsules = _build_capsules(skel)
    assert len(capsules) == expected, (
        f"expected {expected} capsules from BONE_CONFIG, got {len(capsules)}"
    )


def test_capsule_position_at_segment_midpoint(skel):
    # Each capsule's centroid should sit roughly at the midpoint of its
    # parent→child segment (within radius). Sanity-check the L_LowerArm
    # capsule which goes UpperArm → LowerArm.
    capsules = _build_capsules(skel)
    cap = next(c for c in capsules if skel.bones[c.bone_index].name == "J_Bip_L_LowerArm")
    bone = skel.bones[cap.bone_index]
    parent = skel.bones[bone.parent_index]
    expected_mid = (bone.world_matrix[:3, 3] + parent.world_matrix[:3, 3]) / 2
    centroid = cap.triangles.reshape(-1, 3).mean(axis=0)
    assert np.linalg.norm(centroid - expected_mid) < 0.1


def test_assign_splats_to_bones_picks_closest_capsule(skel):
    # A splat placed at the L_Hand bone position should bind to L_Hand.
    capsules = _build_capsules(skel)
    hand_idx = skel.bone_index_by_name["J_Bip_L_Hand"]
    hand_pos = skel.bones[hand_idx].world_matrix[:3, 3]
    splat = hand_pos[None, :].astype(np.float32)
    assigned = _assign_splats_to_bones(splat, capsules)
    assert int(assigned[0]) == hand_idx


def test_relative_poses_small_for_body_shape_splats(skel):
    # Sample ~500 mesh vertices with small jitter — the resulting binding
    # should produce small relative poses (< 0.3m). Larger values would
    # mean we're binding splats to far-away anchors, which would visually
    # break the avatar.
    rng = np.random.default_rng(123)
    sample_idx = rng.integers(0, skel.posed_vertices.shape[0], size=500)
    splats = skel.posed_vertices[sample_idx] + rng.normal(0, 0.03, (500, 3)).astype(np.float32)
    capsules = _build_capsules(skel)
    bone_idx = _assign_splats_to_bones(splats, capsules)
    bins = _bin_vertices_to_bones(skel, capsules)
    _, rel = _assign_splats_to_points(splats, bone_idx, skel, bins)
    norms = np.linalg.norm(rel, axis=1)
    assert norms.max() < 0.3, f"max relative pose {norms.max():.3f} > 0.3m"


def test_preprocess_gvrm_returns_full_data_json(vrm_bytes, skel):
    """End-to-end test of the preprocess pipeline. Generates enough synthetic
    splats (>10k) for cleanSplats's validation to pass, in the PLY Y-down
    convention naruya's algorithm expects (negate Y from glTF Y-up posed
    vertices). Adds small jitter so the scan looks like a real Scaniverse
    output rather than sitting exactly on mesh vertices.
    """
    rng = np.random.default_rng(42)
    n = 50000
    sample_idx = rng.integers(0, skel.posed_vertices.shape[0], size=n)
    splats = skel.posed_vertices[sample_idx].astype(np.float32).copy()
    splats += rng.normal(0, 0.01, splats.shape).astype(np.float32)
    # Flip Y to PLY convention (Y-down). cleanSplats uses -vertex.y as
    # "height up" because the runtime's gsQuaternion default = [0,0,1,0]
    # (180° Z rotation) inverts Y at render time.
    splats[:, 1] = -splats[:, 1]
    ply_bytes = _make_ply(splats)

    dj, aligned_ply = preprocess_gvrm(ply_bytes, vrm_bytes, height_scale=1.07)
    assert isinstance(aligned_ply, bytes) and aligned_ply.startswith(b"ply")
    # Every key naruya's gvrm.js writer expects must be present.
    for key in (
        "modelScale",
        "boneOperations",
        "gsPosition",
        "gsQuaternion",
        "splatVertexIndices",
        "splatBoneIndices",
        "splatRelativePoses",
        "_buildVersion",
    ):
        assert key in dj, f"missing data.json key: {key}"
    # Non-default `height_scale` is preserved as-is (auto-fit only kicks
    # in when caller passes default 1.0).
    assert dj["modelScale"] == 1.07
    assert dj["_buildVersion"] == BUILD_VERSION
    # gsQuaternion must be 180° Z-axis rotation (matching gs.js default).
    assert dj["gsQuaternion"] == [0.0, 0.0, 1.0, 0.0]
    # gsPosition format = [centroid.x, ground - floor_y, -centroid.z]
    assert isinstance(dj["gsPosition"], list) and len(dj["gsPosition"]) == 3
    # cleanSplats drops splats outside the person cylinder, so the output
    # splat count is ≤ N. Just check the three index arrays agree.
    n_out = len(dj["splatBoneIndices"])
    assert 0 < n_out <= n, f"expected 0 < n_out <= {n}, got {n_out}"
    assert parse_ply_centers(aligned_ply).shape[0] == n_out
    assert dj["_sourceSplatCount"] == n
    assert dj["_croppedSplatCount"] == n_out
    assert len(dj["splatVertexIndices"]) == n_out
    assert len(dj["splatRelativePoses"]) == n_out * 3
    # All bone indices must reference real bones in the skeleton.
    n_bones = len(skel.bones)
    assert all(0 <= i < n_bones for i in dj["splatBoneIndices"])
    n_verts = skel.posed_vertices.shape[0]
    assert all(0 <= i < n_verts for i in dj["splatVertexIndices"])


def test_preprocess_gvrm_empty_ply_raises(vrm_bytes):
    empty = _make_ply(np.zeros((0, 3), dtype=np.float32))
    with pytest.raises(ValueError, match="empty"):
        preprocess_gvrm(empty, vrm_bytes)


def test_preprocess_gvrm_invalid_vrm_raises(vrm_bytes, skel):
    splats = skel.posed_vertices[:5]
    ply_bytes = _make_ply(splats)
    with pytest.raises(Exception):
        preprocess_gvrm(ply_bytes, b"not a glb")


def test_parse_ply_centers_returns_float32(skel):
    splats = skel.posed_vertices[:10]
    ply_bytes = _make_ply(splats)
    centers = parse_ply_centers(ply_bytes)
    assert centers.shape == (10, 3)
    assert centers.dtype == np.float32


def test_filter_ply_bytes_preserves_binary_vertex_records():
    dtype = [
        ("x", "f4"), ("y", "f4"), ("z", "f4"),
        ("opacity", "f4"), ("scale_0", "f4"), ("rot_0", "f4"),
    ]
    vert = np.zeros(5, dtype=dtype)
    for i in range(5):
        vert[i] = (i, i + 0.1, i + 0.2, i + 0.3, i + 0.4, i + 0.5)
    buf = io.BytesIO()
    PlyData([PlyElement.describe(vert, "vertex")], text=False).write(buf)
    original = buf.getvalue()

    keep = np.array([True, False, True, False, True])
    filtered = _filter_ply_bytes(original, keep)
    ply = PlyData.read(io.BytesIO(filtered))
    out = ply["vertex"].data

    assert len(out) == 3
    np.testing.assert_array_equal(out["x"], np.array([0, 2, 4], dtype=np.float32))
    np.testing.assert_array_equal(out["opacity"], np.array([0.3, 2.3, 4.3], dtype=np.float32))
