"""Read a SplatPack v5/v6 file and return numpy arrays compatible with gsplat.

Schema reference: SplatPackWriter.cs (v5/v6 = ReferenceFloat / ResearchSectioned).
Header: 4B magic 'SPK1' | i32 version | i32 splatCount | i32 chunkCount |
        i32 splatStride | i32 chunkStride | Vec3 boundsMin | Vec3 boundsMax |
        f32 axisRange | i32 featureFlags.
Chunks: 36 bytes each (Vec3 min, Vec3 max, i32 offset, i32 count, i32 lod).
Splats (v5/v6, float): 9 * Vec4 = 144 bytes — center, axis0..2, color, meta,
                       sh1R, sh1G, sh1B.
Research sections (v6 only): i32 sectionCount, then each section is
                             {i32 tag, i32 recordCount, i32 stride, payload}.
"""
from __future__ import annotations

import struct
from dataclasses import dataclass
from pathlib import Path

import numpy as np

SECTION_TAG_SH3 = 0x20334853  # 'SH3 '

FLOAT_SPLAT_STRIDE = 36 * 4
CHUNK_STRIDE = 6 * 4 + 3 * 4


@dataclass
class SplatPackData:
    version: int
    bounds_min: np.ndarray  # (3,)
    bounds_max: np.ndarray  # (3,)
    centers: np.ndarray     # (N, 3)
    axes: np.ndarray        # (N, 3, 3)  rows are world-space axes
    colors: np.ndarray      # (N, 4)     rgba (sRGB-ish, 0..1)
    sh1: np.ndarray         # (N, 9)     [r0..r2, g0..g2, b0..b2] (linear SH1)
    sh3: np.ndarray | None  # (N, 12, 4) higher-order SH if present
    splat_count: int

    @property
    def scales(self) -> np.ndarray:
        """Per-splat axis lengths (3 scales)."""
        return np.linalg.norm(self.axes, axis=-1)

    @property
    def rotations_matrix(self) -> np.ndarray:
        """Per-splat 3x3 rotation matrices (axes normalized)."""
        s = self.scales[:, :, None]  # (N, 3, 1)
        # Avoid divide-by-zero; degenerate axes become identity row.
        safe = np.where(s > 1e-12, s, 1.0)
        return self.axes / safe

    @property
    def quaternions_wxyz(self) -> np.ndarray:
        """Per-splat quaternions in (w, x, y, z) order."""
        R = self.rotations_matrix
        # Ensure right-handed orthonormal frame: flip last axis if det < 0.
        det = np.linalg.det(R)
        flip = det < 0
        R = R.copy()
        R[flip, :, 2] *= -1.0
        return _matrix_to_quat_wxyz(R)

    @property
    def opacities(self) -> np.ndarray:
        return self.colors[:, 3]

    @property
    def dc_rgb(self) -> np.ndarray:
        return self.colors[:, :3]


