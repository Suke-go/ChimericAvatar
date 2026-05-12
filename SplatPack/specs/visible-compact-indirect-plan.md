# Visible Compact + Indirect Draw Plan

Owner: Worker F  
Scope: design memo only. Runtime, shader, compiler, and experiments are not changed by this note.

## Purpose

Move the SplatPack runtime from "draw every splat and discard invalid projected splats in the vertex/fragment stages" to a GPU-visible compact path that draws only the visible set through `DrawProceduralIndirect`.

The immediate goals are:

- Reduce vertex shader and blend pressure by avoiding `splatCount * 6` submission when many splats are offscreen.
- Keep the existing projected-splat cache model, including the per-eye cache used by XR.
- Make the stereo visible set deterministic: one compacted left-right union list for single-pass stereo, and one visible list for mono or multi-pass eye rendering.
- Sort only the visible set once the compacted path is stable.
- Preserve the existing CPU chunk/splat sort and full-draw paths as fallback until the indirect path is validated on target devices.

## Current Runtime Shape

`SplatPackRenderer` currently allocates and binds:

- `_Splats`: full `SplatPackSplat` buffer, one entry per package splat.
- `_DrawOrder`: full `uint` buffer, one entry per package splat.
- `_ProjectedSplats`: `SplatPackProjectedSplat` buffer with `splatCount * MaxProjectedEyes` entries.
- GPU depth bucket sort buffers: `_BinCounts` and `_BinOffsets`, both sized to `depthSortBinCount`.

The draw path always submits:

```text
vertexCount = package.SplatCount * 6
instanceCount = ResolveProceduralDrawInstanceCount(camera) == 1
```

The vertex shader derives:

```text
drawIndex = SV_VertexID / 6
splatIndex = _DrawOrder[drawIndex]
projected = _ProjectedSplats[eyeIndex * eyeStride + splatIndex]
```

When `projected.meta.z < 0.5`, the shader writes the vertex out of view. This
keeps correctness simple, but invisible splats still consume draw vertices.

`SplatPackProjection.compute` already rejects splats behind the camera or
outside a relaxed clip extent and marks invalid entries with `meta.z = 0`.
For stereo, `SplatPackRenderer` dispatches the same projection kernel twice:

```text
left  -> _ProjectedSplats[0..splatCount)
right -> _ProjectedSplats[splatCount..splatCount*2)
```

`SplatPackDepthSort.compute` currently buckets all splats by a single camera
forward depth, then fills the full `_DrawOrder`. It does not consume projection
visibility.

## Target Shape

The target frame flow is:

```text
Project per eye
  -> write _ProjectedSplats as today
  -> write per-splat visible flags and sort depth

Compact visible set
  -> produce _VisibleDrawOrder, one splat index per visible splat
  -> write indirect args vertex count = visibleCount * 6

Sort visible set
  -> reorder _VisibleDrawOrder by selected depth policy

DrawProceduralIndirect
  -> shader reads drawIndex from compacted visible order
  -> shader still selects projected cache by unity_StereoEyeIndex
```

The compacted list must contain original splat indices, not projected-cache
indices. This keeps the shader's eye-specific projected lookup unchanged:

```text
projected = _ProjectedSplats[eyeIndex * eyeStride + splatIndex]
```

## XR Policy

### Mono

Use one projection dispatch and one compact list:

```text
visible if projected[0].meta.z >= 0.5
sortDepth = projected[0].meta.x or view-space depth
```

The indirect draw count is the mono visible count.

### XR Single-Pass

Use the left-right union visible set:

```text
visible if leftValid || rightValid
visibleMask bit 0 = leftValid
visibleMask bit 1 = rightValid
sortDepth = stereo depth policy described below
```

Each visible splat is emitted once into `_VisibleDrawOrder`. The single draw is
submitted with `visibleUnionCount * 6` vertices. The shader uses
`unity_StereoEyeIndex` to fetch the correct projected entry. If a splat is only
visible in the other eye, the existing `projected.meta.z < 0.5` branch hides it
for the current eye.

This favors correctness and stable stereo over per-eye minimum draw count. It
also avoids drawing the same splat twice in single-pass stereo.

### XR Multi-Pass

Prefer one compact list per eye when Unity invokes separate eye passes:

