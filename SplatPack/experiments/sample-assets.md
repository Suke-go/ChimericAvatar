# Sample Assets For SplatPack Runtime Tests

The first goal is not to build a broad benchmark suite. The first goal is to
obtain a few `.ply` assets that exercise the SplatPack compiler and Unity
runtime reliably.

## Recommended Order

### 1. Smoke Test: J0N45/Gaussian-Splats

```text
https://huggingface.co/J0N45/Gaussian-Splats/blob/d6a58cf8fd2f889dea24fdf8eb6604204c058968/point_cloud.ply
```

Use this first because it is small enough for fast iteration.

```text
file: point_cloud.ply
size: about 8 MB
role: compiler smoke test and Unity runtime first render
license: not declared on the model page; use for local engineering validation only
```

Expected use:

```text
download
  -> compile to .splatpack
  -> import in Unity
  -> render non-XR
  -> render XREAL/OpenXR
```

### 2. CC0 Scaniverse Samples: WakuFactory

```text
https://www.wakufactory.jp/wxr/splats/sample.html
```

The page states the sample PLY files are unmodified Scaniverse exports and CC0.

Use these after the smoke test. They are useful for checking whether SplatPack's
PLY parser handles Scaniverse-style Gaussian PLY files.

```text
role: parser compatibility and XR viewer validation
license: CC0 according to source page
```

The fetch script currently uses:

```text
https://wakufactory.sakura.ne.jp/assets/ply/20240324_kadan1.ply
https://wakufactory.sakura.ne.jp/assets/ply/20240324_kaeru.ply
https://www.wakufactory.jp/wxr/splats/data/kitune1.ply
https://www.wakufactory.jp/wxr/splats/data/sakura1.ply
```

If the current compiler rejects these files, keep them as parser-expansion test
cases rather than blocking the first runtime validation.

### 3. Medium Baseline: camenduru/gaussian-splatting

```text
https://huggingface.co/camenduru/gaussian-splatting/blob/main/train/point_cloud/iteration_30000/point_cloud.ply
```

This is a larger reference output.

```text
size: about 266 MB
role: stress test after smoke assets work
```

Do not use this as the first test. It will slow iteration and hide simple
import/rendering bugs.

### 4. Dataset-Level Benchmark: Voxel51 gaussian_splatting

```text
https://huggingface.co/datasets/Voxel51/gaussian_splatting
```

Use this later for a more formal benchmark set.

```text
license: Apache-2.0 according to the dataset card
role: repeatable benchmark scenes
```

## Current Decision

For the current Unity Runtime-only phase:

```text
Use J0N45 point_cloud.ply first.
Then try WakuFactory CC0 samples.
Only then move to larger benchmark scenes.
```
