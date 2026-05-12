# SplatPack

SplatPack is a standalone XR asset pipeline for 3D Gaussian Splatting.

The goal is to avoid treating a raw `.ply` as a runtime asset. A raw 3DGS
export is compiled offline into an XR-ready package that Unity can load with
minimal runtime work.

```text
standard 3DGS .ply
  -> SplatPack compiler
  -> .splatpack package
  -> Unity runtime renderer
  -> projected splat cache
  -> lightweight quad expansion
```

This directory is intentionally separate from `ChimericAvatar/`. The current
GVRM renderer remains the reference implementation for the rendering kernel,
but SplatPack targets generic `.ply` scenes first.

## Layout

```text
compiler/
  .NET CLI compiler for .ply -> .splatpack

unity/com.chimera.splatpack/
  Unity Package Manager package with runtime renderer, importer hooks, shaders,
  and compute kernels.

specs/
  Binary format and benchmark notes.
```

## First Milestone

1. Compile a binary little-endian 3DGS `.ply` into a chunked `.splatpack`.
2. Load the package in Unity without GVRM or avatar dependencies.
3. Render with a projected splat cache and lightweight quad expansion.
4. Validate on XREAL/OpenXR and then standalone HMD.

## Non-goals For The First Pass

- No VRM body loading.
- No bone skinning.
- No foveated or perceptual LOD.
- No server requirement. The compiler is a local CLI first.
