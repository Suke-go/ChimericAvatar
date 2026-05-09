# Schema Package

このpackageは、Dashboard、Backend、Unity clientが共有する契約を置く場所です。

Current schemas:

- `runtime-manifest.schema.json`
- `runtime-manifest.example.json`
- `gvrm-metadata.schema.json`

検証方針:

```text
runtime-manifest.example.json must validate against runtime-manifest.schema.json.
Unity DTO must deserialize runtime-manifest.example.json.
Backend manifest publisher must emit only schema-valid JSON.
GVRM loader must validate data.json against gvrm-metadata.schema.json before rendering.
Runtime manifest profile values are beginner/master/professional.
Runtime manifest language values are ja/en.
Runtime manifest poster format is A0 portrait or A0 landscape.
```

CI command:

```text
pnpm --filter @chimera/schema check
```
