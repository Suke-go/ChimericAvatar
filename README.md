# ChimericAvatar

ChimericAvatar is an applied fork / derivative project built on top of
[naruya/gaussian-vrm](https://github.com/naruya/gaussian-vrm).

The original Gaussian-VRM project provides the `.gvrm` format, browser runtime,
and preprocessing reference implementation for binding Gaussian Splat avatars to
VRM skeletons. This repository extends that foundation into a production-oriented
Chimera Presenter stack: Next.js dashboard, FastAPI build backend, Supabase
session storage, Fly.io worker deployment, and Unity/XR runtime integration.

GitHub intentionally keeps this repository as a fork of `naruya/gaussian-vrm` so
the provenance is visible. Changes here are for ChimericAvatar and are not
automatically reflected back into the upstream repository.

Chimera Presenterの本番設計方針を管理するリポジトリです。

## Upstream Attribution

- Upstream: [naruya/gaussian-vrm](https://github.com/naruya/gaussian-vrm)
- Upstream license: MIT
- Vendored runtime/reference files: [apps/dashboard/lib/gvrm-vendor](apps/dashboard/lib/gvrm-vendor)
- Local focus: server-side `.gvrm` build pipeline, static web preview,
  Scaniverse PLY handling, and XR runtime assets

設計方針:

- [Chimera Presenter 本番設計方針](docs/production-design-policy.md)
- [技術詳細設計](docs/technical-blueprint.md)
- [Web実装詳細設計](docs/web-implementation-design.md)
- [Unity GVRM Runtime Logic](docs/unity-gvrm-runtime-logic.md)
- [検証計画](docs/verification-plan.md)
- [実装背景と参照文献](docs/implementation-background-and-references.md)
- [Evidence-Governed Knowledge設計](docs/knowledge-governance-design.md)
- [ADR 0001: Unity 6000 LTS Native GVRM Runtimeを採用する](docs/adr/0001-use-unity-native-gvrm-runtime.md)
- [ADR 0002: Next.js + FastAPI + Supabase + Unityを主要stackにする](docs/adr/0002-primary-application-stack.md)
- [ADR 0003: XREAL One Pro と Meta Quest 3 を co-target にする](docs/adr/0003-dual-target-xreal-and-meta-quest.md)
- [ADR 0004: Runtime credential exchange via QR + join token](docs/adr/0004-runtime-credential-exchange.md)

設計の要点は、`.gvrm`を共通アバター資産形式として採用し、OpenSourceの[Gaussian-VRM](https://github.com/naruya/gaussian-vrm)を資産生成・仕様参照に使い、XREAL本番アプリではUnity 6000 LTS上のGVRM Runtimeで動かすことです。

共有schema:

- [Runtime Manifest Schema](packages/schema/runtime-manifest.schema.json)
- [GVRM Metadata Schema](packages/schema/gvrm-metadata.schema.json)
