"""Server-side Gaussian Splat → VRM skeleton binding.

Faithful Python port of the binding algorithm from naruya/gaussian-vrm
(`apps/preprocess/preprocess.js` + `gvrm-format/utils.js`, MIT). Produces a
fully-baked `data.json` payload that both Unity (aras-p + custom HLSL
skinning) and the dashboard preview (vendored gvrm.js) consume.

Algorithm overview (3 stages, identical to naruya's reference):

1. **assign_splats_to_bones** — for each splat, find the bone whose capsule
   proxy has the closest triangle to the splat center. Capsule per bone is
   sized via `BONE_CONFIG` and oriented along the parent→child segment.
2. **assign_splats_to_points** — for each VRM mesh vertex (skinned to
   T-pose), find the closest capsule's bone and bin the vertex under it.
   Then for each splat, find the nearest binned vertex within its assigned
   bone and record the offset (`splatRelativePoses`).
3. **bake data.json** — pack the per-splat bone/vertex/offset arrays plus
   `modelScale` (driven by `AvatarConfig.avatar_height_scale`) into the
   schema naruya's runtime expects.

Coordinate frame note: the binding is computed in glTF mesh-local frame.
The runtime applies VRM 0.x's 180° Y flip via `meshMatrixWorld` (driven
by `VRMUtils.rotateVRM0`), and the shader's `meshMatrixWorld * (transformed
+ relativePos)` rotates anchor and offset *together* — so `relativePose`
must stay in the same frame as `transformed` (glTF mesh-local). The PLY
scan must therefore be supplied in a glTF-compatible orientation
(person facing +Z works for Scaniverse scans of someone facing the
camera). Pose-detection-based auto-rotation (à la naruya's MediaPipe
pipeline) is NOT implemented here; manual rotation override on the
client is the recommended workaround.
"""

from __future__ import annotations

import io
import logging
import struct
from dataclasses import dataclass

import numpy as np
from scipy.spatial import cKDTree

logger = logging.getLogger(__name__)


# Server-side preprocess algorithm version. Stamped into every produced
# `.gvrm`'s `data.json._buildVersion`, and surfaced via the avatar config
# response as `expected_build_version` so the dashboard can flag stale
# .gvrm assets and recommend a rebuild.
#
# Bump this constant whenever the preprocess output format or the binding
# algorithm changes in a way that breaks compatibility with already-built
# .gvrm files. History:
#   server-v1: initial Python port (concat-all-primitives, trimmed bones)
#   server-v2: pick largest single primitive only, emit
#              _serverMeshVertexCount + _serverBoneNames so runtime can
#              align its SkinnedMesh + bone order to ours
#   server-v3: drop the spurious server-side VRM 0.x 180° Y flip on
#              posed_vertices/bones. The runtime shader already applies
#              that flip via meshMatrixWorld; pre-flipping on the server
#              caused relative_pose vectors to mix two coordinate frames,
#              dropping splats on the opposite side of the body.
#   server-v4: faithful port of naruya/gaussian-vrm's cleanSplats
#              pipeline — iterative XZ-cylinder filter, calculateHeights
#              cliff detection, calculateCentroidFeet, auto-modelScale,
#              binding in mesh-local frame via inverse(vrm.scene) ·
#              gsScene · raw_splat. PLY is bundled untranslated; runtime
#              applies gsPosition + gsQuaternion. Replaces AABB-based
#              alignment which couldn't handle floor/wall noise or
#              off-center scans. Pose-detection-based rotation is still
#              not implemented (requires GPU rendering); user scans must
#              be in standard +Z-facing orientation or use manual slider.
#   server-v5: (a) PLY filtered to person cylinder so background floor /
#              wall splats stop bleeding into the rendered avatar (port of
#              naruya's `urls[0]` cleaned-PLY output). (b) Default A-pose
#              `boneOperations` baked into both server-side LBS and emitted
#              to data.json so runtime mirrors the pose. Without this,
#              Scaniverse scans (captured in A-pose) bind to T-pose mesh
#              vertices and arm splats float ~25 cm sideways. Arm angles
#              are hardcoded ±35° pending Task #58 (manual slider).
#   server-v6: dial back the default A-pose arm angles from ±35° to ±20°.
#              ±35° produced a "Y" pose (steeper than scan A-pose) because
#              of the axis-convention mismatch between raw glTF bones
#              (server) and three-vrm normalized humanoid (runtime).
#              ±20° empirically lands closer to natural A-pose for VRoid
#              VRMs. Manual per-session override (Task #58) is still the
#              proper long-term fix; this is the better default.
#   server-v7: FLIP the sign of upper-arm Z rotation. v5/v6 misread the
#              Y-shape complaint as "too steep down" — it was actually
#              "arms going UP" (Y = arms-raised, like the letter). The
#              correct convention in VRoid + three-vrm is leftUpperArm
#              z=+, rightUpperArm z=- to bring arms DOWN into A-pose.
#              Set to ±35° (umbrella shape — natural A-pose).
#   server-v8: (a) Per-scan arm-angle estimation from the 3D point
#              cloud — replaces the hardcoded ±35° with a value
#              derived from each scan's shoulder→wrist geometry.
#              Approximates naruya's MediaPipe pose-detection step
#              without needing GPU rendering. (b) detectShoes port —
#              drops floor noise via a 102×102 cm grid pass that
#              keeps cells with >1cm avg elevation and >5 keep-neighbors.
#              Both bring rendering meaningfully closer to naruya's
#              reference output.
#   server-v9: tighten detectShoes for Scaniverse phone scans. v8
#              ported naruya's algorithm verbatim, but they assume
#              the scan is pre-centered at origin via gsScene.position.
#              Our pipeline keeps the PLY in raw scan coords, so the
#              `sqrt(x²+z²) < 0.5m` filter could miss the floor under
#              feet entirely if the person was off-origin. Switch the
#              floor-zone filter to centroid-relative coords. Also
#              raise the floor band 5% → 10% body height so taller
#              floor noise (carpet, shadows) gets caught.
#   server-v10: drop the PLY filtering step entirely — bundle the
#               ORIGINAL PLY and bind ALL splats. v5-v9 ran the PLY
#               through plyfile (read → mask → write) which was
#               suspected to subtly degrade SH coefficients /
#               covariance precision (rendering looked "chunkier"
#               than naruya's reference despite identical viewer
#               settings). Going raw eliminates that variable;
#               cleanSplats still computes centroid/heights for the
#               binding alignment, just doesn't drop splats from
#               the rendered output. Background splats now render
#               in place — same as naruya's `gs0` scene approach.
#   server-v11: bring back person-only PLY cropping for browser preview
#               performance, but avoid the v5-v9 quality regression by
#               copying binary PLY vertex records verbatim instead of
#               round-tripping every property through plyfile. No splat
#               decimation is applied: face/head density is preserved.
BUILD_VERSION = "server-v12"


# Same five capsule groups + parameters as gvrm-format/utils.js BONE_CONFIG.
# Keeping the JS group/name structure unchanged so future updates can be
# diff-applied. The non-uniform `scale.x/scale.z` on torso/head causes those
# capsules to deform into elliptical shells — we reproduce that by applying
# the scale to the triangulated capsule mesh in world-space.
BONE_CONFIG: dict[str, dict] = {
    "arm": {
        "names": ["J_Bip_L_Hand", "J_Bip_L_LowerArm", "J_Bip_R_Hand", "J_Bip_R_LowerArm"],
        "radius": 0.06,
        "scale": (1.0, 1.0),  # (x, z) — capsule's Y is the spine axis
    },
    "leg": {
        "names": ["J_Bip_L_LowerLeg", "J_Bip_L_Foot", "J_Bip_R_LowerLeg", "J_Bip_R_Foot"],
        "radius": 0.08,
        "scale": (1.0, 1.0),
    },
    "torso": {
        "names": ["J_Bip_C_Neck", "J_Bip_C_Spine", "J_Bip_C_Chest", "J_Bip_C_UpperChest"],
        "radius": 0.03,
        "scale": (6.0, 4.0),
    },
    "headTop": {
        "names": ["J_Bip_C_HeadTop_End"],
        "radius": 0.06,
        "scale": (1.5, 2.0),
    },
    "head": {
        "names": ["J_Bip_C_Head"],
        "radius": 0.03,
        "scale": (2.0, 2.0),
    },
}


def _bone_config_for(name: str) -> dict | None:
    for group in BONE_CONFIG.values():
        if name in group["names"]:
            return group
    return None


# Default A-pose adjustments for the upper arms — Scaniverse scans of a
# person are typically captured in A-pose (arms angled down ~30-45° from
# horizontal) because that's the natural standing posture. Bundled VRoid
# VRMs ship in T-pose (arms horizontal). Without compensation, arm splats
# bind to T-pose vertices that sit ~20-30 cm away from where the scan
# actually has them — visually the splats float off the body sideways.
#
# Naruya's reference uses MediaPipe pose detection (preprocess.js:935-1080)
# to compute the EXACT shoulder→wrist angle from the splat scan, which
# gives sub-degree alignment. We don't have pose detection server-side, so
# we hardcode reasonable A-pose defaults; the dashboard manual-rotation
# slider (Task #58) will let users fine-tune per-scan when they don't
# match the default exactly.
#
# Convention: rotation values are degrees around the bone's local axis.
# `boneName` matches three-vrm's normalized humanoid name (lowercase,
# camel-cased). Runtime's `setPose` calls `humanoid.getNormalizedBoneNode`
# which resolves this to the right glTF node regardless of VRM 0.x/1.0.
DEFAULT_A_POSE_BONE_OPERATIONS: list[dict] = [
    # Indices 0-1 reserved for hips/spine adjustments naruya makes via
    # tilt detection. We leave them as no-ops since we don't detect tilt.
    {"boneName": "hips", "rotation": {"x": 0.0, "y": 0.0, "z": 0.0}},
    {"boneName": "spine", "rotation": {"x": 0.0, "y": 0.0, "z": 0.0}},
    # Index 2-3: upper arms. After empirical testing on VRoid VRMs:
    #   * z = -20°/+20° → arms go UP (Y-shape, raised above T-pose)
    #   * z = +35°/-35° → A-pose, arms angled DOWN ~35° from horizontal
    # The sign convention in three-vrm's normalized humanoid is:
    #   leftUpperArm:  positive Z = down
    #   rightUpperArm: negative Z = down
    # (Opposite of what intuition might suggest from raw glTF axes.)
    # `boneOperations[2/3]` slot is reserved for these in naruya's
    # default.json; pose detection overwrites them at runtime in
    # naruya's pipeline. Without pose detection we lock in a sensible
    # default close to typical Scaniverse A-pose captures. Manual
    # per-session override is Task #58.
    {"boneName": "leftUpperArm", "rotation": {"x": 0.0, "y": 0.0, "z": 35.0}},
    {"boneName": "rightUpperArm", "rotation": {"x": 0.0, "y": 0.0, "z": -35.0}},
    # Indices 4-5: legs — typical A-pose stance is feet shoulder-width.
    # Default to no rotation since most scans have feet roughly together.
    {"boneName": "leftUpperLeg", "rotation": {"x": 0.0, "y": 0.0, "z": 0.0}},
    {"boneName": "rightUpperLeg", "rotation": {"x": 0.0, "y": 0.0, "z": 0.0}},
]