```text
left pass  -> left visible list and left sort depth
right pass -> right visible list and right sort depth
```

If the runtime cannot reliably identify the active multi-pass eye from the
camera callback, fall back to the same left-right union list used by single-pass.
The fallback costs extra vertices in each eye but prevents missing edge splats.

### Left-Right Union Visible Set

The union set should be built from flags, not from two append buffers, to avoid
deduplication cost:

```text
Projection left  sets _Visibility[splat].mask |= 1
Projection right sets _Visibility[splat].mask |= 2
Compact scans one _Visibility entry per splat and emits once when mask != 0
```

Because the current renderer dispatches left and right projection sequentially,
both eyes may write the same `_Visibility` entry safely with simple assignment
if each eye writes a distinct bit field through a per-eye pass. If projection is
later merged into one kernel that handles both eyes, use atomic OR for the mask.

### Sort Depth

Depth sorting must be stable enough for transparent splats while avoiding eye
divergence.

Recommended policy:

- Mono: sort by the projected/view depth for the mono eye.
- XR single-pass: sort by center-eye depth when available, otherwise use
  `max(leftDepth, rightDepth)` in back-to-front space.
- XR multi-pass per-eye: sort by the active eye depth.
- XR multi-pass union fallback: use the same single-pass stereo policy.

The center-eye depth can be computed from `camera.transform.position` and
`camera.transform.forward`, matching the current GPU depth bucket sort. This
keeps one ordering shared by both eyes and reduces stereo shimmer. The projected
cache still determines per-eye position and validity.

## Required Buffers

Add these runtime buffers behind a feature flag:

```text
_Visibility
  stride: 16 bytes suggested
  fields:
    uint mask
    uint reserved0
    float sortDepth
    uint reserved1
  count: splatCount

_VisibleDrawOrder
  stride: 4 bytes
  count: splatCount
  contents: original splat indices, compacted to [0, visibleCount)

_VisibleCount
  stride: 4 bytes
  count: 1
  contents: append/count cursor

_IndirectArgs
  type: IndirectArguments
  count: 4 uints for DrawProceduralIndirect
  fields:
    vertexCountPerInstance = visibleCount * 6
    instanceCount = 1
    startVertex = 0
    startInstance = 0

_VisibleBinCounts
_VisibleBinOffsets
  same role as current sort buffers, sized to depthSortBinCount
  used only by visible depth-bucket sort
```

Optional later buffers:

```text
_VisibleDepthKeys
  one packed sortable key per visible entry if bucket sort is replaced

_EyeVisibleDrawOrder[2]
_EyeIndirectArgs[2]
  only if multi-pass per-eye submission becomes a first-class path
```

The projected buffer remains `splatCount * eyeCount`. It should not be compacted
in the first migration because the shader can cheaply map a compacted draw index
back to a stable original splat index.

## Compute Passes

### Phase 0: Feature Flag And Diagnostics Only

Add a renderer mode such as:

```text
FullDraw
VisibleCompactDebug
VisibleCompactIndirect
```

`FullDraw` remains the default. `VisibleCompactDebug` runs projection and compact
but still draws the full path, allowing visible counts and validation readbacks
without changing rendering.

### Phase 1: Projection Writes Visibility

Extend projection with a visibility output. Keep the existing `ProjectedSplat`
write unchanged.

Per projection dispatch:

```text
ClearVisibility once per frame before the first eye.
ProjectSplats eye 0:
  write _ProjectedSplats[0 * stride + splat]
  if valid: set mask bit 0 and write candidate depth
ProjectSplats eye 1:
  write _ProjectedSplats[1 * stride + splat]
  if valid: set mask bit 1 and update candidate depth
```

For sequential eye dispatch, depth updates can be deterministic:

```text
single-pass stereo sortDepth = center-eye depth from a shared parameter
per-eye sortDepth = current eye depth
union fallback = max(leftDepth, rightDepth) or center depth
```

If the kernel cannot know whether it is the first or second eye, pass:

```text
_ProjectedEyeIndex
_VisibleMaskBit
_StereoSortDepthMode
```

### Phase 2: Compact Visible

Add a compact compute pass:

```text
ClearVisibleCount:
  _VisibleCount[0] = 0

CompactVisible:
  for splat in [0, splatCount):
    if _Visibility[splat].mask != 0:
      slot = atomicAdd(_VisibleCount[0], 1)
      _VisibleDrawOrder[slot] = splat
```

