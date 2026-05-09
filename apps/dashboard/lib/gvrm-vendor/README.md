# Vendored: naruya/gaussian-vrm

Source: <https://github.com/naruya/gaussian-vrm>
License: MIT (see `LICENSE.txt`)
Vendored at commit: `main` (cloned 2026-05-07)

## What's inside

- `gvrm-format/` — runtime + format I/O (load .gvrm, render via GS3D + three-vrm)
- `apps/preprocess/` — browser-side preprocess pipeline that binds Gaussian
  Splats to a base VRM skeleton and emits a finished `.gvrm` zip

## Why we vendor instead of importing from npm

The published package `@naruya/gaussian-vrm` (1.0.2) ships a single
pre-bundled UMD-style file (`lib/gaussian-vrm.bundled.js`). For our build
pipeline we need to call `preprocess()` and `GVRM.save()` directly, which
are not part of the bundled exports. Vendoring the unbundled ES modules
gives us that direct access.

## Modifications

This directory starts from `naruya/gaussian-vrm` and keeps the upstream layout
visible. Local changes are intentionally narrow and are made for ChimericAvatar's
dashboard/backend integration:

- `gvrm-format/gs.js` keeps the upstream Gaussian rendering quality settings
  (`sphericalHarmonicsDegree: 2`, alpha threshold `0`) while using the local
  loader path needed by the dashboard.
- `gvrm-format/gvrm.js` adds ChimericAvatar preview/build integration, including
  static `.gvrm` preview loading and server-emitted binding metadata handling.
- `gvrm-format/ply.js` includes a binary-lossless PLY split path so SH,
  covariance, scale, rotation, opacity, and color properties are preserved.
- `apps/preprocess/` remains the upstream browser-side reference pipeline used
  for design parity and comparison.

The original MIT license is preserved in `LICENSE.txt`.

## Updating

Re-clone naruya/gaussian-vrm and copy:
- `gvrm-format/{gs,gvrm,ply,utils,vrm}.js`
- `apps/preprocess/*.js`
- `LICENSE.txt`

into this directory, then verify the alias still covers all unscoped
imports.
