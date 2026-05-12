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

The first compiler version writes uncompressed float32 splats plus chunk
metadata. Later passes should add quantized attributes, LOD levels, and sort
metadata.