def load(path: str | Path) -> SplatPackData:
    raw = Path(path).read_bytes()
    if raw[:4] != b"SPK1":
        raise ValueError(f"{path}: not a SplatPack file (bad magic {raw[:4]!r})")
    cursor = 4
    version, splat_count, chunk_count, splat_stride, chunk_stride = struct.unpack_from("<5i", raw, cursor)
    cursor += 5 * 4
    bounds_min = np.array(struct.unpack_from("<3f", raw, cursor), dtype=np.float32)
    cursor += 12
    bounds_max = np.array(struct.unpack_from("<3f", raw, cursor), dtype=np.float32)
    cursor += 12
    _axis_range, _feature_flags = struct.unpack_from("<fi", raw, cursor)
    cursor += 8

    if version not in (5, 6):
        raise ValueError(f"{path}: only v5/v6 float-format SplatPack supported (got v{version})")
    if splat_stride != FLOAT_SPLAT_STRIDE:
        raise ValueError(f"{path}: expected float splat stride {FLOAT_SPLAT_STRIDE}, got {splat_stride}")

    # Skip chunks; we render at the splat granularity.
    cursor += chunk_count * chunk_stride

    splats = np.frombuffer(raw, dtype=np.float32, count=splat_count * 36, offset=cursor).reshape(splat_count, 36)
    cursor += splat_count * splat_stride

    centers = splats[:, 0:3].copy()
    axis0 = splats[:, 4:7].copy()
    axis1 = splats[:, 8:11].copy()
    axis2 = splats[:, 12:15].copy()
    axes = np.stack([axis0, axis1, axis2], axis=1)  # (N, 3, 3)
    colors = splats[:, 16:20].copy()
    sh1 = np.stack([
        splats[:, 24:27],
        splats[:, 28:31],
        splats[:, 32:35],
    ], axis=1).reshape(splat_count, 9).copy()

    sh3 = None
    if version == 6 and cursor < len(raw):
        (section_count,) = struct.unpack_from("<i", raw, cursor)
        cursor += 4
        for _ in range(section_count):
            tag, record_count, stride = struct.unpack_from("<3i", raw, cursor)
            cursor += 12
            payload_size = record_count * stride
            if tag == SECTION_TAG_SH3:
                buf = np.frombuffer(raw, dtype=np.float32, count=record_count * stride // 4, offset=cursor)
                sh3 = buf.reshape(record_count, 12, 4).copy()
            cursor += payload_size

    return SplatPackData(
        version=version,
        bounds_min=bounds_min,
        bounds_max=bounds_max,
        centers=centers,
        axes=axes,
        colors=colors,
        sh1=sh1,
        sh3=sh3,
        splat_count=splat_count,
    )


def _matrix_to_quat_wxyz(R: np.ndarray) -> np.ndarray:
    """Batched rotation matrix -> quaternion (w, x, y, z). Numerically stable form."""
    m = R
    trace = m[:, 0, 0] + m[:, 1, 1] + m[:, 2, 2]
    out = np.zeros((m.shape[0], 4), dtype=np.float32)
    # Branch by the largest diagonal to keep the divisor large.
    cond0 = trace > 0
    cond1 = (~cond0) & (m[:, 0, 0] >= m[:, 1, 1]) & (m[:, 0, 0] >= m[:, 2, 2])
    cond2 = (~cond0) & (~cond1) & (m[:, 1, 1] >= m[:, 2, 2])
    cond3 = ~(cond0 | cond1 | cond2)

    s0 = np.sqrt(np.maximum(trace[cond0] + 1.0, 1e-12)) * 2.0
    out[cond0, 0] = 0.25 * s0
    out[cond0, 1] = (m[cond0, 2, 1] - m[cond0, 1, 2]) / s0
    out[cond0, 2] = (m[cond0, 0, 2] - m[cond0, 2, 0]) / s0
    out[cond0, 3] = (m[cond0, 1, 0] - m[cond0, 0, 1]) / s0

    s1 = np.sqrt(np.maximum(1.0 + m[cond1, 0, 0] - m[cond1, 1, 1] - m[cond1, 2, 2], 1e-12)) * 2.0
    out[cond1, 0] = (m[cond1, 2, 1] - m[cond1, 1, 2]) / s1
    out[cond1, 1] = 0.25 * s1
    out[cond1, 2] = (m[cond1, 0, 1] + m[cond1, 1, 0]) / s1
    out[cond1, 3] = (m[cond1, 0, 2] + m[cond1, 2, 0]) / s1

    s2 = np.sqrt(np.maximum(1.0 + m[cond2, 1, 1] - m[cond2, 0, 0] - m[cond2, 2, 2], 1e-12)) * 2.0
    out[cond2, 0] = (m[cond2, 0, 2] - m[cond2, 2, 0]) / s2
    out[cond2, 1] = (m[cond2, 0, 1] + m[cond2, 1, 0]) / s2
    out[cond2, 2] = 0.25 * s2
    out[cond2, 3] = (m[cond2, 1, 2] + m[cond2, 2, 1]) / s2

    s3 = np.sqrt(np.maximum(1.0 + m[cond3, 2, 2] - m[cond3, 0, 0] - m[cond3, 1, 1], 1e-12)) * 2.0
    out[cond3, 0] = (m[cond3, 1, 0] - m[cond3, 0, 1]) / s3
    out[cond3, 1] = (m[cond3, 0, 2] + m[cond3, 2, 0]) / s3
    out[cond3, 2] = (m[cond3, 1, 2] + m[cond3, 2, 1]) / s3
    out[cond3, 3] = 0.25 * s3

    return out


if __name__ == "__main__":
    import sys
    for arg in sys.argv[1:]:
        data = load(arg)
        print(f"{arg}: v{data.version} {data.splat_count} splats, bounds {data.bounds_min} -> {data.bounds_max}")
        print(f"  scales mean={data.scales.mean():.4f}, sh3 present={data.sh3 is not None}")
