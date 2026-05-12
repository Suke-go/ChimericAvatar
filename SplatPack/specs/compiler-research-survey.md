# Compiler Research Survey For SplatPack

This note frames SplatPack as an asset compiler for standalone XR rendering, not
as only a `.ply` converter. The useful analogy is less "a new 3DGS renderer" and
more "a platform-aware lowering pipeline from raw Gaussian scenes to an
XR-executable representation."

## Core Thesis

3DGS-specific compiler work is still thin. The closest mature patterns come from:

- image/tensor compilers: separate intent from schedule and hardware mapping
- sparse visual-computing systems: encode spatial sparsity explicitly
- game asset compilers: move expensive representation choices offline
- virtualized geometry: cluster, stream, and select bounded work per frame
- 3DGS compression/LOD: prune, quantize, cluster, and budget attributes

For SplatPack, the compiler should therefore own the decisions that are expensive
or unstable on standalone HMDs:

- spatial partitioning
- chunk order and draw grouping
- LOD construction
- attribute quantization
- SH degree selection
- sort/cull metadata
- platform profile selection

The runtime should only execute a small number of predictable kernels.

## Direct 3DGS References

### Base Representation

The original 3D Gaussian Splatting work established explicit anisotropic
Gaussians with visibility-aware splatting as a real-time radiance-field
representation.

SplatPack implication:
keep compatibility with standard 3DGS `.ply`, but do not preserve its storage
layout as the runtime layout.

Source: https://arxiv.org/abs/2308.04079

### Compression

LightGaussian combines significance-based pruning, SH distillation, and vector
quantization. CompGS uses vector quantization and opacity regularization to
reduce both storage and render cost. HAC uses spatial context and adaptive
quantization for entropy coding.

SplatPack implication:
compression should be tied to runtime budgets, not just file size. A useful pass
is `estimate_contribution -> assign_attribute_budget -> quantize`.

Sources:

- https://arxiv.org/abs/2311.17245
- https://www.ecva.net/papers/eccv_2024/papers_ECCV/papers/04735.pdf
- https://arxiv.org/abs/2403.14530

### LOD And Hierarchy

Octree-GS, hierarchical 3DGS, and Virtualized 3D Gaussians all point toward
hierarchical representations rather than a flat splat array. Spark 2.0 is a
useful systems reference because it describes a streamable LoD splat tree with a
fixed per-frame splat budget.

SplatPack implication:
the compiler should eventually emit a tree or DAG, but the first research-grade
step can be chunked LOD: full, half, quarter per chunk plus screen-error metadata.

Sources:

- https://arxiv.org/abs/2403.17898
- https://arxiv.org/abs/2406.12080
- https://arxiv.org/abs/2505.06523
- https://www.worldlabs.ai/blog/spark-2.0

### Mobile And XR-Oriented 3DGS

RTGS and Mobile-GS focus on the deployment constraint rather than only fidelity.
RTGS is especially relevant because it treats pruning and foveated rendering as a
system-level route to real-time mobile performance.

SplatPack implication:
the research target should be stable frame time under HMD constraints. The
compiler should produce a package for a target profile such as `quest-balanced`,
`quest-quality`, `xreal-preview`, or `desktop-reference`.

Sources:

- https://huggingface.co/papers/2407.00435
- https://arxiv.org/abs/2603.11531

## Analogous Compiler And Runtime Systems

### Halide / TVM

Halide separates algorithm from schedule. TVM exposes graph/operator
optimization and maps programs to diverse hardware backends.

SplatPack implication:
define a small SplatPack IR where the scene meaning is separate from the runtime
schedule. The same input scene can lower into different schedules:

- desktop reference: high SH, exact sort, larger radii
- standalone XR: bucket sort, capped radii, lower SH, chunk LOD
- web/mobile: stronger quantization, progressive streaming

Sources:

- https://users.cs.duke.edu/~lkw34/papers/halide-pldi2013.pdf
- https://arxiv.org/abs/1802.04799

### Taichi And Sparse Visual Computing

Taichi's useful idea is decoupling computation from sparse data structures, then
using compiler knowledge of sparsity and locality to generate efficient CPU/GPU
code.

SplatPack implication:
Gaussian scenes are sparse and irregular. A compiler pass should convert a loose
point cloud into a structured sparse representation: chunks, pages, bins, and
possibly a hierarchy.

Source: https://yuanming.taichi.graphics/publication/2019-taichi/

### Mesh Asset Compilers

Draco, meshoptimizer, and `EXT_meshopt_compression` are strong models for
offline preprocessing. They optimize order, locality, quantization, and decoding
speed so that runtime loading can go straight into GPU-friendly buffers.

SplatPack implication:
`.splatpack` should become a GPU-ready delivery format: per-buffer compression,
local coordinate quantization, attribute filters, and chunk-local ordering.

Sources:

- https://github.com/google/draco
- https://github.com/zeux/meshoptimizer
- https://github.com/KhronosGroup/glTF/blob/main/extensions/2.0/Vendor/EXT_meshopt_compression/README.md

### Virtualized Geometry / Nanite

Nanite is not directly transferable because Gaussians are transparent and need
sorting, but the systems idea is exactly relevant: internal compressed format,
cluster hierarchy, fine-grained streaming, automatic LOD.

SplatPack implication:
the novelty should be "virtualized splat assets for XR", with transparency and
stereo constraints as the key differences from mesh virtualization.

Sources:

- https://dev.epicgames.com/documentation/unreal-engine/nanite-virtualized-geometry-in-unreal-engine
- https://advances.realtimerendering.com/s2021/Karis_Nanite_SIGGRAPH_Advances_2021_final.pdf

## Proposed SplatPack Compiler IR

Input IR:

- raw Gaussians from `.ply`
- positions, scale, rotation, opacity, SH
- training/export metadata when present

Scene IR:

- normalized coordinate system
- bounds and density statistics
- per-splat contribution estimates
- spatial chunks
- chunk histograms: opacity, radius, depth variance, SH energy

Lowered XR IR:

- chunk-local positions
- quantized attributes
- selected SH degree
- LOD offsets
- sort bins or chunk sort metadata
- runtime draw pages
- target profile metadata

Runtime IR:

- GPU buffer layout
- compute dispatch parameters
- shader feature flags
- budget counters for diagnostics

## Recommended Pass Order

1. Parse `.ply` and normalize conventions.
2. Build spatial chunks.
3. Estimate splat contribution and screen footprint distributions.
4. Select chunk LOD levels.
5. Assign attribute budgets.
6. Quantize into chunk-local buffers.
7. Build sort/cull metadata.
8. Emit target profile package.
9. Validate with desktop reference screenshots and HMD frame metrics.

## Research Gap To Claim

Many papers optimize 3DGS quality, compression, or LOD. Fewer treat the problem
as a platform-aware asset compilation pipeline for standalone XR. SplatPack can
claim the integration point:

raw 3DGS scene -> offline compiler -> XR-ready splat package -> simple Unity/OpenXR runtime

The research question becomes:

Which offline representation and lowering passes make 3DGS predictable enough
for standalone HMD rendering while preserving acceptable image quality?

## Next Implementation Priorities

Short term:

- add compiler-side statistics report per package
- add chunk visibility counters to runtime logs
- add GPU bucket sort and disable CPU exact sort by default
- add chunk-level frustum culling

Medium term:

- chunk-local 16-bit position quantization
- opacity and scale quantization
- RGB-only / low-SH modes per chunk
- full / half / quarter LOD streams

Long term:

- hierarchical splat tree or DAG
- async chunk streaming
- HMD profile autotuning
- optional foveated LOD
