# SplatPack Binary Format v1

The first format is intentionally simple. It stores float32 runtime splats plus
chunk metadata. Compression and multi-LOD payloads should be added after the
runtime path is stable.

All values are little-endian.

## Header

```text
char[4] magic = "SPK1"
int32   version = 1
int32   splatCount
int32   chunkCount
int32   splatStride = 96
int32   chunkStride = 44
float3  boundsMin
float3  boundsMax
```

## Chunk

```text
float3 boundsMin
float3 boundsMax
int32  splatOffset
int32  splatCount
int32  lodLevel
```

Chunks are ordered by the compiler's spatial key. The first version uses one
LOD level only.

## Splat

Runtime splat layout matches the Unity shader and compute kernel.

```text
float4 centerWS
float4 axis0WS
float4 axis1WS
float4 axis2WS
float4 color
float4 meta
```

`axis0WS`, `axis1WS`, and `axis2WS` are world-space covariance axes after
decoding PLY log-scale and quaternion rotation.

`color.rgb` is DC color decoded as:

```text
rgb = clamp(0.5 + SH_C0 * f_dc, 0, 1)
```

`color.a` is opacity decoded with sigmoid.

## Planned v2 Extensions

- Chunk-local quantized positions.
- 16-bit scale.
- Compressed quaternion rotation.
- Per-chunk SH degree.
- LOD payload offsets.
- Sort proxy metadata.
- Optional chunk visibility metadata.
