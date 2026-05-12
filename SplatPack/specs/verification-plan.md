# SplatPack Verification Plan

This plan is for early SplatPack validation while the package still lives in
the ChimericAvatar repository.

## 1. Build The Compiler

From the repository root:

```powershell
dotnet build SplatPack/compiler/SplatPack.Compiler.csproj
```

Expected result:

```text
Build succeeded.
0 warnings
0 errors
```

## 2. Compile A 3DGS PLY

Use a standard binary little-endian 3DGS `.ply`.

```powershell
dotnet run --project SplatPack/compiler/SplatPack.Compiler.csproj -- `
  C:\path\to\input.ply `
  C:\path\to\output.splatpack `
  --chunk-size 4096
```

Expected output:

```text
SplatPack compiled: splats=..., chunks=..., output=...
```

For the first pass, the compiler supports:

```text
x y z
f_dc_0 f_dc_1 f_dc_2
opacity
scale_0 scale_1 scale_2
rot_0 rot_1 rot_2 rot_3
```

`f_rest_*` is parsed but not yet written into the runtime v1 payload.

## 3. Add The Unity Package

In Unity Package Manager:

```text
Add package from disk
  -> SplatPack/unity/com.chimera.splatpack/package.json
```

Then copy or generate a `.splatpack` file under the Unity project's `Assets/`
folder. The package importer should create a `SplatPackAsset`.

## 4. Create A Runtime Scene

Create an empty GameObject and add:

```text
SplatPackRenderer
```

Assign:

```text
Asset:
  imported SplatPackAsset

Projection Compute:
  SplatPackProjection.compute

Splat Material:
  optional material using shader "Chimera/SplatPack Gaussian Splat"
```

If no material is assigned, `SplatPackRenderer` tries `Shader.Find`.

## 5. Expected Runtime Logs

On load:

```text
[SplatPack] Loaded package: splats=..., chunks=...
```

On draw:

```text
[SplatPack] Draw submitted:
phase=srp-end
stereo=...
stereoEye=...
xrEnabled=...
xrActive=...
projection=...ms-cpu/1eye or ...ms-cpu/2eye
splats=...
chunks=...
vertices=splats*6
```

For XR, the first success target is:

```text
projection=.../2eye
vertices=splats*6
```

The runtime should not multiply vertices by two for stereo. Unity XR should
route views; the projected cache stores per-eye data.

## 6. Minimum Experiment Matrix

Use the same `.ply`, headset, Unity quality settings, and render resolution for
all rows.

| Condition | Asset | Runtime | Expected Purpose |
| --- | --- | --- | --- |
| A | Raw PLY / existing renderer | Existing path | Baseline |
| B | SplatPack v1 | Full splats, projected cache | Runtime architecture test |
| C | SplatPack v1 | Smaller chunk size | Chunk metadata overhead test |
| D | SplatPack v2 later | Chunk culling + LOD | Standalone XR target |

Record:

```text
device
Unity version
graphics API
XR provider
stereo mode
eye resolution
splat count
chunk count
projection CPU ms
GPU frame ms
CPU frame ms
FPS
memory usage
visual artifacts
```

## 7. Current Pass/Fail Criteria

Pass:

```text
compiler builds
.ply compiles to .splatpack
Unity imports .splatpack
renderer logs load and draw
non-XR view renders one model
XR view does not show duplicate models
```

Fail:

```text
Unity shader compile error
compute buffer stride mismatch
black/blank render
two duplicated models in XR
left/right eye projection mismatch
projection CPU time spikes above frame budget
```

## 8. First Notes To Capture

For each `.ply`, save:

```text
file name
input file size
compiled file size
splat count
chunk size
chunk count
compile time
load time
first draw log
headset screenshot if possible
```
