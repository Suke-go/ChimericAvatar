"""Generate a deterministic orbit viewset around a scene bounding box.

Cameras orbit around the object centre on two elevation rings. The viewset is
identical for every method-variant of the same scene so renders are pixel-aligned
and PSNR/SSIM/LPIPS comparisons stay fair across configurations.

Output JSON is compatible with SplatPackViewSet.Load and gsplat's camera API
(positions + lookat target).
"""
from __future__ import annotations

import json
import math
from dataclasses import asdict, dataclass
from pathlib import Path

import numpy as np


@dataclass
class OrbitView:
    position: list[float]
    forward: list[float]
    weight: float
    eye: str = "mono"
    fovYDegrees: float = 50.0
    aspect: float = 1.0


def orbit_views(
    bounds_min: np.ndarray,
    bounds_max: np.ndarray,
    *,
    n_azimuth: int = 8,
    n_elevation: int = 2,
    radius_scale: float = 1.8,
    elevation_degrees: tuple[float, float] = (-15.0, 25.0),
) -> list[OrbitView]:
    centre = (bounds_min + bounds_max) * 0.5
    extent = float(np.max(bounds_max - bounds_min))
    radius = extent * radius_scale

    views: list[OrbitView] = []
    for i_elev in range(n_elevation):
        if n_elevation == 1:
            elev = sum(elevation_degrees) / len(elevation_degrees)
        else:
            t = i_elev / (n_elevation - 1)
            elev = elevation_degrees[0] * (1 - t) + elevation_degrees[1] * t
        elev_rad = math.radians(elev)
        for i_az in range(n_azimuth):
            az = 2 * math.pi * i_az / n_azimuth
            offset = np.array([
                radius * math.cos(elev_rad) * math.sin(az),
                radius * math.sin(elev_rad),
                radius * math.cos(elev_rad) * math.cos(az),
            ], dtype=np.float64)
            position = centre + offset
            forward = centre - position
            forward /= np.linalg.norm(forward) + 1e-12
            views.append(OrbitView(
                position=position.tolist(),
                forward=forward.tolist(),
                weight=1.0,
            ))
    return views


def write_viewset(path: Path, views: list[OrbitView]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = {"views": [asdict(v) for v in views]}
    path.write_text(json.dumps(payload, indent=2))


if __name__ == "__main__":
    import argparse

    from splatpack_reader import load as load_splatpack

    parser = argparse.ArgumentParser()
    parser.add_argument("--splatpack", required=True, help="A reference SplatPack asset for the scene's bounds.")
    parser.add_argument("--out", required=True)
    parser.add_argument("--n-azimuth", type=int, default=8)
    parser.add_argument("--n-elevation", type=int, default=2)
    parser.add_argument("--radius-scale", type=float, default=1.8)
    args = parser.parse_args()

    data = load_splatpack(args.splatpack)
    views = orbit_views(
        data.bounds_min.astype(np.float64),
        data.bounds_max.astype(np.float64),
        n_azimuth=args.n_azimuth,
        n_elevation=args.n_elevation,
        radius_scale=args.radius_scale,
    )
    write_viewset(Path(args.out), views)
    print(f"wrote {len(views)} views to {args.out}")
