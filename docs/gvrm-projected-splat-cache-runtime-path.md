# GVRM Projected Splat Cache Runtime Path

This note tracks the experimental runtime path for redundancy-aware GVRM Gaussian splat rendering.

## Runtime Paths

- `ReferenceVertexShader`: keeps projection, covariance, ellipse solve, and SH evaluation in the vertex shader.
- `VertexSkinningCache`: keeps the current vertex-factorized skinning cache and uses the reference vertex-shader projection path.
- `ProjectedSplatCache`: dispatches a compute pass that evaluates view-dependent projected splat data once per splat, then expands lightweight quad corners in the vertex shader.

The existing shader path is retained and selected whenever the projected cache is disabled or unavailable.

## Current Path C Scope

The first implementation intentionally avoids a full `StaticSplatBuffer -> DynamicSplatBuffer` refactor. It uses the existing dynamic procedural splat buffer as the projection input:

```text
splatBuffer / DynamicSplatBuffer
  -> GvrmProceduralSplatProjection.compute
  -> projectedSplatBuffer
  -> GvrmProceduralGaussianSplat.shader lightweight expansion
```

This keeps the patch reversible and isolates the first risk to view-dependent projection.

## Cached Terms

`GvrmProceduralSplatProjection.compute` moves these per-quad-vertex operations to a per-splat compute pass:

- world/view/clip projection
- covariance projection
- 2D eigen solve
- screen-space ellipse axes
- antialias compensation
- view-dependent SH color evaluation

The shader still computes Gaussian falloff per fragment.

## XR Stereo Cache Layout

The projected cache buffer is allocated with two eye slots:

```text
ProjectedSplatBuffer[0 * splatCount + splatId] = left/mono projection
ProjectedSplatBuffer[1 * splatCount + splatId] = right projection
```

Mono and Multi Pass rendering populate slot 0 for the current camera/eye. Single Pass stereo populates both slots before the draw and the shader selects the slot with `unity_StereoEyeIndex`.

## Diagnostics

Renderer diagnostics include:

- `path=<runtimePath>`
- `skinning=gpu-vertex-cache/<vertexCount>`
- `projection=gpu-cache/<dispatchCpuMs>ms-cpu/<eyeCount>eye` or `projection=vertex-shader`
- splat count and draw vertex count

The current projection timing is CPU dispatch/enqueue time, not GPU timestamp timing. Real GPU timing should be added before reporting paper numbers.

## Validation

For paper-quality validation, compare:

| Path | Skinning | Projection | Approximation |
| --- | --- | --- | --- |
| A | current renderer setting | per quad vertex | no |
| B | per source vertex | per quad vertex | no |
| C | per source vertex | per splat per eye | no |

Required checks:

- visual parity between reference projection and projected-cache projection
- left/right eye agreement in Multi Pass and Single Pass XR
- no fallback warnings from `GvrmProceduralSplatProjection`
- GPU timestamp timing before final quantitative claims
