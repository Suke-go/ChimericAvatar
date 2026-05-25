"""Read a vanilla Inria 3DGS-format PLY into gsplat-ready numpy arrays.

The PLY schema is the de-facto standard from the original 3DGS paper:
  x, y, z, nx, ny, nz (unused), f_dc_0..2, f_rest_0..44, opacity,
  scale_0..2 (log), rot_0..3 (wxyz quaternion).

Returns activated values: scales = exp(log_scale), opacities = sigmoid(logit),
quaternions normalized, SH coefficients reshaped to (N, K, 3) with K = 16
(SH degree 3: 1 + 3 + 5 + 7 = 16 per channel).
"""
from __future__ import annotations

import struct
from dataclasses import dataclass
from pathlib import Path

import numpy as np


@dataclass
class GaussianPlyData:
    centers: np.ndarray       # (N, 3)
    quaternions_wxyz: np.ndarray  # (N, 4), normalized
    scales: np.ndarray        # (N, 3), activated (positive)
    opacities: np.ndarray     # (N,), activated (0..1)
    sh_coeffs: np.ndarray     # (N, 16, 3), DC first, then SH1, SH2, SH3
    splat_count: int

    @property
    def dc_rgb(self) -> np.ndarray:
        return self.sh_coeffs[:, 0, :]


def _sigmoid(x: np.ndarray) -> np.ndarray:
    return 1.0 / (1.0 + np.exp(-x))


def _parse_header(raw: bytes) -> tuple[int, list[str], int]:
    """Return (vertex_count, property_names_in_order, header_end_offset)."""
    if not raw.startswith(b"ply\n"):
        raise ValueError("not a PLY file")
    cursor = 0
    end = raw.find(b"end_header\n")
    if end < 0:
        raise ValueError("PLY header missing end_header")
    header_text = raw[:end].decode("ascii")
    vertex_count = 0
    properties: list[str] = []
    for line in header_text.splitlines():
        if line.startswith("format ") and "binary_little_endian" not in line:
            raise ValueError(f"only binary little-endian PLY supported (got '{line}')")
        if line.startswith("element vertex "):
            vertex_count = int(line.split()[-1])
        elif line.startswith("property float "):
            properties.append(line.split()[-1])
    return vertex_count, properties, end + len(b"end_header\n")


def load(path: str | Path) -> GaussianPlyData:
    raw = Path(path).read_bytes()
    vertex_count, properties, data_offset = _parse_header(raw)
    if not properties:
        raise ValueError(f"{path}: no float properties found in header")
    arr = np.frombuffer(raw, dtype=np.float32, count=vertex_count * len(properties), offset=data_offset).reshape(
        vertex_count, len(properties)
    )

    name_to_col = {name: i for i, name in enumerate(properties)}

    def col(name: str) -> np.ndarray:
        return arr[:, name_to_col[name]].copy()

    centers = np.stack([col("x"), col("y"), col("z")], axis=1)
    log_scales = np.stack([col("scale_0"), col("scale_1"), col("scale_2")], axis=1)
    scales = np.exp(log_scales)

    quats = np.stack([col("rot_0"), col("rot_1"), col("rot_2"), col("rot_3")], axis=1)
    norms = np.linalg.norm(quats, axis=1, keepdims=True)
    quats = quats / np.maximum(norms, 1e-12)

    opacities = _sigmoid(col("opacity"))

    dc = np.stack([col("f_dc_0"), col("f_dc_1"), col("f_dc_2")], axis=1)  # (N, 3)
    rest_names = [name for name in properties if name.startswith("f_rest_")]
    rest_names.sort(key=lambda n: int(n.split("_")[-1]))
    if rest_names:
        rest_count = len(rest_names)
        rest = np.stack([col(name) for name in rest_names], axis=1)  # (N, rest_count)
        per_channel = rest_count // 3
        # Inria layout: 3 channels × per_channel SH coefficients, channel-major.
        rest = rest.reshape(arr.shape[0], 3, per_channel).transpose(0, 2, 1)  # (N, per_channel, 3)
        sh_coeffs = np.concatenate([dc[:, None, :], rest], axis=1)
    else:
        sh_coeffs = dc[:, None, :]

    return GaussianPlyData(
        centers=centers.astype(np.float32),
        quaternions_wxyz=quats.astype(np.float32),
        scales=scales.astype(np.float32),
        opacities=opacities.astype(np.float32),
        sh_coeffs=sh_coeffs.astype(np.float32),
        splat_count=vertex_count,
    )


if __name__ == "__main__":
    import sys
    for arg in sys.argv[1:]:
        data = load(arg)
        print(f"{arg}: {data.splat_count} splats, SH coeffs per splat = {data.sh_coeffs.shape[1]}")
        print(f"  scale mean={data.scales.mean():.4f}, opacity mean={data.opacities.mean():.4f}")
