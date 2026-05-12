# Migration Map From ChimericAvatar GVRM

SplatPack is not a GVRM package. The GVRM code is used as a reference for
runtime kernels only.

## Moved Or Reimplemented

```text
GVRM projected splat cache
  -> SplatPackProjection.compute

GVRM lightweight quad expansion
  -> SplatPackGaussianSplat.shader

GVRM runtime buffer layout
  -> SplatPackSplat float4 layout

GVRM XR viewport/matrix handling
  -> SplatPackRenderer projection dispatch
```

## Not Moved

```text
VRM loading
GVRM zip handling
bone remapping
vertex skinning cache
motion smoke test
avatar validation controls
GVRM metadata transforms
```

## New SplatPack Work

```text
.ply parser in compiler
.splatpack binary writer
spatial chunk builder
Unity package layout
Unity compiler launcher
generic .ply runtime loader
```

## Next Required Work

```text
chunk frustum culling
chunk LOD selection
attribute quantization
SH-rest payload support
GPU timing logs
Quest/XREAL benchmark scene
```
