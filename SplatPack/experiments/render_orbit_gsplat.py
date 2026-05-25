"""CUDA gsplat-backed orbit renderer for RunPod (or any GPU box).

Loads a vanilla 3DGS PLY or a SplatPack v5/v6 asset, then renders every view
in the orbit viewset to a PNG. Uses gsplat.rasterization with rasterize_mode
'classic' so the output matches the canonical 3DGS render path that reviewers
expect.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import numpy as np
import torch
from PIL import Image
from gsplat import rasterization

# Local readers (Python files copied alongside this script on the pod).
import gaussian_ply_reader
import splatpack_reader


def _view_matrix(position: np.ndarray, forward: np.ndarray, up_hint: np.ndarray) -> torch.Tensor:
    """World-from-camera 4x4 matrix. Camera convention: +X right, +Y down, +Z forward."""
    f = forward / (np.linalg.norm(forward) + 1e-12)
    right = np.cross(up_hint, f)
    norm = np.linalg.norm(right)
    if norm < 1e-6:
        right = np.cross(np.array([1.0, 0.0, 0.0]), f)
        norm = np.linalg.norm(right)
    right = right / (norm + 1e-12)
    down = np.cross(f, right)
    R_wc = np.stack([right, down, f], axis=1)
    M = np.eye(4, dtype=np.float64)
    M[:3, :3] = R_wc
    M[:3, 3] = position
    return torch.from_numpy(M.astype(np.float32))


def _splatpack_tensors(data: splatpack_reader.SplatPackData, device: torch.device):
    means = torch.from_numpy(data.centers).float().to(device)
    quats = torch.from_numpy(data.quaternions_wxyz).float().to(device)
    scales = torch.from_numpy(data.scales).float().to(device)
    opacities = torch.from_numpy(data.opacities).float().to(device)
    # SplatPack DC-only; higher-order SH path is a separate task.
    sh = torch.from_numpy(data.dc_rgb).float().unsqueeze(1).to(device)
    return means, quats, scales, opacities, sh, 0


def _ply_tensors(data: gaussian_ply_reader.GaussianPlyData, sh_degree: int, device: torch.device):
    k_target = (sh_degree + 1) ** 2
    n_avail = min(k_target, data.sh_coeffs.shape[1])
    used_degree = int(math.floor(math.sqrt(n_avail))) - 1
    means = torch.from_numpy(data.centers).float().to(device)
    quats = torch.from_numpy(data.quaternions_wxyz).float().to(device)
    scales = torch.from_numpy(data.scales).float().to(device)
    opacities = torch.from_numpy(data.opacities).float().to(device)
    sh = torch.from_numpy(data.sh_coeffs[:, :n_avail, :]).float().to(device)
    return means, quats, scales, opacities, sh, used_degree


def render(
    asset_path: Path,
    viewset_path: Path,
    out_dir: Path,
    *,
    image_size: int = 512,
    sh_degree: int = 3,
    up_hint: np.ndarray | None = None,
    background: tuple[float, float, float] = (1.0, 1.0, 1.0),
) -> None:
    out_dir.mkdir(parents=True, exist_ok=True)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")

    suffix = asset_path.suffix.lower()
    if suffix == ".ply":
        data = gaussian_ply_reader.load(asset_path)
        means, quats, scales, opacities, sh, used_degree = _ply_tensors(data, sh_degree, device)
    elif suffix == ".splatpack":
        data = splatpack_reader.load(asset_path)
        means, quats, scales, opacities, sh, used_degree = _splatpack_tensors(data, device)
    else:
        raise ValueError(f"Unsupported asset extension: {suffix}")

    payload = json.loads(viewset_path.read_text())
    views = payload["views"] if isinstance(payload, dict) else payload

    fov_y = float(views[0].get("fovYDegrees", 50.0))
    aspect = float(views[0].get("aspect", 1.0))
    fy = (image_size / 2.0) / math.tan(math.radians(fov_y) / 2.0)
    fx = fy * aspect
    cx = image_size / 2.0
    cy = image_size / 2.0
    K = torch.tensor([[fx, 0.0, cx], [0.0, fy, cy], [0.0, 0.0, 1.0]], dtype=torch.float32, device=device)
    K_batch = K.unsqueeze(0)
    up = up_hint if up_hint is not None else np.array([0.0, -1.0, 0.0])
    bg = torch.tensor(background, dtype=torch.float32, device=device).unsqueeze(0)

    for i, view in enumerate(views):
        position = np.array(view["position"], dtype=np.float64)
        forward = np.array(view["forward"], dtype=np.float64)
        world_from_cam = _view_matrix(position, forward, up)
        cam_from_world = torch.linalg.inv(world_from_cam).unsqueeze(0).to(device)

        with torch.inference_mode():
            colors, _alphas, _meta = rasterization(
                means=means,
                quats=quats,
                scales=scales,
                opacities=opacities,
                colors=sh,
                viewmats=cam_from_world,
                Ks=K_batch,
                width=image_size,
                height=image_size,
                sh_degree=used_degree,
                backgrounds=bg,
                rasterize_mode="classic",
            )

        img = colors[0].clamp(0, 1).cpu().numpy()
        Image.fromarray((img * 255.0 + 0.5).astype(np.uint8)).save(out_dir / f"view_{i:03d}.png")

    print(f"{asset_path.name}: rendered {len(views)} views to {out_dir}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--asset", required=True)
    parser.add_argument("--viewset", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--image-size", type=int, default=512)
    parser.add_argument("--sh-degree", type=int, default=3)
    parser.add_argument("--up", type=float, nargs=3, default=None)
    args = parser.parse_args()
    render(
        Path(args.asset),
        Path(args.viewset),
        Path(args.out),
        image_size=args.image_size,
        sh_degree=args.sh_degree,
        up_hint=np.array(args.up, dtype=np.float64) if args.up else None,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