Then write indirect args:

```text
BuildIndirectArgs:
  visibleCount = _VisibleCount[0]
  _IndirectArgs[0] = visibleCount * 6
  _IndirectArgs[1] = 1
  _IndirectArgs[2] = 0
  _IndirectArgs[3] = 0
```

This first compact pass can be unordered. Use it only with sort disabled or for
debug count validation.

### Phase 3: Visible Depth-Bucket Sort

Move the current GPU depth bucket sort from full splat count to visible count.

Two implementation choices:

1. Count bins by compacted list:

```text
for visibleSlot in [0, visibleCount):
  splat = _VisibleDrawOrder[visibleSlot]
  bin = ResolveDepthBin(splat or _Visibility[splat].sortDepth)
  atomicAdd(_VisibleBinCounts[bin], 1)
```

2. Count bins from all splats but only when visible:

```text
for splat in [0, splatCount):
  if _Visibility[splat].mask != 0:
    bin = ResolveDepthBin(splat)
    atomicAdd(...)
```

Choice 1 is better after compaction because it scales with visible count.
Choice 2 is simpler for the first implementation because it avoids reading
`_VisibleCount` during dispatch planning.

The fill pass writes sorted visible order into a second order buffer:

```text
_VisibleDrawOrderUnsorted -> _VisibleDrawOrderSorted
```

or ping-pongs a pair of visible order buffers. Avoid overwriting the source
while fill order is still being read.

### Phase 4: DrawProceduralIndirect

Update the draw command only when compact validation is passing:

```text
DrawProceduralIndirect(
  Matrix4x4.identity,
  material,
  0,
  MeshTopology.Triangles,
  _IndirectArgs,
  0,
  propertyBlock)
```

Bind `_DrawOrder` to the compacted visible order buffer. The shader can keep the
same name at first, minimizing shader changes:

```text
propertyBlock.SetBuffer("_DrawOrder", visibleDrawOrderBuffer)
```

The shader's `drawIndex = vertexID / 6` remains valid because indirect vertex
count is already `visibleCount * 6`.

### Phase 5: Cache And Reuse

The current projection cache is keyed by camera transform, renderer transform,
viewport, stereo state, and quality parameters. Visible compact cache must add:

```text
visibleCount
indirect args buffer content
visibility/sort buffers are valid for the cached projection
sort depth policy
```

If projection is reused, compact and indirect args can be reused too, provided
the selected sort mode and sort interval also allow reuse. Treat projection
cache invalidation as invalidating the visible compact cache.

## Renderer Integration Notes

Add resources lazily, following the current `EnsureGpuSortResources` pattern:

- Allocate visible buffers after package load.
- Reallocate on package reload or mode change.
- Release visible buffers in `ReleaseBuffers`.
- Fall back to full draw if any required buffer or kernel is unavailable.

Ordering in `TryPrepare` should become:

```text
RecenterIfNeeded
DispatchProjectedSplatsAndVisibility
CompactVisible
UpdateVisibleDrawOrder
BuildIndirectArgs
Bind material buffers
```

During migration, keep current `UpdateDrawOrder` for full draw fallback. The
visible path should not mutate the full `_DrawOrder` until it is the default.

## Shader Integration Notes

The shader can stay close to its current shape:

```text
drawIndex = input.vertexID / 6
splatIndex = _DrawOrder[drawIndex]
projected = _ProjectedSplats[projectedEyeIndex * projectedEyeStride + splatIndex]
```

No per-eye compact buffer is needed for single-pass stereo. The union draw list
may include splats invalid for the current eye; the current invalid projected
branch remains the safety net.

Later shader cleanup can rename `_DrawOrder` to `_VisibleDrawOrder`, but that is
not needed for the first indirect migration.

## Logging Items

Extend the existing `[SplatPack] Draw submitted` line with compact fields:

```text
draw=full|visible-indirect|visible-debug|fallback
visible=12345/67890
visiblePct=18.2
visibleMask=mono|left|right|lr-union
compact=dispatch|cache|off
compactCpuMs=...
indirectArgs=vertices/74070,instances/1
sort=visible-gpu-depth-bucket/8192|visible-unsorted|full-gpu-depth-bucket|...
sortDepth=mono|center-eye|max-eye|active-eye
xrMode=mono|single-pass|multi-pass|multi-pass-union-fallback
fallbackReason=none|missing-kernel|missing-buffer|unsupported-api|readback-error
```

