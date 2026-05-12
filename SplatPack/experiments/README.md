# SplatPack Experiments

This directory is for reproducible SplatPack experiments. External renderers,
datasets, and generated results are intentionally ignored by git.

```text
external/
  optional cloned baseline repositories

data/
  local PLY/SPZ/SOG/splat files

results/
  logs, screenshots, profiler exports, and benchmark tables
```

The current priority is to complete and validate the SplatPack proposed method:

```text
PLY
  -> SplatPack compiler
  -> chunked .splatpack
  -> Unity runtime
  -> projected splat cache
  -> XR draw
```

External repositories should be used only for baselines and implementation
reference. Do not introduce a hard runtime dependency on them.

## Recommended Local Layout

```text
SplatPack/experiments/
  external/
    UnityGaussianSplatting/
    gaussian-splatting/
    supersplat/
    supersplat-viewer/
    splat/

  data/
    garden/
      point_cloud.ply
      point_cloud.splatpack

  results/
    YYYYMMDD-xreal/
      notes.md
      logs.txt
      screenshots/
```

## First Experiment

1. Pick one medium-sized PLY.
2. Compile with SplatPack chunk size 4096.
3. Render in Unity non-XR.
4. Render in XREAL/OpenXR.
5. Record the first load log and draw log.

The first pass is not a performance paper yet. It verifies correctness and
establishes the minimum benchmark harness.