def _rotation_z_matrix(angle_deg: float) -> np.ndarray:
    """4×4 rotation matrix around Z axis by `angle_deg` degrees."""
    a = np.radians(angle_deg)
    c, s = float(np.cos(a)), float(np.sin(a))
    return np.array([
        [c, -s, 0.0, 0.0],
        [s,  c, 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ], dtype=np.float64)


def _rotation_x_matrix(angle_deg: float) -> np.ndarray:
    a = np.radians(angle_deg)
    c, s = float(np.cos(a)), float(np.sin(a))
    return np.array([
        [1.0, 0.0, 0.0, 0.0],
        [0.0,   c,  -s, 0.0],
        [0.0,   s,   c, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ], dtype=np.float64)


def _rotation_y_matrix(angle_deg: float) -> np.ndarray:
    a = np.radians(angle_deg)
    c, s = float(np.cos(a)), float(np.sin(a))
    return np.array([
        [c,  0.0,  s,  0.0],
        [0.0, 1.0, 0.0, 0.0],
        [-s,  0.0,  c,  0.0],
        [0.0, 0.0, 0.0, 1.0],
    ], dtype=np.float64)


# VRoid Studio bone-name mapping from three-vrm normalized humanoid names
# to the glTF node names we see in the rig. These are stable across VRoid
# Studio outputs (and madjin/vrm-samples). For non-VRoid VRMs we fall
# back to the normalized name and skip the operation if no match.
_VROID_HUMANOID_TO_GLTF: dict[str, str] = {
    "hips": "J_Bip_C_Hips",
    "spine": "J_Bip_C_Spine",
    "chest": "J_Bip_C_Chest",
    "upperChest": "J_Bip_C_UpperChest",
    "neck": "J_Bip_C_Neck",
    "head": "J_Bip_C_Head",
    "leftShoulder": "J_Bip_L_Shoulder",
    "leftUpperArm": "J_Bip_L_UpperArm",
    "leftLowerArm": "J_Bip_L_LowerArm",
    "leftHand": "J_Bip_L_Hand",
    "rightShoulder": "J_Bip_R_Shoulder",
    "rightUpperArm": "J_Bip_R_UpperArm",
    "rightLowerArm": "J_Bip_R_LowerArm",
    "rightHand": "J_Bip_R_Hand",
    "leftUpperLeg": "J_Bip_L_UpperLeg",
    "leftLowerLeg": "J_Bip_L_LowerLeg",
    "leftFoot": "J_Bip_L_Foot",
    "rightUpperLeg": "J_Bip_R_UpperLeg",
    "rightLowerLeg": "J_Bip_R_LowerLeg",
    "rightFoot": "J_Bip_R_Foot",
}


def _apply_bone_operations(
    bones: list,  # list[_Bone] — forward reference, _Bone defined below
    bone_index_by_name: dict[str, int],
    operations: list[dict],
) -> None:
    """Mutate `bones[i].local_matrix` and `bones[i].world_matrix` so the
    skeleton ends up in the pose described by `operations`. Mirrors what
    runtime's `setPose` does via `vrm.humanoid.getNormalizedBoneNode(...)
    .rotation`. Server-side application is necessary so `posed_vertices`
    (computed downstream via LBS) reflect the A-pose, otherwise the
    binding lands on T-pose vertex positions while the splat scan is in
    A-pose — arms misalign by 20-30 cm.

    For each op:
    1. Resolve `boneName` → glTF node name via `_VROID_HUMANOID_TO_GLTF`.
       Skip if not in the rig (allows partial-rig safety).
    2. Multiply the bone's `local_matrix` by R(x) · R(y) · R(z) (degrees).
    3. Recompute `world_matrix` for this bone AND every descendant bone
       (children of children, etc.) via forward kinematics.

    `bones` MUST be in topological order (parents before children) — our
    `parse_vrm_skeleton` produces them that way (skin.joints order).
    """
    # Build descendant list once for efficiency.
    children_of: dict[int, list[int]] = {i: [] for i in range(len(bones))}
    for i, b in enumerate(bones):
        if b.parent_index >= 0:
            children_of[b.parent_index].append(i)

    def _collect_descendants(idx: int) -> list[int]:
        out: list[int] = []
        stack = [idx]
        while stack:
            cur = stack.pop()
            for c in children_of[cur]:
                out.append(c)
                stack.append(c)
        return out

    for op in operations:
        bone_name = op.get("boneName")
        if not bone_name:
            continue
        gltf_name = _VROID_HUMANOID_TO_GLTF.get(bone_name, bone_name)
        if gltf_name not in bone_index_by_name:
            logger.debug(
                "skipping boneOperation for %r — bone not in rig", bone_name,
            )
            continue
        bone_idx = bone_index_by_name[gltf_name]
        bone = bones[bone_idx]

        rot = op.get("rotation") or {}
        rx = float(rot.get("x", 0.0))
        ry = float(rot.get("y", 0.0))
        rz = float(rot.get("z", 0.0))
        if rx == 0.0 and ry == 0.0 and rz == 0.0:
            continue

        # Three-vrm applies normalized-bone rotation as Euler XYZ. We
        # right-multiply the local matrix so the rotation occurs in the
        # bone's own frame, matching runtime semantics.
        rot_local = _rotation_x_matrix(rx) @ _rotation_y_matrix(ry) @ _rotation_z_matrix(rz)
        bone.local_matrix = (bone.local_matrix @ rot_local).astype(bone.local_matrix.dtype)

        # Recompute world for this bone + every descendant.
        if bone.parent_index >= 0:
            parent_world = bones[bone.parent_index].world_matrix
            bone.world_matrix = (parent_world @ bone.local_matrix).astype(bone.world_matrix.dtype)
        else:
            bone.world_matrix = bone.local_matrix.astype(bone.world_matrix.dtype)
        for desc_idx in _collect_descendants(bone_idx):
            d = bones[desc_idx]
            d.world_matrix = (
                bones[d.parent_index].world_matrix @ d.local_matrix
            ).astype(d.world_matrix.dtype)

        logger.info(
            "applied boneOperation %r (z=%.1f°): bone[%d] (%s) world pos = (%.3f, %.3f, %.3f)",
            bone_name, rz, bone_idx, gltf_name,
            bone.world_matrix[0, 3], bone.world_matrix[1, 3], bone.world_matrix[2, 3],
        )


# VRM 0.x → renderer-frame rotation (180° around Y). Applied as a
# right-multiply on world-space coordinates: P_world_after = R · P_world_before.
_VRM0_FLIP_Y = np.array(
    [[-1, 0, 0], [0, 1, 0], [0, 0, -1]],
    dtype=np.float64,
)


# ============================================================================
# PLY parsing + alignment
# ============================================================================


def parse_ply_centers(ply_bytes: bytes) -> np.ndarray:
    """Read x, y, z columns from a Gaussian Splat PLY (binary or ASCII).

    Returns (N, 3) float32 array of splat centers in PLY coordinate space.
    Other columns (SH coefficients, opacity, scale, rotation) are ignored —
    we only need centers for bone binding; the bytes themselves are kept
    intact downstream when the .gvrm zip is built.
    """
    from plyfile import PlyData

    plydata = PlyData.read(io.BytesIO(ply_bytes))
    vert = plydata["vertex"].data
    centers = np.column_stack([vert["x"], vert["y"], vert["z"]]).astype(np.float32)
    return centers


def _splat_alignment_translation(splat_centers: np.ndarray) -> np.ndarray:
    """[Legacy server-v3 alignment] Returns AABB-based translation.

    Replaced by `_clean_splats` for server-v4+. Kept for backward
    compatibility with tests that exercise the old alignment path.
    """
    bbox_min = splat_centers.min(axis=0)
    bbox_max = splat_centers.max(axis=0)
    xz_center = (bbox_min + bbox_max) / 2.0
    return np.array(
        [-float(xz_center[0]), -float(bbox_min[1]), -float(xz_center[2])],
        dtype=np.float32,
    )


# ============================================================================
# cleanSplats — Python port of naruya/gaussian-vrm preprocess.js
# (cleanSplats / calculateHeights / calculateCentroidFeet / calculateCentroidHead).
# Convention matches naruya: "height up" is `-vertex.y` because the runtime
# applies a 180° Z-axis rotation (gsQuaternion default = [0, 0, 1, 0]) which
# flips Y, so the PLY's negative-Y is rendered as up.
# ============================================================================


def _calculate_heights(
    centers: np.ndarray,
    centroid_xz: tuple[float, float],
    dist_xz: float,
    *,
    thresh: float = 1.0,
    dist_y: float = 2.0,
    knee_height_hint: float | None = None,
    n_neighborhood: int = 5,
) -> tuple[float, float, float]:
    """Find floor and ceiling Y for splats inside the XZ cylinder around
    `centroid_xz`. Faithful port of naruya's `calculateHeights`.

    Algorithm:
    1. Filter to cylinder (radius `dist_xz * thresh`, |y| < `dist_y`).
    2. Bin `-vertex.y` to 1 cm bins.
    3. Floor = bin with maximum (sum of N bins above) − (sum of N bins below).
    4. Ceiling = first bin above (floor + 0.3m) with frequency below
       0.025% of total (tracks the gap above the head).
    5. Validate: ≥10000 points, height diff > 0.3m, low outer-ring ratio.
    6. If validation fails, expand `dist_xz` by 0.1 (cap 3.0m) and retry.

    Returns `(floor_y, ceiling_y, final_dist_xz)` — both heights in
    `-y` space (positive = up in render).
    """
    cx, cz = centroid_xz
    N = n_neighborhood

    while dist_xz < 3.0:
        d = np.sqrt((centers[:, 0] - cx) ** 2 + (centers[:, 2] - cz) ** 2)
        mask = (np.abs(centers[:, 1]) < dist_y) & (d < dist_xz * thresh)
        radius_filtered = centers[mask]

        if len(radius_filtered) == 0:
            dist_xz += 0.1
            continue

        y_coords = np.round(-radius_filtered[:, 1] * 100).astype(np.int64)
        if len(y_coords) == 0:
            dist_xz += 0.1
            continue

        min_y = int(y_coords.min()) - N
        max_y = int(y_coords.max()) + N
        bin_edges = np.arange(min_y, max_y + 2)
        counts, _ = np.histogram(y_coords, bins=bin_edges)
        sorted_y = bin_edges[:-1]  # bin lower edges (in cm units, -y space)
        sorted_freq = counts.astype(np.int64)

        max_diff = -np.inf
        floor_y = min_y + N
        for i in range(N, len(sorted_y) - N + 1):
            current_y = sorted_y[i]
            if knee_height_hint is not None and current_y / 100.0 > knee_height_hint:
                continue
            lower_sum = int(sorted_freq[i - N:i].sum())
            upper_sum = int(sorted_freq[i:i + N].sum())
            diff = upper_sum - lower_sum
            if diff > max_diff:
                max_diff = diff
                # Naruya picks `sortedYCoords[i + 1][0]` — the bin just above
                # the cliff, which is the first occupied row of the body.
                idx = min(i + 1, len(sorted_y) - 1)
                floor_y = int(sorted_y[idx])

        if max_diff == -np.inf:
            raise ValueError("calculate_heights: max_diff is -inf (insufficient bins)")

        # Ceiling = first bin above (floor + 30 cm) with very low frequency.
        empty_y = max_y - N
        thresh_freq = len(radius_filtered) * 0.00025
        for i, y in enumerate(sorted_y):
            if y / 100.0 > floor_y / 100.0 + 0.3 and sorted_freq[i] < thresh_freq:
                empty_y = int(y)
                break

        floor_yf = floor_y / 100.0
        ceil_yf = empty_y / 100.0
        height_diff = ceil_yf - floor_yf

        # Validation step (naruya line 316-355).
        ymin_thresh = (ceil_yf - floor_yf) * 0.05 + floor_yf
        all_neg_y = -centers[:, 1]
        all_d = np.sqrt((centers[:, 0] - cx) ** 2 + (centers[:, 2] - cz) ** 2)
        test_mask = (all_d < dist_xz) & (ymin_thresh < all_neg_y) & (all_neg_y < ceil_yf)
        test_count = int(test_mask.sum())

        outer_ring_width = 0.02
        ring_d = all_d[test_mask]
        outer_ring_mask = (ring_d > (dist_xz - outer_ring_width)) & (ring_d <= dist_xz)
        outer_ring_count = int(outer_ring_mask.sum())
        outer_ring_ratio = outer_ring_count / max(test_count, 1)

        logger.debug(
            "calculate_heights: floor=%.2f ceil=%.2f centroid=(%.2f,%.2f) "
            "test_count=%d height_diff=%.2f outer_ring=%d/%.4f dist_xz=%.2f",
            floor_yf, ceil_yf, cx, cz, test_count, height_diff,
            outer_ring_count, outer_ring_ratio, dist_xz,
        )

        if (
            test_count < 10000
            or height_diff <= 0.3
            or (outer_ring_count > 10 and outer_ring_ratio > 0.00025)
        ):
            dist_xz += 0.1
            continue

        return floor_yf, ceil_yf, dist_xz

    raise ValueError(
        f"calculate_heights: could not find target after expanding to "
        f"dist_xz={dist_xz:.2f}m. Splat scan may be too sparse, too tall, "
        "or contain too much background noise. Trim background in Scaniverse "
        "and re-upload."
    )


def _calculate_centroid_feet(
    centers: np.ndarray,
    heights: tuple[float, float],
    centroid_xz_in: tuple[float, float],
    dist_xz: float,
) -> tuple[float, float]:
    """XZ centroid of splats in the lower 10–20% height band of the cylinder.
    Ports naruya's `calculateCentroidFeet`. The lower band is robust to
    arms/clothes that broaden the AABB at chest level.
    """
    floor_y, ceil_y = heights
    h = ceil_y - floor_y
    ymin = h * 0.1 + floor_y
    ymax = h * 0.2 + floor_y
    cx, cz = centroid_xz_in

    d = np.sqrt((centers[:, 0] - cx) ** 2 + (centers[:, 2] - cz) ** 2)
    neg_y = -centers[:, 1]
    mask = (d < dist_xz) & (ymin < neg_y) & (neg_y < ymax)
    sub = centers[mask]
    if len(sub) == 0:
        raise ValueError(
            "calculate_centroid_feet: no points in [10%, 20%] height band — "
            "splat scan may be too thin or heights detection wrong."
        )
    return float(sub[:, 0].mean()), float(sub[:, 2].mean())


def _calculate_centroid_head(
    centers: np.ndarray,
    heights: tuple[float, float],
    centroid_xz_in: tuple[float, float],
    dist_xz: float,
) -> tuple[float, float]:
    """XZ centroid of splats in the top 90–100% height band. Ports naruya's
    `calculateCentroidHead`."""
    floor_y, ceil_y = heights
    h = ceil_y - floor_y
    ymin = h * 0.9 + floor_y
    ymax = h * 1.0 + floor_y
    cx, cz = centroid_xz_in

    d = np.sqrt((centers[:, 0] - cx) ** 2 + (centers[:, 2] - cz) ** 2)
    neg_y = -centers[:, 1]
    mask = (d < dist_xz) & (ymin < neg_y) & (neg_y < ymax)
    sub = centers[mask]
    if len(sub) == 0:
        raise ValueError(
            "calculate_centroid_head: no points in top 10% band"
        )
    return float(sub[:, 0].mean()), float(sub[:, 2].mean())


def _estimate_arm_angle_degrees(
    centers: np.ndarray,
    centroid_xz: tuple[float, float],
    heights: tuple[float, float],
) -> float | None:
    """Estimate the actual arm-drop angle from a 3D splat scan, replacing
    naruya's MediaPipe-based shoulder→wrist landmark measurement
    (preprocess.js:942-1007) which we can't run server-side.

    Approximation:
    1. Define shoulder height = floor + 0.85·(ceil-floor) (top of torso).
    2. Define wrist height = the lowest Y where extreme-X splats exist
       in the upper body (above floor + 0.5·height). This isolates the
       hand position regardless of foot stance.
    3. For each side (left = X < cx, right = X > cx):
       - shoulder_x = mean X of splats in the shoulder height band on
         that side
       - wrist_x  = extreme X (10th/90th percentile) at any height in
         the upper-body band
       - wrist_y  = -centers[:,1] at that wrist_x splat's height
       - arm_drop = atan2(shoulder_y - wrist_y, |wrist_x - shoulder_x|)
    4. Average left/right; return in DEGREES.

    Returns `None` if the scan doesn't have enough body data to estimate
    (caller falls back to the hardcoded default in DEFAULT_A_POSE_*).
    """
    cx, cz = centroid_xz
    floor_y, ceil_y = heights
    h = ceil_y - floor_y
    if h < 0.5:
        # Torso too short to estimate reliably.
        return None

    neg_y = -centers[:, 1]

    # Shoulder band: 80-90% body height (top of torso, just below neck).
    shoulder_low = floor_y + 0.80 * h
    shoulder_high = floor_y + 0.90 * h
    shoulder_band = (neg_y >= shoulder_low) & (neg_y <= shoulder_high)
    # Upper-body band for wrist search: 50-90% (waist to shoulder).
    upper_low = floor_y + 0.50 * h
    upper_high = floor_y + 0.90 * h
    upper_band = (neg_y >= upper_low) & (neg_y <= upper_high)

    if shoulder_band.sum() < 100 or upper_band.sum() < 100:
        return None

    estimates: list[float] = []
    for side_name, side_filter in (
        ("left", centers[:, 0] < cx),
        ("right", centers[:, 0] > cx),
    ):
        # Shoulder = the X-extreme (away from centroid) of the shoulder band
        # on this side. We use 90th-percentile distance from centroid to
        # avoid latching onto stray splats.
        sb = centers[shoulder_band & side_filter]
        ub = centers[upper_band & side_filter]
        if len(sb) < 30 or len(ub) < 30:
            continue
        sx_dist = np.abs(sb[:, 0] - cx)
        shoulder_x_dist = float(np.percentile(sx_dist, 90))
        shoulder_x = cx - shoulder_x_dist if side_name == "left" else cx + shoulder_x_dist
        shoulder_y_neg = float(np.median(-sb[:, 1]))

        # Wrist = the X-extreme of the upper band. For a person in A-pose
        # this is the hand. For T-pose it's also at extreme X but at the
        # same height as the shoulder. The angle distinguishes them.
        ux_dist = np.abs(ub[:, 0] - cx)
        # Take the 95th-percentile-distance splats: those are the hand cluster.
        wrist_thresh = float(np.percentile(ux_dist, 95))
        wrist_pts = ub[ux_dist >= wrist_thresh]
        if len(wrist_pts) == 0:
            continue
        wrist_x = float(wrist_pts[:, 0].mean())
        wrist_y_neg = float(np.median(-wrist_pts[:, 1]))

        dx = abs(wrist_x - shoulder_x)
        dy = shoulder_y_neg - wrist_y_neg  # positive when wrist below shoulder
        if dx < 0.05:
            # Arm folded against torso — angle indeterminate.
            continue
        arm_drop_rad = float(np.arctan2(max(dy, 0.0), dx))
        arm_drop_deg = np.degrees(arm_drop_rad)
        logger.debug(
            "estimate_arm_angle %s: shoulder=(%.2f, y=%.2f) wrist=(%.2f, y=%.2f) "
            "dx=%.2f dy=%.2f angle=%.1f°",
            side_name, shoulder_x, shoulder_y_neg, wrist_x, wrist_y_neg,
            dx, dy, arm_drop_deg,
        )
        estimates.append(arm_drop_deg)

    if not estimates:
        return None
    avg = float(np.mean(estimates))
    # Clamp to a sane range. T-pose ≈ 0°, A-pose ≈ 25-45°, near-vertical 80°+.
    avg = max(0.0, min(60.0, avg))
    logger.info(
        "estimated arm-drop angle = %.1f° (sides: %s)",
        avg, [round(e, 1) for e in estimates],
    )
    return avg


def _detect_shoes(
    centers: np.ndarray,
    heights: tuple[float, float],
    centroid_xz: tuple[float, float],
) -> np.ndarray:
    """Port of naruya's `detectShoes` (preprocess.js:428-513). Returns a
    boolean mask: True = keep, False = drop (floor noise).

    Algorithm:
    1. Look at the bottom 5% height band of the scan (floor zone).
    2. Bin XZ to a 102×102 cm grid (1 cm cells).
    3. For each cell, compute:
       - count = splats in the cell
       - mean = average height above floor (in -y space)
    4. Mark cells `keep=True` where count > avg-cell-count AND mean > 1 cm.
    5. Drop cells with 5+ neighbors marked drop (isolated → noise).
    6. Splats above the 5% band are unconditionally kept; splats in the
       floor zone are kept only if their cell survived.

    Naruya's rationale: the floor immediately below feet has low avg
    height per cell (almost flat) and gets dropped; shoes/feet have
    higher avg (rising volume) and get kept. Isolated specks are
    background noise and get pruned.
    """
    cx, cz = centroid_xz
    floor_y, ceil_y = heights
    height_diff = ceil_y - floor_y
    # Floor band raised 5% → 10%. naruya assumes a low-mass camera-relative
    # scan so 5% catches just the floor; Scaniverse phone scans often have
    # taller noise (carpet, shadow artifacts up to ~10cm). 10% body height
    # ≈ 16cm for a 1.6m subject — still safely below ankles.
    ymin = height_diff * 0.10 + floor_y

    # Step 1: filter to floor zone within 0.5m of FEET CENTROID (not origin).
    # naruya uses origin because their pipeline pre-shifts the scan via
    # gsScene.position; we don't shift the PLY, so the person can be far
    # from origin — origin-based filter would miss the actual floor under
    # the feet. Working in centroid-coordinates fixes that.
    neg_y = -centers[:, 1]
    floor_zone_mask = (
        (neg_y < ymin)
        & (np.sqrt((centers[:, 0] - cx) ** 2 + (centers[:, 2] - cz) ** 2) < 0.5)
    )
    floor_pts = centers[floor_zone_mask]

    if len(floor_pts) == 0:
        # No floor zone splats at all — nothing to drop.
        return np.ones(len(centers), dtype=bool)

    # Step 2: bin XZ (relative to centroid) to 102×102 grid (1 cm cells).
    GRID_HALF = 51
    GRID_DIM = GRID_HALF * 2 + 1  # 103 to be safe
    bin_x = np.round((floor_pts[:, 0] - cx) * 100).astype(np.int64)
    bin_z = np.round((floor_pts[:, 2] - cz) * 100).astype(np.int64)
    in_grid = (
        (bin_x >= -GRID_HALF) & (bin_x <= GRID_HALF)
        & (bin_z >= -GRID_HALF) & (bin_z <= GRID_HALF)
    )
    bin_x = bin_x[in_grid] + GRID_HALF
    bin_z = bin_z[in_grid] + GRID_HALF
    # Mean = (-y - floor_y) per cell
    height_above_floor = (-floor_pts[in_grid, 1]) - floor_y

    # Step 3: per-cell count + sum.
    cell_count = np.zeros((GRID_DIM, GRID_DIM), dtype=np.int64)
    cell_sum = np.zeros((GRID_DIM, GRID_DIM), dtype=np.float64)
    np.add.at(cell_count, (bin_x, bin_z), 1)
    np.add.at(cell_sum, (bin_x, bin_z), height_above_floor)
    cell_mean = np.where(cell_count > 0, cell_sum / np.maximum(cell_count, 1), 0.0)

    # Step 4: keep cells with count > avg AND mean > 0.01m.
    mean_count = float(cell_count.mean())
    keep_grid = (cell_count > mean_count) & (cell_mean > 0.01)

    # Step 5: drop cells with 5+ "drop" neighbors (isolated keepers are noise).
    # Use a convolution-like 3×3 neighbor count of NOT-keep cells.
    pad_keep = np.pad(keep_grid.astype(np.int8), 1, mode="constant", constant_values=0)
    not_keep_neighbors = np.zeros_like(keep_grid, dtype=np.int64)
    for dx in (-1, 0, 1):
        for dz in (-1, 0, 1):
            if dx == 0 and dz == 0:
                continue
            shifted = pad_keep[1 + dx:1 + dx + GRID_DIM, 1 + dz:1 + dz + GRID_DIM]
            not_keep_neighbors += (shifted == 0).astype(np.int64)
    keep_grid &= (not_keep_neighbors < 5)

    # Step 6: build the per-splat mask.
    # Above-zone splats: always keep.
    # Below-zone splats: keep iff (within 0.5m of feet centroid) AND
    # (cell is keep). Splats below ymin AND outside 0.5m of feet are
    # dropped unconditionally — those are far-floor artifacts the scan
    # captured behind/beside the person.
    final_mask = neg_y >= ymin  # above-floor zone
    radial_to_feet = np.sqrt((centers[:, 0] - cx) ** 2 + (centers[:, 2] - cz) ** 2)
    below_zone = (~final_mask) & (radial_to_feet < 0.5)
    if below_zone.any():
        bx = np.round((centers[below_zone, 0] - cx) * 100).astype(np.int64)
        bz = np.round((centers[below_zone, 2] - cz) * 100).astype(np.int64)
        in_grid_b = (bx >= -GRID_HALF) & (bx <= GRID_HALF) & (bz >= -GRID_HALF) & (bz <= GRID_HALF)
        bx = bx + GRID_HALF
        bz = bz + GRID_HALF
        # Default to drop; flip on if cell is keep.
        below_keep = np.zeros(below_zone.sum(), dtype=bool)
        valid_idx = np.where(in_grid_b)[0]
        below_keep[valid_idx] = keep_grid[bx[valid_idx], bz[valid_idx]]
        # Apply back to final_mask via index lookup.
        below_indices = np.where(below_zone)[0]
        final_mask[below_indices] = below_keep

    dropped = int((~final_mask).sum())
    logger.info(
        "detect_shoes: %d/%d splats dropped (%d cells in keep grid out of %d)",
        dropped, len(centers),
        int(keep_grid.sum()), GRID_DIM * GRID_DIM,
    )
    return final_mask


def _person_mask(
    centers: np.ndarray,
    centroid_xz: tuple[float, float],
    heights: tuple[float, float],
    dist_xz: float,
) -> np.ndarray:
    """Boolean mask selecting splats inside the person cylinder defined by
    cleanSplats: distance to centroid (XZ) ≤ dist_xz, height (in -y space)
    in [floor - 0.5, ceil + 0.5]. Mirrors preprocess.js:536-543 inclusion.
    """
    cx, cz = centroid_xz
    floor_y, ceil_y = heights
    d = np.sqrt((centers[:, 0] - cx) ** 2 + (centers[:, 2] - cz) ** 2)
    neg_y = -centers[:, 1]
    return (d <= dist_xz) & (neg_y >= floor_y - 0.5) & (neg_y <= ceil_y + 0.5)


def _filter_ply_bytes(ply_bytes: bytes, keep_mask: np.ndarray) -> bytes:
    """Write a new PLY containing only vertices where `keep_mask` is True.

    The hot path preserves Gaussian quality by copying fixed-size binary PLY
    vertex records byte-for-byte. This avoids reserializing SH coefficients,
    opacity, covariance scale, and rotation through numpy/plyfile, which made
    earlier cropped builds look rougher. ASCII or list-property PLYs fall back
    to plyfile because there is no fixed vertex stride to copy.
    """
    filtered = _filter_binary_ply_bytes_lossless(ply_bytes, keep_mask)
    if filtered is not None:
        return filtered
    logger.warning(
        "filter_ply_bytes: falling back to plyfile rewrite; binary fixed-stride "
        "path unavailable for this PLY"
    )
    return _filter_ply_bytes_via_plyfile(ply_bytes, keep_mask)


def _filter_ply_bytes_via_plyfile(ply_bytes: bytes, keep_mask: np.ndarray) -> bytes:
    """Fallback PLY filter for non-binary or variable-stride PLY files."""
    from plyfile import PlyData, PlyElement

    plydata = PlyData.read(io.BytesIO(ply_bytes))
    vert = plydata["vertex"].data
    if len(keep_mask) != len(vert):
        raise ValueError(
            f"PLY has {len(vert)} vertices but mask has {len(keep_mask)} — "
            "ordering mismatch."
        )
    kept = vert[keep_mask]
    new_element = PlyElement.describe(kept, "vertex")
    # Preserve all other elements (face, etc.) if any.
    other_elements = [el for el in plydata.elements if el.name != "vertex"]
    out = PlyData([new_element, *other_elements], text=plydata.text, byte_order=plydata.byte_order)
    buf = io.BytesIO()
    out.write(buf)
    return buf.getvalue()


_PLY_SCALAR_SIZES = {
    "char": 1,
    "uchar": 1,
    "int8": 1,
    "uint8": 1,
    "short": 2,
    "ushort": 2,
    "int16": 2,
    "uint16": 2,
    "int": 4,
    "uint": 4,
    "int32": 4,
    "uint32": 4,
    "float": 4,
    "float32": 4,
    "double": 8,
    "float64": 8,
}


def _filter_binary_ply_bytes_lossless(
    ply_bytes: bytes,
    keep_mask: np.ndarray,
) -> bytes | None:
    """Losslessly filter fixed-stride binary PLY vertex records.

    Returns None when the file is ASCII, has list properties on the vertex
    element, or otherwise cannot be safely subset by byte stride.
    """
    try:
        header_end = _find_ply_header_end(ply_bytes)
    except ValueError:
        return None

    header_text = ply_bytes[:header_end].decode("ascii", errors="strict")
    lines = header_text.splitlines()
    if len(lines) < 3 or lines[0].strip() != "ply":
        return None

    fmt = None
    vertex_count = None
    vertex_stride = 0
    in_vertex = False
    saw_vertex = False
    for line in lines:
        stripped = line.strip()
        if stripped.startswith("format "):
            parts = stripped.split()
            fmt = parts[1] if len(parts) >= 2 else None
            if fmt not in {"binary_little_endian", "binary_big_endian"}:
                return None
        elif stripped.startswith("element "):
            parts = stripped.split()
            if len(parts) < 3:
                return None
            in_vertex = parts[1] == "vertex"
            if in_vertex:
                saw_vertex = True
                vertex_count = int(parts[2])
                vertex_stride = 0
        elif in_vertex and stripped.startswith("property "):
            parts = stripped.split()
            if len(parts) < 3 or parts[1] == "list":
                return None
            size = _PLY_SCALAR_SIZES.get(parts[1])
            if size is None:
                return None
            vertex_stride += size
        elif stripped == "end_header":
            break

    if fmt is None or not saw_vertex or vertex_count is None or vertex_stride <= 0:
        return None
    if len(keep_mask) != vertex_count:
        raise ValueError(
            f"PLY has {vertex_count} vertices but mask has {len(keep_mask)} - "
            "ordering mismatch."
        )

    vertex_bytes_len = vertex_stride * vertex_count
    vertex_start = header_end
    vertex_end = vertex_start + vertex_bytes_len
    if len(ply_bytes) < vertex_end:
        return None

    keep_indices = np.flatnonzero(keep_mask)
    new_vertex_count = int(keep_indices.size)
    new_header_lines = []
    replaced_count = False
    for line in lines:
        stripped = line.strip()
        if stripped.startswith("element vertex "):
            new_header_lines.append(f"element vertex {new_vertex_count}")
            replaced_count = True
        else:
            new_header_lines.append(line)
    if not replaced_count:
        return None

    new_vertices = bytearray(new_vertex_count * vertex_stride)
    src = memoryview(ply_bytes)
    for out_i, src_i in enumerate(keep_indices):
        src0 = vertex_start + int(src_i) * vertex_stride
        dst0 = out_i * vertex_stride
        new_vertices[dst0:dst0 + vertex_stride] = src[src0:src0 + vertex_stride]

    new_header = ("\n".join(new_header_lines) + "\n").encode("ascii")
    tail = ply_bytes[vertex_end:]
    return new_header + bytes(new_vertices) + tail


def _find_ply_header_end(ply_bytes: bytes) -> int:
    for marker in (b"end_header\n", b"end_header\r\n"):
        idx = ply_bytes.find(marker)
        if idx >= 0:
            return idx + len(marker)
    raise ValueError("PLY header missing end_header")


def _clean_splats(centers: np.ndarray) -> tuple[np.ndarray, tuple[float, float], tuple[float, float], float, np.ndarray]:
    """Port of naruya's `cleanSplats` main routine (preprocess.js:528-573).

    Three iterations of (calculate_heights → calculate_centroid_feet) to
    converge on a tight feet-cluster centroid + reliable floor/ceiling
    detection. Then filter out points outside the person cylinder.
    Returns `(cleaned_centers, centroid_xz, heights, final_dist_xz)`.

    `detect_shoes` (grid-based floor noise filter) is NOT ported yet — it
    requires a 102×102 grid pass that's straightforward but not strictly
    necessary for first-pass alignment. Add later if floor noise dominates.
    """
    centroid: tuple[float, float] = (0.0, 0.0)

    # Iteration 1: search starts at 0.3m radius, bins person's full height.
    floor_y, ceil_y, dist_xz = _calculate_heights(centers, centroid, 0.3)
    centroid = _calculate_centroid_feet(centers, (floor_y, ceil_y), centroid, dist_xz)

    # Iteration 2: re-detect heights with refined centroid, restart at 0.3m.
    floor_y, ceil_y, dist_xz = _calculate_heights(centers, centroid, 0.3)
    centroid = _calculate_centroid_feet(centers, (floor_y, ceil_y), centroid, dist_xz)

    # Filter to person cylinder for iteration 3 (removes floor / wall noise).
    cx, cz = centroid
    d = np.sqrt((centers[:, 0] - cx) ** 2 + (centers[:, 2] - cz) ** 2)
    neg_y = -centers[:, 1]
    person_mask = (d <= dist_xz) & (neg_y >= floor_y - 0.5) & (neg_y <= ceil_y + 0.5)
    person_centers = centers[person_mask]
    if len(person_centers) < 1000:
        # Stick with the unfiltered set; the person cylinder is too narrow.
        person_centers = centers
        logger.warning(
            "clean_splats: person cylinder filter kept <1000 points "
            "(%d), keeping raw scan for iteration 3", int(person_mask.sum()),
        )

    centroid = _calculate_centroid_feet(person_centers, (floor_y, ceil_y), centroid, dist_xz)
    floor_y, ceil_y, dist_xz = _calculate_heights(person_centers, centroid, dist_xz, thresh=0.5)

    # Recompute the FINAL person mask against the original `centers` array
    # using the converged centroid + heights + dist_xz. The caller uses this
    # mask to filter the bundled PLY so the runtime renders only the person
    # (drops floor/wall splats outside the cylinder).
    final_mask = _person_mask(centers, centroid, (floor_y, ceil_y), dist_xz)
    final_centers = centers[final_mask]

    logger.info(
        "clean_splats: %d → %d splats (%.1f%% kept), centroid_xz=(%.2f,%.2f), "
        "floor_y=%.3f, ceil_y=%.3f, dist_xz=%.2f, height=%.2fm",
        len(centers), int(final_mask.sum()),
        100.0 * float(final_mask.sum()) / max(len(centers), 1),
        centroid[0], centroid[1], floor_y, ceil_y, dist_xz, ceil_y - floor_y,
    )
    return final_centers, centroid, (floor_y, ceil_y), dist_xz, final_mask


# ============================================================================
# Runtime transform builders (gsScene + vrm.scene matrices)
# ============================================================================


def _build_gs_scene_matrix(
    centroid_xz: tuple[float, float],
    floor_y: float,
    ground: float,
) -> np.ndarray:
    """Build the runtime gsScene's matrixWorld = T(gsPosition) · R(gsQuaternion).

    Mirrors what naruya's `cleanSplats` finalize block does at runtime
    (preprocess.js:786-790):
        gsScene.position.y = character.ground - heights.min
        gsScene.position.x += centroid.x
        gsScene.position.z -= centroid.z
        gsScene.quaternion = (0, 0, 1, 0)  // 180° Z, gs.js default
    """
    cx, cz = centroid_xz
    T = np.eye(4, dtype=np.float64)
    T[0, 3] = cx
    T[1, 3] = ground - floor_y
    T[2, 3] = -cz
    # 180° around Z: (x, y, z) → (-x, -y, z).
    R = np.eye(4, dtype=np.float64)
    R[0, 0] = -1
    R[1, 1] = -1
    return T @ R


def _build_vrm_scene_matrix(
    bbsize_y: float,
    height_scale: float,
    is_vrm_0x: bool,
) -> tuple[np.ndarray, float]:
    """Build the runtime vrm.scene's matrixWorld and return (matrix, ground).

    Mirrors three-vrm's load + `VRMUtils.rotateVRM0` path (vrm.js):
        ground = -bbsize.y * 0.5 * scale
        vrm.scene.position.y = ground
        vrm.scene.scale = scale
        vrm.scene.rotation.y = π   (only for VRM 0.x)
    """
    ground = -bbsize_y * 0.5 * height_scale

    T = np.eye(4, dtype=np.float64)
    T[1, 3] = ground

    R = np.eye(4, dtype=np.float64)
    if is_vrm_0x:
        # 180° around Y: (x, y, z) → (-x, y, -z)
        R[0, 0] = -1
        R[2, 2] = -1

    S = np.eye(4, dtype=np.float64) * float(height_scale)
    S[3, 3] = 1.0

    return (T @ R @ S, ground)


def translate_ply_bytes(ply_bytes: bytes, translation: np.ndarray) -> bytes:
    """Apply a uniform XYZ translation to every splat in a PLY file.

    The PLY is rewritten so subsequent consumers (Three.js GS3D, Unity
    aras-p) see splats in the aligned (VRM-matching) coordinate frame.
    Other vertex columns (SH coefficients, scale, rotation, opacity) are
    preserved verbatim.
    """
    from plyfile import PlyData

    plydata = PlyData.read(io.BytesIO(ply_bytes))
    vert = plydata["vertex"].data
    vert["x"] = (vert["x"].astype(np.float32) + translation[0]).astype(vert["x"].dtype)
    vert["y"] = (vert["y"].astype(np.float32) + translation[1]).astype(vert["y"].dtype)
    vert["z"] = (vert["z"].astype(np.float32) + translation[2]).astype(vert["z"].dtype)
    buf = io.BytesIO()
    plydata.write(buf)
    return buf.getvalue()


# ============================================================================
# GLB/VRM parsing — manually walk the GLB binary so we can decode skin data
# without depending on a heavy Three.js shim.
# ============================================================================


@dataclass
class _Bone:
    """A single bone after `removeUnnecessaryJoints`-equivalent filtering.
    Indices are local to the trimmed bone list (matches what naruya's
    `skinnedMesh.skeleton.bones` contains)."""

    name: str
    parent_index: int  # -1 for root
    local_matrix: np.ndarray  # (4, 4) bone's transform relative to parent (rest pose)
    inverse_bind_matrix: np.ndarray  # (4, 4) IBM from skin
    world_matrix: np.ndarray  # (4, 4) world transform in rest/T-pose


@dataclass
class Skeleton:
    """Parsed VRM body skin in T-pose, in the "post VRMUtils.rotateVRM0"
    coordinate frame (= what naruya's preprocess.js sees and what runtime
    consumers expect)."""

    bones: list[_Bone]
    bone_index_by_name: dict[str, int]
    # Posed body mesh (after LBS to rest pose, in rotated VRM frame).
    posed_vertices: np.ndarray  # (V, 3) float32
    # Original mesh-local vertex positions (pre-skinning) — needed when we
    # later recompute relative poses against `applyBoneTransform`.
    raw_vertices: np.ndarray  # (V, 3)


def parse_vrm_skeleton(
    vrm_bytes: bytes,
    bone_operations: list[dict] | None = None,
) -> Skeleton:
    """Parse a VRM (GLB) and return the body skin's bone hierarchy +
    skinned mesh vertices.

    `bone_operations` is naruya's data.json `boneOperations` schema. When
    provided, each rotation is applied to the matching bone via forward
    kinematics BEFORE LBS, so `posed_vertices` reflects the requested pose
    (e.g., A-pose for arm splats). Pass `None` (default) for raw T-pose.

    `removeUnnecessaryJoints` is applied: only bones referenced by JOINTS_0
    are kept, indices renumbered, matching Three-VRM's behavior."""

    gltf = _load_glb(vrm_bytes)
    bin_blob = gltf.binary_blob() if hasattr(gltf, "binary_blob") else gltf._glb_data

    # Pick the body skin: heuristic = the skin used by the largest mesh
    # (by primitive[0] vertex count). VRoid models have a body + a face mesh;
    # the body is the one we want for splat binding.
    body_mesh_idx, body_skin_idx = _pick_body_mesh_and_skin(gltf, bin_blob)
    body_mesh = gltf.meshes[body_mesh_idx]
    body_skin = gltf.skins[body_skin_idx]

    # Bone world matrices in rest pose (T-pose) — drive the GLB node
    # hierarchy and accumulate transforms.
    node_world = _compute_node_world_matrices(gltf)

    # Inverse bind matrices, indexed by the skin's joint position
    ibm_array = _read_accessor(gltf, body_skin.inverseBindMatrices, bin_blob)
    ibm_array = ibm_array.reshape(-1, 4, 4).transpose(0, 2, 1)  # GLB stores column-major

    # Joints array is a list of GLB node indices in the order JOINTS_0 expects
    joint_node_ids: list[int] = list(body_skin.joints)

    # Mesh vertex data — pick ONLY the largest skinned primitive. Three.js
    # GLTFLoader creates a separate SkinnedMesh per primitive (sharing the
    # skeleton). If we concatenated all primitives here, our
    # `splatVertexIndices` would reference vertex IDs that don't exist on
    # any single runtime SkinnedMesh, causing the shader's anchor lookup
    # to read zeros and produce NaN splat positions.
    skinned_prims = []
    for prim_idx, prim in enumerate(body_mesh.primitives):
        attrs = prim.attributes
        if attrs.POSITION is None or attrs.JOINTS_0 is None or attrs.WEIGHTS_0 is None:
            # Some primitives may be face-only without skinning; skip.
            continue
        positions = _read_accessor(gltf, attrs.POSITION, bin_blob).reshape(-1, 3)
        joints = _read_accessor(gltf, attrs.JOINTS_0, bin_blob).reshape(-1, 4)
        weights = _read_accessor(gltf, attrs.WEIGHTS_0, bin_blob).reshape(-1, 4)
        # Normalize weights (some models leave them un-normalized).
        wsum = weights.sum(axis=1, keepdims=True)
        wsum[wsum == 0] = 1.0
        weights = weights / wsum
        skinned_prims.append((prim_idx, positions, joints.astype(np.int32), weights.astype(np.float32)))

    if not skinned_prims:
        raise ValueError("VRM body mesh has no skinned primitives")

    # Largest primitive wins. Logged so server logs show which primitive is bound.
    largest = max(skinned_prims, key=lambda p: p[1].shape[0])
    chosen_prim_idx, raw_vertices, skin_joints, skin_weights = largest
    raw_vertices = raw_vertices.astype(np.float32)
    logger.info(
        "binding to body_mesh[%d].primitive[%d] (%d verts, of %d primitives total)",
        body_mesh_idx, chosen_prim_idx, raw_vertices.shape[0], len(body_mesh.primitives),
    )

    # `removeUnnecessaryJoints`: keep only joints actually referenced by any
    # vertex with non-zero weight. Renumber indices to be 0..K-1.
    used_joint_positions = np.unique(skin_joints[skin_weights > 0])
    used_joint_positions = used_joint_positions.astype(np.int32)

    old_to_new = -np.ones(len(joint_node_ids), dtype=np.int32)
    for new_idx, old_idx in enumerate(used_joint_positions):
        old_to_new[old_idx] = new_idx

    # Remap JOINTS_0 indices to the trimmed bone list. Vertices with weight 0
    # to a bone may still reference an unused index — clamp to 0 since their
    # weight is also 0 (no contribution).
    skin_joints_remapped = np.where(
        old_to_new[skin_joints] >= 0, old_to_new[skin_joints], 0
    ).astype(np.int32)

    # Build trimmed bone list. Parent indices are computed from the GLB
    # parent relationship in node space, then remapped to the trimmed list.
    parent_node_of: dict[int, int] = {}
    for node_id, node in enumerate(gltf.nodes):
        for child_id in node.children or []:
            parent_node_of[child_id] = node_id

    bones: list[_Bone] = []
    bone_index_by_name: dict[str, int] = {}
    for new_idx, old_idx in enumerate(used_joint_positions):
        node_id = joint_node_ids[old_idx]
        node = gltf.nodes[node_id]
        local_mat = _node_local_matrix(node)
        ibm = ibm_array[old_idx]
        # Parent in trimmed list. Ascend the GLB tree until we find an
        # ancestor that's also a kept joint (or hit root).
        parent_node = parent_node_of.get(node_id)
        parent_new = -1
        while parent_node is not None:
            if parent_node in joint_node_ids:
                parent_old = joint_node_ids.index(parent_node)
                if old_to_new[parent_old] >= 0:
                    parent_new = int(old_to_new[parent_old])
                    break
            parent_node = parent_node_of.get(parent_node)

        bone_world = node_world[node_id]

        bones.append(
            _Bone(
                name=node.name or f"node{node_id}",
                parent_index=parent_new,
                local_matrix=local_mat,
                inverse_bind_matrix=ibm,
                world_matrix=bone_world,
            )
        )
        bone_index_by_name[node.name or f"node{node_id}"] = new_idx

    # Apply bone operations BEFORE LBS so `posed_vertices` reflects the
    # requested pose. Without this, server-side binding lands on T-pose
    # vertex positions while a Scaniverse scan is typically in A-pose,
    # leaving arm splats 20-30 cm off the body.
    if bone_operations:
        _apply_bone_operations(bones, bone_index_by_name, bone_operations)

    # Compute posed world vertex positions via standard LBS:
    #   p_world = sum_i  w_i · (bone_world_i · ibm_i) · p_local
    # where bone_world_i is in scene world space (post any boneOperations).
    bone_world_dot_ibm = np.stack(
        [b.world_matrix @ b.inverse_bind_matrix for b in bones],
        axis=0,
    )  # (K, 4, 4)

    # Vectorized LBS with up to 4 influences per vertex.
    raw_h = np.concatenate(
        [raw_vertices, np.ones((raw_vertices.shape[0], 1), dtype=raw_vertices.dtype)],
        axis=1,
    )  # (V, 4)

    # For each vertex v and influence i (0..3):
    #   contrib_i = w_iv * (bone_world_dot_ibm[joint_iv] @ raw_h_v)
    # Sum the four contribs.
    posed = np.zeros((raw_vertices.shape[0], 3), dtype=np.float32)
    for influence in range(4):
        m = bone_world_dot_ibm[skin_joints_remapped[:, influence]]  # (V, 4, 4)
        transformed = np.einsum("vij,vj->vi", m, raw_h)[:, :3]
        posed += skin_weights[:, influence : influence + 1] * transformed

    # NOTE: we do NOT apply `VRMUtils.rotateVRM0`-equivalent flip server-side.
    # Earlier versions did, on the theory that runtime sees the rotated frame.
    # That was wrong: the runtime shader binds anchor + offset in glTF mesh-
    # local frame and then `meshMatrixWorld` (which DOES include the 180° Y
    # rotation for VRM 0.x) rotates anchor + offset TOGETHER to world. If we
    # pre-rotate `posed_vertices` here but leave the splat PLY in its native
    # scan frame, the resulting `relative_pose` is the difference between
    # vectors in different frames — meaningless. The runtime then double-
    # rotates the offset, dropping splats on the OPPOSITE side of the body.
    #
    # Keeping `posed_vertices` and `bones[i].world_matrix` in glTF native
    # frame means the splat scan must also be in glTF-native orientation
    # (a Scaniverse scan of a person facing +Z works since glTF/VRoid's
    # default is +Z-facing too). Manual rotation override can be added
    # later if user scans aren't oriented this way.

    # naruya's gvrm.js (runtime) synthesizes `J_Bip_C_HeadTop_End` if it isn't
    # in the rig — it lives at (0, 0.2, -0.05) in head-local space. We mirror
    # that here so the headTop capsule (which our `BONE_CONFIG` expects) gets
    # built. Skip if the rig already has it (some VRMs do).
    head_top_name = "J_Bip_C_HeadTop_End"
    head_name = "J_Bip_C_Head"
    if head_top_name not in bone_index_by_name and head_name in bone_index_by_name:
        head_idx = bone_index_by_name[head_name]
        head_world = bones[head_idx].world_matrix
        local_translate = np.eye(4, dtype=np.float32)
        local_translate[:3, 3] = [0.0, 0.2, -0.05]
        synthetic_world = (head_world @ local_translate).astype(np.float32)
        synthetic_local = local_translate.copy()  # parent-relative TRS = pure translate
        synthetic_ibm = np.linalg.inv(synthetic_world).astype(np.float32)
        synthetic_idx = len(bones)
        bones.append(
            _Bone(
                name=head_top_name,
                parent_index=head_idx,
                local_matrix=synthetic_local,
                inverse_bind_matrix=synthetic_ibm,
                world_matrix=synthetic_world,
            )
        )
        bone_index_by_name[head_top_name] = synthetic_idx
        logger.info(
            "synthesized %s at world (%.3f, %.3f, %.3f) — VRM rig didn't ship one",
            head_top_name,
            synthetic_world[0, 3], synthetic_world[1, 3], synthetic_world[2, 3],
        )

    return Skeleton(
        bones=bones,
        bone_index_by_name=bone_index_by_name,
        posed_vertices=posed,
        raw_vertices=raw_vertices,
    )


def _load_glb(vrm_bytes: bytes):
    """Load GLB bytes via pygltflib without going through the disk path
    (avoids the cp932 codec issue on Japanese Windows locales)."""
    import pygltflib
    import tempfile
    import os

    # pygltflib's parse_binary expects a file-like object. The simplest
    # robust path: write to a temp file and use load_binary.
    with tempfile.NamedTemporaryFile(suffix=".glb", delete=False) as f:
        f.write(vrm_bytes)
        tmp_path = f.name
    try:
        gltf = pygltflib.GLTF2().load_binary(tmp_path)
    finally:
        os.unlink(tmp_path)
    return gltf


def _is_vrm_0x(gltf) -> bool:
    """Detect VRM 0.x by checking for the legacy VRM extension at the root.
    VRM 1.0 uses VRMC_vrm and reports `specVersion: '1.0'`."""
    ext = gltf.extensions or {}
    if "VRM" in ext:
        return True
    if "VRMC_vrm" in ext:
        # 1.0 — no rotateVRM0 needed.
        return False
    # Fallback: assume 0.x for backward compat (matches naruya's default).
    return True


def _pick_body_mesh_and_skin(gltf, bin_blob) -> tuple[int, int]:
    """Pick the largest skinned mesh (by primitive[0] vertex count). VRoid
    models pair a body mesh + a face mesh; the body is the bigger one."""
    best_idx = -1
    best_count = -1
    best_skin = -1
    # Find which skin each mesh uses by walking nodes.
    mesh_to_skin: dict[int, int] = {}
    for node in gltf.nodes:
        if node.mesh is not None and node.skin is not None:
            mesh_to_skin[node.mesh] = node.skin

    for mesh_idx, mesh in enumerate(gltf.meshes):
        prim = mesh.primitives[0]
        if prim.attributes.POSITION is None:
            continue
        accessor = gltf.accessors[prim.attributes.POSITION]
        count = accessor.count
        if count > best_count and mesh_idx in mesh_to_skin:
            best_idx = mesh_idx
            best_count = count
            best_skin = mesh_to_skin[mesh_idx]

    if best_idx < 0:
        raise ValueError("No skinned mesh found in VRM")
    return best_idx, best_skin


def _node_local_matrix(node) -> np.ndarray:
    """4x4 local transform of a GLB node from its TRS or matrix fields."""
    if node.matrix is not None and len(node.matrix) == 16:
        # GLB stores column-major; numpy is row-major.
        return np.array(node.matrix, dtype=np.float32).reshape(4, 4).T
    t = np.array(node.translation or [0, 0, 0], dtype=np.float32)
    r = np.array(node.rotation or [0, 0, 0, 1], dtype=np.float32)  # x,y,z,w
    s = np.array(node.scale or [1, 1, 1], dtype=np.float32)
    return _trs_to_matrix(t, r, s)


def _trs_to_matrix(t: np.ndarray, r: np.ndarray, s: np.ndarray) -> np.ndarray:
    """Build a 4x4 from translation, quaternion (x,y,z,w), scale."""
    x, y, z, w = r
    xx, yy, zz = x * x, y * y, z * z
    xy, xz, yz = x * y, x * z, y * z
    wx, wy, wz = w * x, w * y, w * z
    rot = np.array(
        [
            [1 - 2 * (yy + zz), 2 * (xy - wz), 2 * (xz + wy)],
            [2 * (xy + wz), 1 - 2 * (xx + zz), 2 * (yz - wx)],
            [2 * (xz - wy), 2 * (yz + wx), 1 - 2 * (xx + yy)],
        ],
        dtype=np.float32,
    )
    m = np.eye(4, dtype=np.float32)
    m[:3, :3] = rot * s[None, :]  # column-wise scale
    m[:3, 3] = t
    return m


def _compute_node_world_matrices(gltf) -> dict[int, np.ndarray]:
    """Walk all scene roots in DFS order; emit world matrix per node."""
    out: dict[int, np.ndarray] = {}
    scene = gltf.scenes[gltf.scene or 0]

    def walk(node_id: int, parent_world: np.ndarray) -> None:
        node = gltf.nodes[node_id]
        local = _node_local_matrix(node)
        world = parent_world @ local
        out[node_id] = world
        for child_id in node.children or []:
            walk(child_id, world)

    eye = np.eye(4, dtype=np.float32)
    for root_id in scene.nodes:
        walk(root_id, eye)
    return out


def _read_accessor(gltf, accessor_idx: int, bin_blob: bytes) -> np.ndarray:
    """Read accessor data into a numpy array. Handles the common GLB
    accessor + bufferView indirection plus the relevant component types."""
    accessor = gltf.accessors[accessor_idx]
    bv = gltf.bufferViews[accessor.bufferView]
    offset = (bv.byteOffset or 0) + (accessor.byteOffset or 0)

    type_to_count = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}
    components = type_to_count[accessor.type]

    # GLTF component types (matches GL constants).
    ctype_map = {
        5120: ("int8", 1),
        5121: ("uint8", 1),
        5122: ("int16", 2),
        5123: ("uint16", 2),
        5125: ("uint32", 4),
        5126: ("float32", 4),
    }
    dtype_str, item_size = ctype_map[accessor.componentType]
    raw = np.frombuffer(
        bin_blob,
        dtype=np.dtype(dtype_str),
        count=accessor.count * components,
        offset=offset,
    )
    return raw.copy()


# ============================================================================
# Capsule generation — match Three.js CapsuleGeometry(radius, length, 1, 6)
# ============================================================================


@dataclass
class _Capsule:
    """A bone proxy capsule in world space.

    Two paths exist for distance queries:

    - **Uniform-scale fast path** (12-13 of 14 capsules in BONE_CONFIG): the
      capsule is a true revolved cylinder+spheres, so closest-point-to-capsule
      reduces to a closed-form `max(0, dist_to_axis_segment - effective_radius)`.
      Vectorizes across N splats in ~10 numpy ops total per capsule.
    - **Non-uniform scale fallback** (torso 6×4, headTop 1.5×2): the capsule
      becomes an elliptical surface so we keep the triangulated mesh and fall
      back to point-to-triangle distance.

    Stage 1 of preprocess is dominated by this calculation, so the fast path
    cuts wall time ~10-50× on real Scaniverse scans (~300k splats).
    """

    bone_index: int  # index into Skeleton.bones (the *child* bone of this segment)
    is_uniform: bool
    # Triangulated representation — always populated for compatibility, but
    # only consulted when `is_uniform=False`.
    triangles: np.ndarray  # (T, 3, 3) float32
    # Analytical representation (only valid when is_uniform=True).
    seg_a: np.ndarray | None = None        # (3,) capsule spine endpoint A (parent bone pos)
    seg_b: np.ndarray | None = None        # (3,) capsule spine endpoint B (child bone pos)
    eff_radius: float | None = None        # radius * uniform_scale


def _capsule_geometry(radius: float, length: float, cap_segs: int = 1, radial_segs: int = 6) -> tuple[np.ndarray, np.ndarray]:
    """Generate a tessellated capsule mesh in local space (Y axis = capsule
    spine, capsule centered at origin). Reproduces the shape Three.js's
    CapsuleGeometry(radius, length, capSegments=1, radialSegments=6) emits.

    Three.js builds a Path with two arcs and revolves it (LatheGeometry):
      - bottom hemisphere: arc from (radius, -length/2 - radius) sweeping up to (radius, -length/2)
      - cylinder body: line from (radius, -length/2) to (radius, length/2)
      - top hemisphere: arc from (radius, length/2) sweeping up to (0, length/2 + radius)

    With cap_segs=1 each arc is split into 1 segment (= 2 endpoints), so the
    lathe profile has roughly 4-5 unique points. Returns (vertices, indices).
    """
    # Build the 2D profile (x, y) — left half of the capsule's silhouette.
    # Match three.js Path.absarc semantics with `clockwise=false`.
    half = length / 2.0
    profile_xs: list[float] = []
    profile_ys: list[float] = []

    # Bottom arc: center (0, -half), from angle 1.5π to 0 (i.e., down→right).
    # cap_segs=1 → 2 endpoints.
    for i in range(cap_segs + 1):
        t = i / cap_segs
        ang = 1.5 * np.pi + t * (0 - 1.5 * np.pi)
        profile_xs.append(np.cos(ang) * radius)
        profile_ys.append(-half + np.sin(ang) * radius)

    # Top arc: center (0, half), from angle 0 to π/2.
    for i in range(cap_segs + 1):
        t = i / cap_segs
        ang = 0 + t * (0.5 * np.pi - 0)
        profile_xs.append(np.cos(ang) * radius)
        profile_ys.append(half + np.sin(ang) * radius)

    profile = np.column_stack([profile_xs, profile_ys]).astype(np.float64)

    # Lathe: revolve profile around Y axis with `radial_segs` slices.
    n_profile = profile.shape[0]
    angles = np.linspace(0, 2 * np.pi, radial_segs + 1, endpoint=True)
    cos_a = np.cos(angles)
    sin_a = np.sin(angles)

    # vertices grid: for each profile point i, for each angle j:
    #   (profile_x * cos_a, profile_y, profile_x * sin_a)  -- but Three.js
    # produces (cos_a * px, py, -sin_a * px). Sign of z doesn't change which
    # triangles cover the surface — we just need a consistent capsule mesh.
    verts = []
    for i in range(n_profile):
        px, py = profile[i]
        for j in range(radial_segs + 1):
            verts.append((cos_a[j] * px, py, sin_a[j] * px))
    vertices = np.asarray(verts, dtype=np.float32)

    # Indices: build quads then split into triangles. Three.js LatheGeometry
    # produces faces with consistent winding. Skip degenerate quads at the
    # poles (where profile_x == 0).
    indices: list[int] = []
    for i in range(n_profile - 1):
        for j in range(radial_segs):
            v0 = i * (radial_segs + 1) + j
            v1 = (i + 1) * (radial_segs + 1) + j
            v2 = (i + 1) * (radial_segs + 1) + (j + 1)
            v3 = i * (radial_segs + 1) + (j + 1)
            # Two triangles per quad; degenerate ones are auto-skipped by
            # area=0 and don't affect closest-point math.
            indices.extend([v0, v1, v3])
            indices.extend([v3, v1, v2])

    return vertices, np.asarray(indices, dtype=np.int32)


def _build_capsules(skel: Skeleton) -> list[_Capsule]:
    """Walk the skeleton; for every parent→child edge whose CHILD bone is in
    BONE_CONFIG, build a capsule with the configured radius/scale, oriented
    along the segment, positioned at midpoint. Same traversal/order as
    `getPointsMeshCapsules` in naruya's utils.js so capsule indices match
    what the runtime ends up shipping."""

    capsules: list[_Capsule] = []
    children_of: dict[int, list[int]] = {i: [] for i in range(len(skel.bones))}
    for i, b in enumerate(skel.bones):
        if b.parent_index >= 0:
            children_of[b.parent_index].append(i)

    # Roots = bones whose parent is not in the trimmed bone list (parent=-1).
    roots = [i for i, b in enumerate(skel.bones) if b.parent_index < 0]

    visited: set[int] = set()

    def walk(parent_idx: int) -> None:
        if parent_idx in visited:
            return
        visited.add(parent_idx)
        parent_pos = skel.bones[parent_idx].world_matrix[:3, 3]
        for child_idx in children_of[parent_idx]:
            child_bone = skel.bones[child_idx]
            cfg = _bone_config_for(child_bone.name)
            if cfg is not None:
                child_pos = child_bone.world_matrix[:3, 3]
                seg = child_pos - parent_pos
                seg_len = float(np.linalg.norm(seg))
                if seg_len > 1e-6:
                    # CapsuleGeometry(radius, distance - 2*radius, 1, 6).
                    # In rare cases distance < 2*radius the cylindrical body
                    # has negative length — Three.js still produces a valid
                    # mesh (mostly hemispheres). Clamp to 0 to keep our math
                    # numerically stable.
                    cyl_len = max(0.0, seg_len - 2 * cfg["radius"])
                    verts_local, idx = _capsule_geometry(cfg["radius"], cyl_len)
                    sx, sz = cfg["scale"]
                    verts_local[:, 0] *= sx
                    verts_local[:, 2] *= sz
                    direction = seg / seg_len
                    rot = _rotation_from_y_to(direction)
                    midpoint = (parent_pos + child_pos) * 0.5
                    verts_world = (rot @ verts_local.T).T + midpoint
                    triangles = verts_world[idx].reshape(-1, 3, 3).astype(np.float32)

                    # Uniform-scale fast path: when sx == sz the capsule
                    # stays a true revolved capsule, so distance reduces to
                    # closed-form on the parent→child segment with the
                    # scaled radius. Avoids 36-triangle iteration.
                    is_uniform = abs(sx - sz) < 1e-6
                    capsules.append(_Capsule(
                        bone_index=child_idx,
                        is_uniform=is_uniform,
                        triangles=triangles,
                        seg_a=parent_pos.astype(np.float32) if is_uniform else None,
                        seg_b=child_pos.astype(np.float32) if is_uniform else None,
                        eff_radius=float(cfg["radius"] * sx) if is_uniform else None,
                    ))
            walk(child_idx)

    for root in roots:
        walk(root)
    return capsules


def _rotation_from_y_to(direction: np.ndarray) -> np.ndarray:
    """3x3 rotation that maps the +Y axis to `direction` (unit vector).
    Matches THREE.Quaternion.setFromUnitVectors((0,1,0), direction)."""
    y = np.array([0.0, 1.0, 0.0], dtype=np.float64)
    d = direction.astype(np.float64)
    cos_t = float(np.dot(y, d))
    if cos_t > 1.0 - 1e-8:
        return np.eye(3, dtype=np.float32)
    if cos_t < -1.0 + 1e-8:
        # 180° rotation; pick X axis as the rotation axis.
        return np.array(
            [[1, 0, 0], [0, -1, 0], [0, 0, -1]],
            dtype=np.float32,
        )
    axis = np.cross(y, d)
    s = float(np.linalg.norm(axis))
    axis /= s
    sin_t = s
    # Rodrigues' formula
    K = np.array(
        [[0, -axis[2], axis[1]], [axis[2], 0, -axis[0]], [-axis[1], axis[0], 0]],
        dtype=np.float64,
    )
    R = np.eye(3) + sin_t * K + (1 - cos_t) * (K @ K)
    return R.astype(np.float32)


# ============================================================================
# Closest-point-to-triangle (vectorized for batches of points × triangles)
# ============================================================================


def _closest_point_to_triangles(points: np.ndarray, triangles: np.ndarray) -> np.ndarray:
    """For each (point, triangle) pair return the squared Euclidean distance
    between the point and the closest spot on that triangle.

    points: (P, 3) — query points
    triangles: (T, 3, 3) — T triangles, each (a, b, c) vertices

    Returns: (P, T) squared distances.

    Uses the standard barycentric closest-point algorithm from Real-Time
    Collision Detection (Ericson 2005, ch. 5.1.5). Fully vectorized so we
    can broadcast P × T pairs in one numpy call.
    """
    a = triangles[:, 0, :]  # (T, 3)
    b = triangles[:, 1, :]
    c = triangles[:, 2, :]
    ab = b - a
    ac = c - a
    # Pre-compute per-triangle dot products
    d00 = np.einsum("tj,tj->t", ab, ab)  # (T,)
    d01 = np.einsum("tj,tj->t", ab, ac)
    d11 = np.einsum("tj,tj->t", ac, ac)
    denom = d00 * d11 - d01 * d01  # (T,)

    # ap = points[:, None, :] - a[None, :, :]  → shape (P, T, 3) but memory
    # heavy. Instead, do it per-point in a loop over chunks. For Stage 1 we
    # call this at most ~(splat_count * 14) capsules but with batched splats.
    P = points.shape[0]
    T = triangles.shape[0]
    out = np.empty((P, T), dtype=np.float32)
    # The nested loop below allocates (chunk, T, 3) per iteration; small
    # enough to fit in cache.
    chunk = max(1, min(P, 4096))
    for start in range(0, P, chunk):
        end = min(P, start + chunk)
        ap = points[start:end, None, :] - a[None, :, :]  # (C, T, 3)
        d20 = np.einsum("ctj,tj->ct", ap, ab)  # (C, T)
        d21 = np.einsum("ctj,tj->ct", ap, ac)
        v = (d11 * d20 - d01 * d21) / np.where(denom != 0, denom, 1.0)
        w = (d00 * d21 - d01 * d20) / np.where(denom != 0, denom, 1.0)
        u = 1.0 - v - w

        # Clip barycentric coords to the triangle. The "outside" cases below
        # follow Ericson's region table (R1..R6). Easiest is to clamp to
        # nearest edge / vertex and recompute closest point analytically.
        outside_v = v < 0
        outside_w = w < 0
        outside_u = u < 0

        # Region: closest point on edge ab (w<0, project onto ab)
        t_ab = np.clip(d20 / np.where(d00 != 0, d00, 1.0), 0.0, 1.0)
        # edge ac (v<0)
        t_ac = np.clip(d21 / np.where(d11 != 0, d11, 1.0), 0.0, 1.0)
        # edge bc (u<0): parametrize p = b + s*(c-b)
        bc = c - b
        bp = points[start:end, None, :] - b[None, :, :]
        bc_dot = np.einsum("tj,tj->t", bc, bc)
        bp_dot_bc = np.einsum("ctj,tj->ct", bp, bc)
        t_bc = np.clip(bp_dot_bc / np.where(bc_dot != 0, bc_dot, 1.0), 0.0, 1.0)

        # Default: inside triangle → barycentric interpolation
        closest = (
            u[..., None] * a[None, :, :]
            + v[..., None] * b[None, :, :]
            + w[..., None] * c[None, :, :]
        )

        # Override for outside regions. Each region's closest point is on an
        # edge (or vertex). Treat them in priority — point first if two
        # coords are negative simultaneously.
        # Edge ab projection
        ab_proj = a[None, :, :] + t_ab[..., None] * ab[None, :, :]
        ac_proj = a[None, :, :] + t_ac[..., None] * ac[None, :, :]
        bc_proj = b[None, :, :] + t_bc[..., None] * bc[None, :, :]

        # Apply: edge ab when w<0 (and we'd otherwise be outside)
        mask_ab = outside_w & ~outside_v
        mask_ac = outside_v & ~outside_w
        mask_bc = outside_u & ~outside_v & ~outside_w
        # Corner cases where two coords are negative — pick the one with
        # larger magnitude, equivalent to projecting on either incident edge.
        # In practice the edge projections above already handle this within
        # numerical tolerance because t_** is clamped to [0,1].
        mask_corner = (outside_v & outside_w) | (outside_v & outside_u) | (outside_w & outside_u)
        # Corner v,w both negative → vertex a
        mask_va = outside_v & outside_w & ~outside_u
        # Corner v,u both negative → vertex c (or b? near c)
        mask_vc = outside_u & outside_v
        # Corner w,u both negative → vertex b
        mask_vb = outside_u & outside_w

        # Apply edge projections
        closest = np.where(mask_ab[..., None], ab_proj, closest)
        closest = np.where(mask_ac[..., None], ac_proj, closest)
        closest = np.where(mask_bc[..., None], bc_proj, closest)
        # Vertex fallbacks (override edge projections at corners)
        closest = np.where(mask_va[..., None], a[None, :, :], closest)
        closest = np.where(mask_vc[..., None], c[None, :, :], closest)
        closest = np.where(mask_vb[..., None], b[None, :, :], closest)

        diff = points[start:end, None, :] - closest
        out[start:end] = np.einsum("ctj,ctj->ct", diff, diff)

    return out


# ============================================================================
# Stage 1: per-splat → bone assignment
# ============================================================================


def _capsule_distance_squared(points: np.ndarray, capsule: _Capsule) -> np.ndarray:
    """Squared distance from each point to one capsule's surface.

    Uniform-scale fast path: closed-form against the parent→child segment
    minus the effective radius. Vectorized over all N points in ~6 numpy ops.
    Non-uniform fallback: triangle-mesh closest-point min.
    """
    if capsule.is_uniform:
        # Project each point onto the capsule's spine segment, clamped.
        a = capsule.seg_a
        b = capsule.seg_b
        ab = b - a
        ab_dot = float(np.dot(ab, ab))
        if ab_dot < 1e-12:
            # Degenerate segment — treat as point.
            diff = points - a
            d = np.linalg.norm(diff, axis=1) - capsule.eff_radius
        else:
            t = np.clip(((points - a) @ ab) / ab_dot, 0.0, 1.0)
            proj = a + t[:, None] * ab[None, :]
            diff = points - proj
            d = np.linalg.norm(diff, axis=1) - capsule.eff_radius
        d = np.maximum(d, 0.0)
        return (d * d).astype(np.float32)
    # Non-uniform: defer to triangle-mesh routine, take min over triangles.
    sq = _closest_point_to_triangles(points, capsule.triangles)
    return sq.min(axis=1).astype(np.float32)


def _assign_splats_to_bones(splat_centers: np.ndarray, capsules: list[_Capsule]) -> np.ndarray:
    """For each splat, return the bone index (in skeleton.bones) of the
    capsule with the closest surface point. Matches `assignSplatsToBones`
    (CPU) in naruya/preprocess.js but uses analytical capsule distance for
    the 12 uniform-scale capsules, falling back to triangle-mesh on the
    non-uniform torso/headTop pair only."""
    if not capsules:
        return np.zeros(splat_centers.shape[0], dtype=np.int32)

    per_capsule_min = np.empty((splat_centers.shape[0], len(capsules)), dtype=np.float32)
    for ci, cap in enumerate(capsules):
        per_capsule_min[:, ci] = _capsule_distance_squared(splat_centers, cap)
    best_capsule = per_capsule_min.argmin(axis=1)

    bone_index_per_splat = np.fromiter(
        (capsules[ci].bone_index for ci in best_capsule),
        dtype=np.int32,
        count=splat_centers.shape[0],
    )
    return bone_index_per_splat


# ============================================================================
# Stage 2: per-splat → vertex assignment + relative pose
# ============================================================================


def _bin_vertices_to_bones(skel: Skeleton, capsules: list[_Capsule]) -> dict[int, np.ndarray]:
    """For each VRM mesh vertex, find the closest capsule and bin the vertex
    under that capsule's bone. Output maps bone_index → array of vertex
    indices. Matches the first half of `assignSplatsToPoints`.

    Uses the same uniform-scale fast path as `_assign_splats_to_bones` —
    on a typical VRM body mesh (~14k vertices) this drops vertex binning
    from ~5s to <1s.
    """
    if not capsules:
        return {b.bone_index: np.empty(0, dtype=np.int32) for b in [skel.bones[0]]}

    per_capsule_min = np.empty(
        (skel.posed_vertices.shape[0], len(capsules)),
        dtype=np.float32,
    )
    for ci, cap in enumerate(capsules):
        per_capsule_min[:, ci] = _capsule_distance_squared(skel.posed_vertices, cap)
    best_capsule = per_capsule_min.argmin(axis=1)

    out: dict[int, list[int]] = {}
    for vi, ci in enumerate(best_capsule):
        bone_idx = capsules[ci].bone_index
        out.setdefault(bone_idx, []).append(vi)
    return {k: np.asarray(v, dtype=np.int32) for k, v in out.items()}


def _assign_splats_to_points(
    splat_centers: np.ndarray,
    splat_bone_indices: np.ndarray,
    skel: Skeleton,
    bone_vertex_indices: dict[int, np.ndarray],
) -> tuple[np.ndarray, np.ndarray]:
    """For each splat with bone B, find the nearest binned vertex (under B)
    in the posed mesh; return its index and the splat-relative position.
    Matches the second half + relative-pose loop of `assignSplatsToPoints`."""

    posed = skel.posed_vertices  # (V, 3)

    # Build a per-bone KDTree once for O(log V) nearest-neighbor lookup.
    trees_per_bone: dict[int, tuple[cKDTree, np.ndarray]] = {}
    for bone_idx, vert_indices in bone_vertex_indices.items():
        if vert_indices.size == 0:
            continue
        trees_per_bone[bone_idx] = (
            cKDTree(posed[vert_indices]),
            vert_indices,
        )

    splat_vertex_indices = np.zeros(splat_centers.shape[0], dtype=np.int32)
    splat_relative_poses = np.zeros((splat_centers.shape[0], 3), dtype=np.float32)

    # Fall-back tree across all vertices for splats whose bone has zero binned
    # vertices (rare; happens when a splat ended up on a bone whose capsule
    # was the nearest but no mesh vertex chose that capsule).
    fallback_tree = cKDTree(posed)

    for i in range(splat_centers.shape[0]):
        bone_idx = int(splat_bone_indices[i])
        target = splat_centers[i]
        bound = trees_per_bone.get(bone_idx)
        if bound is None or bound[1].size == 0:
            _, vi_global = fallback_tree.query(target)
            anchor_vertex_index = int(vi_global)
        else:
            tree, vert_indices = bound
            _, local_idx = tree.query(target)
            anchor_vertex_index = int(vert_indices[local_idx])

        splat_vertex_indices[i] = anchor_vertex_index
        splat_relative_poses[i] = target - posed[anchor_vertex_index]

    return splat_vertex_indices, splat_relative_poses


# ============================================================================
# Public entry point
# ============================================================================


def preprocess_gvrm(
    ply_bytes: bytes,
    vrm_bytes: bytes,
    *,
    height_scale: float = 1.0,
) -> tuple[dict, bytes]:
    """Run the full preprocess + alignment pipeline.

    Returns `(data_json, aligned_ply_bytes)`:
      - `data_json` matches naruya's writer schema with real splat→bone
        bindings (no stub).
      - `aligned_ply_bytes` is the person-cropped PLY to bundle. Binary PLY
        vertex records are copied verbatim so retained face/head splats keep
        their original SH/covariance data.

    `ply_bytes` is the Gaussian Splat scan (binary PLY). `vrm_bytes` is the
    base humanoid VRM (`fem_vroid.vrm` bundled default, or a user upload).
    `height_scale` is baked into `modelScale` for renderer-side resize.
    """
    requested_height_scale = float(height_scale)
    splat_centers_raw = parse_ply_centers(ply_bytes)
    if splat_centers_raw.size == 0:
        raise ValueError("PLY contains no splats (empty vertex element)")

    # We need preliminary cleanSplats to estimate arm angle BEFORE
    # building the skeleton (the bone operations depend on the scan's
    # actual pose). Run it first; the result feeds both the binding
    # frame computation AND the per-scan A-pose default.
    _prelim_cleaned, _prelim_centroid, _prelim_heights, _prelim_dist_xz, _ = (
        _clean_splats(splat_centers_raw)
    )
    estimated_arm_deg = _estimate_arm_angle_degrees(
        _prelim_cleaned, _prelim_centroid, _prelim_heights,
    )

    # Build per-scan boneOperations using the estimated arm angle, falling
    # back to the hardcoded default if estimation failed.
    bone_operations = list(DEFAULT_A_POSE_BONE_OPERATIONS)
    if estimated_arm_deg is not None:
        # In our convention: leftUpperArm.z = +deg, rightUpperArm.z = -deg
        for op in bone_operations:
            if op.get("boneName") == "leftUpperArm":
                op["rotation"]["z"] = float(estimated_arm_deg)
            elif op.get("boneName") == "rightUpperArm":
                op["rotation"]["z"] = float(-estimated_arm_deg)
        logger.info(
            "preprocess: using per-scan estimated arm angle ±%.1f° "
            "(replaces hardcoded default)", estimated_arm_deg,
        )
    else:
        logger.info(
            "preprocess: using hardcoded default arm angle ±35° "
            "(scan didn't have enough data for estimation)"
        )

    skel = parse_vrm_skeleton(vrm_bytes, bone_operations=bone_operations)
    capsules = _build_capsules(skel)
    if not capsules:
        raise ValueError(
            "VRM has no bones matching BONE_CONFIG (need J_Bip_* bone naming "
            "from VRoid Studio / VRM 0.x). Check the base VRM's humanoid rig."
        )

    # ---- Stage 0: cleanSplats (background removal + feet/head centroid) ----
    # Already ran above for arm-angle estimation; reuse those results.
    cleaned_centers = _prelim_cleaned
    centroid_xz = _prelim_centroid
    heights = _prelim_heights
    dist_xz = _prelim_dist_xz
    floor_y, ceil_y = heights

    # Recompute the cylinder mask against the original PLY so the bundled
    # PLY (filter applied below) and bound splats line up.
    person_cylinder_mask = _person_mask(splat_centers_raw, centroid_xz, heights, dist_xz)

    # ---- Stage 0b: detectShoes (floor noise removal) ----
    # Drops splats in the bottom 5% height band that don't have enough
    # vertical mass — i.e., flat floor cells, NOT shoes (which rise off
    # the ground). Combined with the person-cylinder mask, this drops
    # both the wall-distant floor AND the immediate floor below feet.
    shoes_mask = _detect_shoes(splat_centers_raw, heights, centroid_xz)

    # Final keep mask = inside cylinder AND survived shoe filter.
    neg_y = -splat_centers_raw[:, 1]
    radial = np.sqrt(
        (splat_centers_raw[:, 0] - centroid_xz[0]) ** 2
        + (splat_centers_raw[:, 2] - centroid_xz[1]) ** 2
    )
    body_height = max(float(ceil_y - floor_y), 1e-3)
    head_guard_radius = max(float(dist_xz), 0.45)
    head_guard_mask = (
        (neg_y >= floor_y + body_height * 0.62)
        & (neg_y <= ceil_y + 0.20)
        & (radial <= head_guard_radius)
    )
    final_mask = (person_cylinder_mask | head_guard_mask) & shoes_mask
    kept_count = int(final_mask.sum())
    if kept_count == 0 or (kept_count < 1000 and len(splat_centers_raw) >= 1000):
        logger.warning(
            "preprocess: person crop kept only %d/%d splats; falling back to raw PLY",
            kept_count, len(splat_centers_raw),
        )
        final_mask = np.ones(len(splat_centers_raw), dtype=bool)
        kept_count = int(final_mask.sum())
    cleaned_centers = splat_centers_raw[final_mask]
    logger.info(
        "preprocess: %d → %d splats after cylinder + detectShoes (%.1f%% kept)",
        len(splat_centers_raw), int(final_mask.sum()),
        100.0 * float(final_mask.sum()) / max(len(splat_centers_raw), 1),
    )

    # ---- Stage 0b: auto-detect modelScale from splat height ----
    # Mirrors preprocess.js:779: vrmScale = (heights.max - heights.min) /
    #                                       (- character.ground * 2 + 0.05).
    # `character.ground = -bbsize.y * 0.5 * scale`, so RHS denominator =
    # bbsize.y * scale + 0.05. We solve for scale as splat_height / bbsize.
    bbox_min = skel.posed_vertices.min(axis=0)
    bbox_max = skel.posed_vertices.max(axis=0)
    bbsize_y = float(bbox_max[1] - bbox_min[1])

    splat_height = float(ceil_y - floor_y)
    if abs(height_scale - 1.0) < 1e-6 and bbsize_y > 1e-3:
        # Default-ish height_scale → auto-fit. naruya's formula has +0.05
        # in the denominator as a small slack; keep that.
        height_scale = float(splat_height / (bbsize_y + 0.05))
        logger.info(
            "preprocess: auto-fit modelScale=%.3f from splat height %.2fm vs "
            "bundled VRM body %.2fm",
            height_scale, splat_height, bbsize_y,
        )

    # ---- Stage 0c: build runtime gsScene + vrm.scene matrices ----
    # These reproduce what naruya does in vrm.js (rotateVRM0 + ground) and
    # preprocess.js post-cleanSplats (gsScene.position/quaternion). We need
    # them so the binding sees splats in the same frame the runtime shader
    # will compute splat positions in.
    is_0x = _is_vrm_0x(_load_glb(vrm_bytes))
    vrm_scene_matrix, ground = _build_vrm_scene_matrix(bbsize_y, height_scale, is_0x)
    gs_scene_matrix = _build_gs_scene_matrix(centroid_xz, floor_y, ground)

    # Splat-raw → world (via gsScene) → mesh-local (via inverse(vrm.scene))
    # = the same value naruya's preprocess uses inside its relativePos
    # computation (preprocess.js:208-213):
    #   center0.applyMatrix4(gsScene.matrixWorld);
    #   center0.applyMatrix4(inverse(vrm.scene.matrixWorld));
    # We bind ALL raw splats now (not just `cleaned_centers`). The PLY
    # filtering through plyfile's read+write cycle was suspected to
    # subtly degrade splat quality (SH coefficients, covariance). By
    # bundling the original PLY untouched and providing one binding
    # entry per splat we eliminate that variable. Background splats
    # bind to whatever bone is geometrically closest — they render
    # along with the avatar but stay anchored, mirroring naruya's
    # `gs0` background scene behavior.
    splat_to_mesh = np.linalg.inv(vrm_scene_matrix) @ gs_scene_matrix
    splat_h = np.column_stack(
        [cleaned_centers, np.ones(cleaned_centers.shape[0], dtype=np.float64)]
    )
    splat_mesh_local = (splat_h @ splat_to_mesh.T)[:, :3].astype(np.float32)
    bbox_local_min = splat_mesh_local.min(axis=0)
    bbox_local_max = splat_mesh_local.max(axis=0)

    logger.info(
        "preprocess: %d splats (%d after cleanSplats), %d bones, %d capsules; "
        "centroid_xz=(%.2f,%.2f), heights=(%.2f,%.2f), modelScale=%.3f, "
        "ground=%.3f, is_vrm_0x=%s",
        splat_centers_raw.shape[0], cleaned_centers.shape[0], len(skel.bones), len(capsules),
        centroid_xz[0], centroid_xz[1], floor_y, ceil_y, height_scale, ground, is_0x,
    )
    logger.info(
        "preprocess: splat bbox in mesh-local (post gsScene+inverse(vrm.scene)): "
        "min=%s max=%s",
        bbox_local_min.tolist(), bbox_local_max.tolist(),
    )

    # ---- Stage 1+2: bind splats (mesh-local) → bones / vertices ----
    # `posed_vertices` are at rest pose in mesh-local frame (no VRM 0.x flip
    # applied server-side; runtime applies it via meshMatrixWorld). Capsules
    # are computed from bone world positions which are also in mesh-local.
    splat_bone_indices = _assign_splats_to_bones(splat_mesh_local, capsules)
    bone_vertex_bins = _bin_vertices_to_bones(skel, capsules)
    splat_vertex_indices, splat_relative_poses = _assign_splats_to_points(
        splat_mesh_local, splat_bone_indices, skel, bone_vertex_bins,
    )

    rel_norms = np.linalg.norm(splat_relative_poses, axis=1)
    logger.info(
        "preprocess: relative pose norm min=%.3f mean=%.3f max=%.3f",
        float(rel_norms.min()), float(rel_norms.mean()), float(rel_norms.max()),
    )

    # Bundle the ORIGINAL PLY untouched. We previously filtered through
    # plyfile (read → mask → write) but suspected the round-trip was
    # subtly degrading splat covariance / SH coefficient precision —
    # rendered splats came out chunkier than naruya's reference. Going
    # back to the raw upload guarantees we don't introduce render
    # artifacts. cleanSplats's centroid/heights still drive alignment;
    # background splats just render in place (visually similar to
    # naruya's `gs0` background scene).
    aligned_ply_bytes = _filter_ply_bytes(ply_bytes, final_mask)

    # Bone NAMES list (indexed by our preprocess bone_index). Runtime uses
    # this to remap splatBoneIndices/splatVertexIndices to its own current
    # skeleton.bones order — three-vrm 3.x's combineSkeletons may reorder
    # bones, so a server-side index of 12 doesn't necessarily mean the
    # same bone as runtime's `skeleton.bones[12]`. Without this mapping
    # the per-bone scene transforms in updateByBones land splats at
    # arbitrary bones (or off-screen).
    server_bone_names = [b.name for b in skel.bones]

    # gsPosition + gsQuaternion encode the cleanSplats-derived alignment.
    # Runtime's GVRM.initGS passes these to GS3D's per-scene rotation +
    # position; combined with the shader's anchor-based splat positioning
    # they place the splat cloud on the VRM body without further user
    # intervention.
    gs_position = [
        float(centroid_xz[0]),
        float(ground - floor_y),
        float(-centroid_xz[1]),
    ]
    # Quaternion (x, y, z, w) for 180° around Z.
    gs_quaternion = [0.0, 0.0, 1.0, 0.0]

    # Build data.json matching naruya's writer (gvrm-format/gvrm.js:528-541)
    # plus our extensions for index/mesh translation.
    data_json = {
        "modelScale": float(height_scale),
        "boneOperations": bone_operations,
        "gsPosition": gs_position,
        "gsQuaternion": gs_quaternion,
        "splatVertexIndices": splat_vertex_indices.tolist(),
        "splatBoneIndices": splat_bone_indices.tolist(),
        "splatRelativePoses": splat_relative_poses.flatten().tolist(),
        # Server build metadata. Runtime keys on `_buildVersion` to flag
        # stale .gvrm builds and trigger the rebuild banner.
        "_buildVersion": BUILD_VERSION,
        "_builtHeightScale": requested_height_scale,
        "_attribution": "Algorithm based on naruya/gaussian-vrm (MIT)",
        "_floorY": float(floor_y),
        "_ceilY": float(ceil_y),
        "_centroidXZ": [float(centroid_xz[0]), float(centroid_xz[1])],
        "_distXZ": float(dist_xz),
        "_sourceSplatCount": int(splat_centers_raw.shape[0]),
        "_croppedSplatCount": int(cleaned_centers.shape[0]),
        "_cropMode": "person-binary-lossless",
        "_serverBoneNames": server_bone_names,
        # Vertex count of the body primitive the server bound against.
        # Three.js GLTFLoader creates one SkinnedMesh per primitive; the
        # runtime picks the SkinnedMesh whose vertex count matches this
        # value so anchor lookups land on the right vertices.
        "_serverMeshVertexCount": int(skel.posed_vertices.shape[0]),
    }
    return data_json, aligned_ply_bytes


# Re-export `struct` only to keep tools that scan for unused imports happy.
_ = struct