For debug builds or opt-in readback:

```text
visibleReadback=count/..., first/..., last/...
visibleInvalidInDrawOrder=...
leftVisible=...
rightVisible=...
unionVisible=...
leftOnly=...
rightOnly=...
bothEyes=...
```

Keep readback off by default. Visible count can be copied through async GPU
readback only in diagnostics mode.

## Verification Items

Minimum validation matrix:

| Case | Expected result |
| --- | --- |
| Mono, sort off, compact debug | Visible count matches projected valid count readback. |
| Mono, indirect, sort off | Visual output matches full draw except for expected overdraw reduction. |
| Mono, visible GPU sort | No large transparency regression versus current GPU depth bucket sort. |
| XR single-pass | One model, no stereo duplication, edge splats visible in either eye. |
| XR single-pass head turn | Union visible count changes smoothly, no per-eye popping at frustum edges. |
| XR multi-pass | Per-eye path works when active eye is reliable; union fallback has no missing splats. |
| Projection cache enabled | Stable camera logs `projection=cache` and compact path does not rebuild unnecessarily. |
| Projection cache invalidated | Camera movement, renderer movement, viewport change, and quality changes rebuild visible data. |
| GPU sort unavailable | Runtime falls back to full or unsorted compact path with one warning. |
| Zero visible splats | Indirect args vertex count is zero and draw produces no artifact. |
| All visible splats | Visible count equals splat count and output matches current full draw. |

Performance checks:

```text
visibleCount / splatCount
projection CPU dispatch cost
compact CPU dispatch cost
sort CPU dispatch cost
GPU frame time
CPU frame time
Quest/XREAL FPS
memory delta from new buffers
```

Correctness checks:

```text
No duplicate model in XR.
No missing left-only or right-only edge splats in single-pass.
No visible-count flicker while the camera is stationary.
No indirect args stale count after package reload.
No buffer stride mismatch on D3D11, Vulkan, and Android target API.
Transparent ordering is at least as stable as current depth-bucket sorting.
```

## Risks

- **Stereo sort compromise:** one shared order cannot be perfect for both eyes.
  Center-eye depth is the safest first choice; per-eye sorting should be limited
  to true multi-pass.
- **Union overdraw:** left-right union still draws right-only splats for the
  left eye and left-only splats for the right eye. The shader invalid branch
  preserves correctness, but visible count will be higher than per-eye compact.
- **Atomic compact order:** a simple atomic append produces nondeterministic
  order. Do not use it as the final transparent draw order; add visible sorting
  before making indirect the default.
- **Sort buffer overwrite:** visible sort needs a source and destination order
  buffer or a carefully staged fill. Overwriting the compact list while reading
  it can corrupt order.
- **Indirect args API support:** target graphics APIs must support procedural
  indirect draws and `ComputeBufferType.IndirectArguments`. Keep full draw
  fallback.
- **Cache staleness:** reusing projected data without reusing matching visible
  count and indirect args can draw stale entries. Tie compact cache validity to
  projection cache validity.
- **Multi-pass detection:** Unity XR callback behavior differs by pipeline and
  provider. If active eye is ambiguous, use union fallback rather than risking
  missing splats.
- **Depth range mismatch:** current GPU bucket range comes from renderer bounds.
  Visible sort should keep the same range initially to avoid changing sort
  behavior while changing draw submission.
- **Diagnostics readback cost:** visible count readback is useful but can stall.
  Keep it opt-in and async where possible.

## Recommended Rollout

1. Add feature flag and visible buffers, no rendering behavior change.
2. Extend projection to write visibility flags and debug visible counts.
3. Add compact pass and indirect args generation, still draw full path in debug.
4. Enable unsorted compact indirect only for sort-off test scenes.
5. Port GPU depth bucket sort to visible input and draw sorted compact order.
6. Enable XR single-pass union and validate headset behavior.
7. Add multi-pass per-eye path only after provider-specific eye detection is
   proven reliable.
8. Make visible indirect the default once fallback, logs, and validation matrix
   are passing on desktop and standalone XR.
