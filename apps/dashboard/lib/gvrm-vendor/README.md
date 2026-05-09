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

**None to the vendored .js files.** We resolve naruya's unscoped
`'gaussian-splats-3d'` specifier to `@mkkellogg/gaussian-splats-3d` via
a webpack alias in `next.config.ts` rather than editing the source.

## Updating

Re-clone naruya/gaussian-vrm and copy:
- `gvrm-format/{gs,gvrm,ply,utils,vrm}.js`
- `apps/preprocess/*.js`
- `LICENSE.txt`

into this directory, then verify the alias still covers all unscoped
imports.
