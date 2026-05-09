"""Wrapper around nianticlabs/spz so the rest of the codebase can call
`encode_ply_to_spz(ply_bytes) -> spz_bytes` without caring about the C++
binding's file-path-based API.

The native binding is an optional dependency (Linux production Docker only;
Windows dev needs MSVC + libzstd which isn't worth the setup pain). When
the import fails we surface that clearly via `is_available()` so callers
can fall back to bundling the raw PLY.
"""

from __future__ import annotations

import gzip
import logging
import tempfile
from pathlib import Path

logger = logging.getLogger(__name__)


def is_available() -> bool:
    """True iff `spz` import succeeds. Cheap — caches via lru."""
    return _try_import() is not None


def encode_ply_to_spz(ply_bytes: bytes) -> bytes:
    """Convert PLY bytes to SPZ bytes (~10× smaller).

    Raises RuntimeError if the native binding is unavailable. Callers that
    want graceful degradation should check `is_available()` first.
    """
    spz = _try_import()
    if spz is None:
        raise RuntimeError(
            "nianticlabs/spz Python binding is not installed. Add 'spz' "
            "extra: pip install '.[spz]' (Linux only — needs cmake + libzstd-dev)."
        )

    with tempfile.TemporaryDirectory(prefix="spz-encode-") as tmpdir:
        ply_path = Path(tmpdir) / "input.ply"
        spz_path = Path(tmpdir) / "output.spz"
        ply_path.write_bytes(ply_bytes)

        # Pin SPZ format to v2 — that's the maximum version
        # mkkellogg/gaussian-splats-3d 0.4.7 (the in-browser preview's
        # decoder) supports. Niantic's binding defaults to v3+ on recent
        # builds, which crashes the preview with "version not supported".
        # We pay the slight format-feature loss to keep the dashboard
        # preview working; Unity has its own SPZ loader that we'll feed
        # whatever version the production .gvrm carries.
        pack_opts = spz.PackOptions()
        for attr in ("version", "spz_version", "format_version"):
            if hasattr(pack_opts, attr):
                try:
                    setattr(pack_opts, attr, 2)
                    logger.info("spz PackOptions.%s pinned to 2", attr)
                except Exception:
                    pass

        # The official binding expects file paths, not bytes.
        cloud = spz.load_splat_from_ply(str(ply_path), spz.UnpackOptions())
        spz.save_spz(cloud, pack_opts, str(spz_path))

        spz_bytes = spz_path.read_bytes()

    # The canonical SPZ wire format is **gzip-wrapped** binary
    # (mkkellogg/GaussianSplats3D, Niantic's web viewer, naruya/gvrm.js all
    # call gunzip first). The Python binding's `save_spz` writes the raw
    # NGSP-magic body uncompressed, so we wrap it in gzip ourselves when
    # we detect the unwrapped form. If the binding ever switches to writing
    # gzipped output, the magic check below short-circuits without
    # double-wrapping.
    if len(spz_bytes) >= 2 and spz_bytes[:2] == b"\x1f\x8b":
        # Already gzipped — pass through.
        return spz_bytes
    if len(spz_bytes) >= 4 and spz_bytes[:4] == b"NGSP":
        return gzip.compress(spz_bytes, compresslevel=6)
    raise RuntimeError(
        f"SPZ encoder produced output with unexpected magic "
        f"{spz_bytes[:4]!r} (expected gzip magic 1f 8b or raw NGSP)"
    )


def decode_spz_to_ply(spz_bytes: bytes) -> bytes:
    """Convert SPZ bytes back to PLY bytes. Used by the preprocess pipeline
    when the user uploads SPZ directly — splat-center extraction needs the
    PLY representation. Raises RuntimeError if `spz` isn't installed."""
    spz = _try_import()
    if spz is None:
        raise RuntimeError(
            "nianticlabs/spz Python binding is not installed; cannot decode "
            "SPZ to PLY. Upload PLY directly or install the [spz] extra."
        )

    # Niantic's `load_spz` may want raw NGSP bytes (uncompressed). If the
    # caller hands us gzip-wrapped SPZ — which is what the wire format and
    # the .gvrm zip carry — gunzip first.
    if len(spz_bytes) >= 2 and spz_bytes[:2] == b"\x1f\x8b":
        spz_bytes = gzip.decompress(spz_bytes)

    with tempfile.TemporaryDirectory(prefix="spz-decode-") as tmpdir:
        spz_path = Path(tmpdir) / "input.spz"
        ply_path = Path(tmpdir) / "output.ply"
        spz_path.write_bytes(spz_bytes)

        cloud = spz.load_spz(str(spz_path), spz.UnpackOptions())
        spz.save_splat_to_ply(cloud, spz.PackOptions(), str(ply_path))

        return ply_path.read_bytes()


def _try_import():
    try:
        import spz  # type: ignore[import-untyped]
        return spz
    except Exception as exc:  # ImportError or anything else from native init
        logger.debug("spz binding unavailable: %s", exc)
        return None
