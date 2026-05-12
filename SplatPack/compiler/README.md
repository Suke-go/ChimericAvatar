# SplatPack Compiler

Local CLI compiler for converting standard 3DGS `.ply` files into `.splatpack`
runtime packages.

```powershell
dotnet run --project SplatPack/compiler -- input.ply output.splatpack
```

Optional:

```powershell
dotnet run --project SplatPack/compiler -- input.ply output.splatpack --chunk-size 4096
```

The default target is `standalone-xr`. It emits uncompressed float32 splats plus
chunk metadata, but already applies compiler-side safety passes:

- drops invalid splats
- prunes near-zero opacity splats
- caps extreme Gaussian axis lengths using a percentile budget
- writes a JSON build report next to the package

Use `--target reference` when you need an unpruned reference package.

Useful options:

```powershell
dotnet run --project SplatPack/compiler -- input.ply output.splatpack `
  --target standalone-xr `
  --opacity-prune 0.002 `
  --max-axis-percentile 99 `
  --max-axis-multiplier 1.1
```

Later passes should add quantized attributes, LOD levels, and richer sort
metadata.
