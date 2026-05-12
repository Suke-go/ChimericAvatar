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

## Fetch Smoke Assets

Use the smoke set first:

```powershell
SplatPack/experiments/fetch-samples.ps1 -Set Smoke -Compile
```

This downloads a small PLY into `experiments/data/` and compiles it to
`.splatpack`. The data directory is ignored by git.

For local Unity validation, stage the compiled asset under:

```text
ChimericAvatar/Assets/SplatPackSamples/External/
```

Then use `Tools > SplatPack > Create Viewer From First Sample` in Unity. This
menu searches `Assets/SplatPackSamples` and creates a configured viewer from the
first imported `.splatpack` asset it finds.

## Export Compiler Report Metrics

After compiling one or more assets, turn the generated
`.splatpack.report.json` files into a CSV table:

```powershell
SplatPack/experiments/export-report-metrics.ps1
```

By default this scans `SplatPack/experiments/data/` and writes:

```text
SplatPack/experiments/results/report-metrics.csv
```

The CSV is the minimum compiler-side validation table for SplatPack proposed
method experiments. It includes:

```text
input/emitted splats
chunk count and chunk fill stats
axis length cap and emitted axis length stats
axis ratio cap and emitted axis ratio stats
axis clamp / axis-ratio clamp counts
package bytes, uncompressed memory bytes, runtime vertices
```

For a specific report or directory:

```powershell
SplatPack/experiments/export-report-metrics.ps1 `
  -ReportPath SplatPack/experiments/data/smoke/j0n45_point_cloud.splatpack.report.json `
  -OutputPath SplatPack/experiments/results/smoke-report-metrics.csv `
  -PassThru
```

Use this CSV as the first row in any runtime validation note. Add Unity frame
time, FPS, visual artifacts, and XR stereo observations beside the compiler
metrics once the scene has been rendered.

## Record Runtime Observations

Use `runtime-observation-template.csv` as the hand-entry sheet for Unity runs.
Copy it into a result folder, then fill one row per observed condition:

```powershell
Copy-Item `
  SplatPack/experiments/runtime-observation-template.csv `
  SplatPack/experiments/results/runtime-observations.csv
```

Runtime columns are meant for direct log/profiler transcription:

```text
asset, condition, headset/editor, render_api, xr_enabled, stereo_mode
splats, chunks, vertices
projection_path, sort_path, projection_cpu_ms, sort_cpu_ms
fps, cpu_frame_ms, gpu_frame_ms
visual_notes, artifacts, pass_fail
```

Suggested condition labels match the verification matrix:

```text
A_raw_baseline
B_splatpack_full_projected_cache
C_splatpack_smaller_chunk_size
D_splatpack_lod_culling_future
```

For A/B/C/D comparison, keep the `asset` value identical to the compiler
metrics `asset` column, then join or compare the tables in a spreadsheet:

```text
compiler metrics CSV
  report-metrics.csv
runtime observations CSV
  runtime-observations.csv
comparison key
  asset + condition
```

Use compiler metrics for package-side facts such as splats, chunks, axis clamps,
axis-ratio clamps, memory, and runtime vertices. Use runtime observations for
Unity-side facts such as projection/sort paths, CPU/GPU frame time, FPS, stereo
mode, visual artifacts, and pass/fail.

Optional PowerShell check after filling observations:

```powershell
$compiler = @{}
Import-Csv SplatPack/experiments/results/report-metrics.csv |
  ForEach-Object { $compiler[$_.asset] = $_ }

Import-Csv SplatPack/experiments/results/runtime-observations.csv |
  Where-Object asset |
  ForEach-Object {
    [pscustomobject]@{
      asset = $_.asset
      condition = $_.condition
      emitted_splats = $compiler[$_.asset].emitted_splats
      chunks = $(if ($_.chunks) { $_.chunks } else { $compiler[$_.asset].chunk_count })
      vertices = $(if ($_.vertices) { $_.vertices } else { $compiler[$_.asset].runtime_vertices })
      projection_path = $_.projection_path
      sort_path = $_.sort_path
      fps = $_.fps
      cpu_frame_ms = $_.cpu_frame_ms
      gpu_frame_ms = $_.gpu_frame_ms
      pass_fail = $_.pass_fail
      artifacts = $_.artifacts
    }
  } | Export-Csv SplatPack/experiments/results/ab-comparison.csv -NoTypeInformation
```
