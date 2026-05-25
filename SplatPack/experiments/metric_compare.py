"""Compute PSNR/SSIM/LPIPS between a reference image set and one or more method
image sets, all rendered at the same orbit viewset.

Layout assumption:
  <root>/<scene>/<method>/view_XXX.png
Reference method label can be passed via --reference (default 'vanilla').
Writes a CSV row per (scene, method) pair to --out, averaging over the
viewset.
"""
from __future__ import annotations

import argparse
import csv
import sys
from pathlib import Path

import numpy as np
from PIL import Image

import torch
from skimage.metrics import peak_signal_noise_ratio as sk_psnr
from skimage.metrics import structural_similarity as sk_ssim
import lpips


def _load_image(path: Path) -> np.ndarray:
    img = np.array(Image.open(path).convert("RGB"), dtype=np.float32) / 255.0
    return img


def _to_lpips_tensor(img: np.ndarray) -> torch.Tensor:
    # lpips wants float tensors in [-1, 1] shaped (1, 3, H, W).
    t = torch.from_numpy(img).permute(2, 0, 1).unsqueeze(0)
    return t * 2.0 - 1.0


def _list_views(folder: Path) -> list[Path]:
    return sorted(folder.glob("view_*.png"))


def compute_pair_metrics(
    ref_dir: Path,
    method_dir: Path,
    lpips_model: lpips.LPIPS,
) -> dict[str, float] | None:
    ref_views = _list_views(ref_dir)
    method_views = _list_views(method_dir)
    if not ref_views or not method_views:
        return None
    if len(ref_views) != len(method_views):
        print(f"  WARN: ref has {len(ref_views)} views, method has {len(method_views)}; using min.", file=sys.stderr)
    n = min(len(ref_views), len(method_views))

    psnrs: list[float] = []
    ssims: list[float] = []
    lps: list[float] = []
    for i in range(n):
        ref_img = _load_image(ref_views[i])
        method_img = _load_image(method_views[i])
        if ref_img.shape != method_img.shape:
            print(f"  WARN: view {i} shape mismatch; skipping.", file=sys.stderr)
            continue
        psnrs.append(sk_psnr(ref_img, method_img, data_range=1.0))
        ssims.append(sk_ssim(ref_img, method_img, data_range=1.0, channel_axis=2))
        with torch.inference_mode():
            lp = lpips_model(_to_lpips_tensor(ref_img), _to_lpips_tensor(method_img)).item()
        lps.append(lp)
    return {
        "views": float(n),
        "psnr_mean": float(np.mean(psnrs)),
        "ssim_mean": float(np.mean(ssims)),
        "lpips_mean": float(np.mean(lps)),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, help="Root containing <scene>/<method>/view_XXX.png")
    parser.add_argument("--reference", default="vanilla", help="Method label whose renders are treated as ground truth.")
    parser.add_argument("--out", required=True, help="Output CSV.")
    args = parser.parse_args()

    root = Path(args.root)
    # lpips alex is the default 3DGS evaluation choice; smaller + faster than vgg.
    lpips_model = lpips.LPIPS(net="alex", verbose=False)

    rows: list[dict[str, object]] = []
    for scene_dir in sorted(p for p in root.iterdir() if p.is_dir()):
        ref_dir = scene_dir / args.reference
        if not ref_dir.is_dir():
            print(f"{scene_dir.name}: no reference dir '{args.reference}', skipping", file=sys.stderr)
            continue
        for method_dir in sorted(p for p in scene_dir.iterdir() if p.is_dir() and p.name != args.reference):
            metrics = compute_pair_metrics(ref_dir, method_dir, lpips_model)
            if metrics is None:
                print(f"{scene_dir.name}/{method_dir.name}: missing renders", file=sys.stderr)
                continue
            row = {"scene": scene_dir.name, "method": method_dir.name, **metrics}
            print(
                f"{scene_dir.name:<12} {method_dir.name:<24} "
                f"PSNR {metrics['psnr_mean']:6.2f}  "
                f"SSIM {metrics['ssim_mean']:.4f}  "
                f"LPIPS {metrics['lpips_mean']:.4f}  "
                f"({int(metrics['views'])} views)"
            )
            rows.append(row)

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with out_path.open("w", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=["scene", "method", "views", "psnr_mean", "ssim_mean", "lpips_mean"])
        writer.writeheader()
        writer.writerows(rows)
    print(f"\nwrote {len(rows)} rows to {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
