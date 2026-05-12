# External Baselines And Repositories

SplatPack should remain independent from external renderers. These repositories
are used for baseline comparison, dataset access, and design reference.

## Priority 0: Proposed Method

Finish SplatPack first.

```text
SplatPack compiler
  -> chunk metadata
  -> .splatpack package
  -> Unity projected cache runtime
  -> XREAL / standalone XR logs
```

External repositories are not blockers for this path.

## Priority 1: Unity Baseline

### aras-p/UnityGaussianSplatting

```text
https://github.com/aras-p/UnityGaussianSplatting
```

Use this as the main Unity baseline.

Why:

- Mature Unity implementation.
- Imports original PLY and Scaniverse SPZ.
- Has compression options and performance notes.
- Reported to work on some VR devices.

Limits:

- The project documentation states it is mainly known to work on D3D12, Metal,
  and Vulkan.
- Mobile and OpenGL ES paths are not reliable baselines.
- It is useful for comparison, not as a dependency.

Baseline role:

```text
SplatPack vs UnityGaussianSplatting
  import size
  load time
  GPU memory
  FPS
  XR stereo stability
```

Clone only when we are ready to run a baseline:

```powershell
git clone --depth 1 https://github.com/aras-p/UnityGaussianSplatting.git SplatPack/experiments/external/UnityGaussianSplatting
```

## Priority 2: Official Reference And Datasets

### graphdeco-inria/gaussian-splatting

```text
https://github.com/graphdeco-inria/gaussian-splatting
```

Use this for:

- Original 3DGS reference.
- Official pretrained models and scenes.
- Canonical PLY schema assumptions.
- Paper-style baseline naming.

Do not use it as the XR runtime baseline. The viewer/training stack is not a
Unity standalone XR renderer.

Clone when we need official data or schema checks:

```powershell
git clone --depth 1 https://github.com/graphdeco-inria/gaussian-splatting.git SplatPack/experiments/external/gaussian-splatting
```

## Priority 3: Packaging And Web Runtime Reference

### playcanvas/supersplat

```text
https://github.com/playcanvas/supersplat
```

Use this as a packaging/editor reference.

Why:

- Open-source editor.
- Practical production workflow.
- Exports PLY, compressed PLY, and SOG.
- Useful reference for asset optimization UX.

Clone later, when designing SplatPack compression and editor-facing tools:

```powershell
git clone --depth 1 https://github.com/playcanvas/supersplat.git SplatPack/experiments/external/supersplat
```

### playcanvas/supersplat-viewer

```text
https://github.com/playcanvas/supersplat-viewer
```

Use this as a web viewer reference.

Why:

- Supports PLY, SOG, compressed PLY, metadata, and LOD metadata.
- Has runtime flags for WebGPU, splat budget, LOD colorization, and heatmaps.
- Good reference for streaming/LOD runtime design.

Clone later, when comparing runtime packaging and LOD behavior:

```powershell
git clone --depth 1 https://github.com/playcanvas/supersplat-viewer.git SplatPack/experiments/external/supersplat-viewer
```

## Priority 4: Minimal Format Reference

### antimatter15/splat

```text
https://github.com/antimatter15/splat
```

Use this as a small format/conversion reference.

Why:

- Minimal WebGL implementation.
- Includes a PLY-to-splat conversion path.
- Easy to inspect.

It is not a primary performance baseline for standalone XR.

```powershell
git clone --depth 1 https://github.com/antimatter15/splat.git SplatPack/experiments/external/splat
```

## Baseline Matrix

| Label | Renderer | Role | Platform |
| --- | --- | --- | --- |
| A | Existing ChimericAvatar/GVRM renderer | current local baseline | Unity/OpenXR |
| B | SplatPack v1 | proposed method | Unity/OpenXR/XREAL |
| C | UnityGaussianSplatting | Unity renderer baseline | PC Unity, some VR |
| D | SuperSplat Viewer | web runtime reference | Browser/WebGL/WebGPU |
| E | Official graphdeco viewer | canonical 3DGS reference | PC |

## What To Measure

Use the same scene where possible.

```text
input file size
compiled asset size
splat count
chunk count
load time
GPU memory
CPU frame time
GPU frame time
FPS
XR stereo mode
eye resolution
visual artifacts
```

## Decision Rule

Do not optimize against every external renderer at once.

Current decision rule:

```text
If SplatPack cannot load and render its own .splatpack reliably,
do not spend time porting external baselines.

If SplatPack renders reliably,
compare first against UnityGaussianSplatting and the existing GVRM renderer.

Only after that, use SuperSplat/SOG as a reference for compression and LOD.
```
