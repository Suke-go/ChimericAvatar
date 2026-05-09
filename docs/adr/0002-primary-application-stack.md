# ADR 0002: Next.js + FastAPI + Supabase + Unityを主要stackにする

Status: Accepted

## Context

Chimera Presenterは、Dashboard、AI workflow、asset processing、runtime manifest、Unity clientを含む。AI/asset処理はPython ecosystemが強く、DashboardはReact/TypeScriptが速い。Auth、Postgres、Storage、signed URLは早期に本番相当で検証する必要がある。

## Decision

主要stack:

```text
Dashboard:
  Next.js App Router + TypeScript

API / Realtime:
  FastAPI + Pydantic + WebSocket/SSE

Worker:
  DB job table in FastAPI first, later Celery + Redis

DB/Auth/Storage:
  Supabase Auth + PostgreSQL + pgvector + Supabase Storage

XREAL Client:
  Unity 6000 LTS + XREAL SDK 3.1.0 + UniVRM + URP + Animation Rigging + Timeline
```

Knowledge設計は、普通のRAGを直接公開する形ではなく、Evidence Store、承認済みscript、answerability判定、escalationを持つEvidence-Governed Knowledge Systemにする。

## Consequences

良い点:

- DashboardとAPIの型境界をOpenAPIで検証できる。
- AI処理、PDF処理、asset変換をPythonで実装できる。
- Auth、RLS、pgvector、signed URLを早期に本番相当で扱える。
- Unity runtimeはWeb appから独立してGaussian-VRM、Scaniverse由来human splats、poster motionを性能検証できる。
- 研究室運用ではworker/realtimeを分けずに始められる。

技術リスク:

- TypeScriptとPythonの2系統のtoolchainを持つ。
- Supabase依存が増えるため、将来のself-host移行用にinterface境界が必要。

## Validation

- OpenAPIからDashboard clientを生成する。
- Supabase JWTをFastAPIで検証する。
- pgvector searchは必ず`session_id` filter付きで行う。
- Supabase signed URL期限切れをtestする。
- Unity mobile clientが認証情報入力後にruntime manifestとsigned assetsをdownloadできる。
- Unity mock clientがruntime manifestをdeserializeできる。
