# Chimera Presenter 技術詳細設計

## 1. 設計の粒度

この文書は、`docs/production-design-policy.md`を実装可能な技術選定へ落としたものです。

実装背景と参照文献は`docs/implementation-background-and-references.md`を正本にする。

方針:

- 主要ライブラリは、採用理由、代替、検証方法を必ず持つ。
- Web実装は研究室運用を優先し、P0ではNext.js、FastAPI、Supabaseの小さな構成で開始する。詳細は[Web実装詳細設計](web-implementation-design.md)を正本にする。
- Unity GVRM RuntimeはOpenSourceの`naruya/gaussian-vrm`を資産生成・仕様参照に使い、人間Gaussian Splatting scanをVRM骨格で動かすruntimeとして実装する。実装ロジックは[Unity GVRM Runtime Logic](unity-gvrm-runtime-logic.md)を正本にする。
- OpenAPI、JSON Schema、DB migration、Unity Test Frameworkで検証可能な境界を作る。
- P0では動作確認を優先し、P1以降で分離・高速化する。

## 2. 採用スタック要約

| 領域 | 採用 | 理由 | 検証 |
|---|---|---|---|
| Monorepo | pnpm workspace + Turborepo | JS/TS appと共有packageを扱いやすい | `pnpm install`, `pnpm turbo build` |
| Dashboard | Next.js App Router + TypeScript | Dashboard、SSE、server action、認証UIに向く | Playwright E2E |
| UI | Tailwind CSS + shadcn/ui + Radix UI | 業務Dashboardを速く作れる | StorybookまたはPlaywright visual smoke |
| API | FastAPI + Pydantic v2 | OpenAPI生成、WebSocket、AI処理との相性 | `pytest`, OpenAPI schema diff |
| DB | PostgreSQL + pgvector | Evidence Store、承認済み説明、vector retrievalを同一DBで管理 | retrieval recall test |
| Auth/Storage | Supabase Auth + Supabase Storage | JWT、RLS、signed URL、Postgres/pgvectorと統合 | JWT検証、signed URL expiry test |
| Jobs | DB job table + FastAPI task, later Celery + Redis | 研究室運用では小さく開始し、詰まったらqueue分離 | retry/idempotency test |
| Realtime | FastAPI WebSocket/SSE | P0ではAPI内に集約し、必要なら後で分離 | audio/Q&A streaming test |
| LLM | OpenAI Responses API | structured outputs、streaming、tool/function calling | schema conformance eval |
| Embedding | `text-embedding-3-large` with `dimensions=1536` | 多言語/専門文書のretrieval品質とpgvector互換 | retrieval eval |
| TTS | ElevenLabs HTTP streaming + WebSocket TTS | pre-renderとlive Q&Aの両方に対応 | time-to-first-audio test |
| Unity | Unity 6000 LTS | XREAL本番clientの固定target | Android build smoke |
| XREAL | XREAL One Pro + XREAL Eye + XREAL Beam Pro + XREAL SDK 3.1.0 | 6DoF tracking、Unity XR系統、標準hostを固定 | poster image tracking sample |
| VRM | UniVRM v0.131+ package line | VRM 0.x/1.0 import | runtime VRM load test |
| Gaussian-VRM | `naruya/gaussian-vrm` | `.gvrm`生成、`data.json`仕様、skinned Gaussian avatarの挙動確認 | `.gvrm` fixture compatibility |
| Gaussian Splat reference | `wuyize25/gsplat-unity`, `aras-p/UnityGaussianSplatting` | GPU sorting、PLY import、Unity shader参考 | 60k/150k splat benchmark |
| Motion | Animator + Timeline + Animation Rigging | Humanoid retarget、poster pointing、look-at、gesture制御 | presentation gesture smoke |
| Unity tests | Unity Test Framework + Performance Testing | GVRM loader、renderer、PlayMode検証 | EditMode/PlayMode tests |

## 3. Runtime / Version方針

### Node.js

Target: Node.js `24.x` LTS。

理由:

- 2026-05-01時点でNode.js 24はActive LTSで、EOLは2028-04-30。
- Next.js/Turborepo/pnpmをproductionで扱う前提として、現行LTSに合わせる。

検証:

```text
node --version
pnpm --version
pnpm install
pnpm turbo build
```

### Python

Target: Python `3.13`。

理由:

- 2026-05-01時点でPython 3.13はbugfix系列で、EOLは2029-10。
- Python 3.14は最新だが、AI/ML/OCR系依存の互換性確認コストを避ける。
- 依存が3.13未対応の場合だけ、Python 3.12へ一時fallbackする。

Package manager: `uv`。

検証:

```text
uv python install 3.13
uv sync
uv run pytest
```

### Unity

Primary: Unity `6000 LTS`。

理由:

- XREAL本番clientのUnity targetをUnity 6000 LTSへ固定する。
- SDK、UniVRM、URP、Animation Rigging、Android Vulkan検証を同一Unity系統で行う。
- fallback Unityを並行管理せず、Unity 6000 LTS上で問題を検証・解消する。

検証:

```text
Unity Editor opens project
XREAL SDK imports without compile errors
Android build completes
XREAL Image Tracking sample runs
Animation Rigging constraints run in PlayMode
```

### XREAL Hardware

Target hardware:

```text
Glasses:
  XREAL One Pro

Camera / 6DoF:
  XREAL Eye

Host:
  XREAL Beam Pro
```

Development host:

```text
Samsung S25
```

検証:

```text
XREAL One Pro firmware is updated
XREAL Eye is recognized
Beam Pro MyGlasses is updated
XREAL SDK 3.1.0 Image Tracking sample runs
Poster image target creates PosterAnchorRoot
```

### Poster

Target poster format:

```text
A0 portrait:
  physicalSizeM = [0.841, 1.189]

A0 landscape:
  physicalSizeM = [1.189, 0.841]
```

Dashboardはposter orientationを登録し、runtime manifestへ`posterFormat`、`orientation`、`physicalSizeM`を出力する。

## 4. Dashboard設計

DashboardとAPIの具体的な画面、endpoint、DB schema、job設計は[Web実装詳細設計](web-implementation-design.md)を正本にする。

Path:

```text
apps/dashboard/
```

採用ライブラリ:

```text
next
react
typescript
tailwindcss
shadcn/ui
@radix-ui/*
lucide-react
@tanstack/react-query
react-hook-form
zod
@supabase/supabase-js
openapi-typescript
openapi-fetch
@playwright/test
vitest
```

主要責務:

- Session作成、poster/paper/notes/asset登録。
- `.gvrm` upload、preview、conversion status表示。
- Audience profile管理。profileは`beginner`、`master`、`professional`に固定する。
- Language管理。languageは`ja`、`en`に固定する。
- Script Agentの生成結果レビュー。
- Evidence差し戻し、script approval。
- TTS cache状態表示。
- Q&A escalation一覧、presenter manual answer。
- Runtime manifest publish。

検証可能な境界:

| 機能 | 合格条件 |
|---|---|
| Session作成 | Dashboardからsessionが作られ、APIのOpenAPI schemaと型が一致 |
| Asset upload | Supabase Storageへprivate uploadされ、APIがsigned URLを返す |
| Script approval | `script_segments.status`が`approved`になり、未承認segmentはmanifestに出ない |
| Escalation | Live Q&Aのunanswerable eventがDashboardにSSEで表示される |
| E2E | Playwrightでsession作成からpublishまで通る |

## 5. API / Worker設計

Path:

```text
apps/api/
apps/workers/
```

採用ライブラリ:

```text
fastapi
uvicorn
pydantic
pydantic-settings
sqlalchemy
alembic
asyncpg
psycopg
pgvector
redis
openai
elevenlabs
httpx
websockets
PyJWT
cryptography
jsonschema
structlog
opentelemetry-api
opentelemetry-sdk
pytest
pytest-asyncio
```

PDF/OCR/文書処理:

```text
pymupdf
pillow
python-multipart
```

OCRはP0ではprovider interfaceだけ作り、実装は次の順で検証する。

```text
1. Poster text manually uploaded
2. PyMuPDF text extraction for paper PDFs
3. Cloud OCR or PaddleOCR spike for poster OCR
```

API責務:

- Supabase JWT検証。
- admin/member access control。
- Session / asset / knowledge / script / manifest API。
- Supabase Storage signed URL発行。
- OpenAPI schema生成。
- Dashboard SSE。
- Unity runtime manifest API。
- Runtime Q&A WebSocket。

OpenAI API方針:

```text
primary endpoint: Responses API
script / evidence / Q&A default model: gpt-5.4-mini
reasoning effort: low by default, medium for evidence checking when needed
structured output: JSON Schema
streaming: Live Q&A only
embedding model: text-embedding-3-large, dimensions=1536
```

`gpt-5.4-mini`は、Responses API、streaming、structured outputsに対応しており、研究室レベルのScript Agent / Evidence Checker / Q&Aの初期実装ではコストと性能のバランスがよい。複雑な査読者向け説明や長い論文横断の検証だけ、環境変数で`gpt-5.4`へ上げられるようにする。

Worker責務:

- PDF extraction。
- Poster panel region解析。
- Embedding生成。
- Script generation。
- Evidence checking。
- TTS生成。
- `.gvrm` -> `avatar.bundle`変換。

P0では`jobs` table + FastAPI taskで開始する。長時間処理や複数sessionが詰まり始めたら、同じjob tableを維持してCelery/Redisへ分離する。

## 6. Database / Evidence-Governed Knowledge設計

Primary DB: Supabase PostgreSQL。

Extension:

```sql
create extension if not exists vector;
create extension if not exists pgcrypto;
```

Embedding:

```text
model: text-embedding-3-large
dimensions: 1536
distance: cosine
index: HNSW
```

理由:

- OpenAIの`text-embedding-3-large`はdefault 3072 dimensionsだが、`dimensions` parameterで短縮できる。
- pgvectorの`vector`は上限2000 dimensionsなので、1536 dimensionsに固定する。
- session内文書数は増減するため、IVFFlatよりHNSWを第一候補にする。

retrievalはEvidence-Governed Knowledge Systemの一部として扱う。普通のRAG応答を直接公開せず、次の層を分ける。

```text
Knowledge Documents:
  paper / poster / notes / approved presenter statements

Evidence Store:
  claim / evidence chunk / figure / panel / limitation / approval status

Script Store:
  profile / language / segment / evidence ids / approval status

Live Q&A:
  question / active panel / retrieved evidence / answerability / answer / escalation
```

必須query制約:

```sql
where session_id = :session_id
```

retrievalは、vector searchだけでなく、BM25相当の全文検索とmetadata filterを組み合わせる。回答生成ではretrieved chunkをそのまま信じず、`answerability`と`evidence_ids`を必ず保存する。

最小テーブル:

```text
sessions
app_users
assets
poster_configs
poster_panels
knowledge_documents
knowledge_chunks
evidence_claims
script_segments
presentation_cues
tts_assets
jobs
questions
voice_consents
event_logs
```

検証:

| Test | 合格条件 |
|---|---|
| session isolation | 他sessionのchunkがretrievalに混ざらない |
| vector recall | fixed eval setでtop-kに正解evidenceが入る |
| evidence checker | unsupported claimが`UNSUPPORTED`になる |
| manifest publish | approved segmentのみmanifestに含まれる |
| answerability | evidence不足の質問がescalationになる |

## 7. Script Agent設計

OpenAI Responses APIを使う。Agent frameworkは初期段階では保留する。

理由:

- Script generationはworkflowが明確で、LangChain/LlamaIndexの抽象化よりも、Pydantic schemaと明示的なstep実装の方が検証しやすい。
- 各Agentの入力/出力をDBへ保存し、再実行・差分比較できる。

採用パターン:

```text
Pydantic schema
  -> OpenAI structured output
  -> jsonschema validation
  -> DB persist
  -> human review
```

Audience / Language:

```text
profiles:
  beginner      HighSchool相当
  master        Bachelor / Master相当
  professional  PhD相当

languages:
  ja
  en
```

Agent:

```text
KnowledgeIngestionAgent
ClaimExtractionAgent
AudienceModelingAgent
ScriptPlannerAgent
ScriptWriterAgent
EvidenceCheckerAgent
VoiceStyleAgent
SafetyBoundaryAgent
TtsChunkingAgent
```

検証:

- 同じinput fixtureからschema-valid JSONが出る。
- `evidence_chunk_ids`が実在する。
- `UNSUPPORTED` claimを公開scriptへ進めない。
- profile/language別にduration upper boundを守る。

## 8. TTS / Audio設計

採用:

```text
ElevenLabs Text-to-Speech HTTP streaming
ElevenLabs Text-to-Speech WebSocket stream-input
ElevenLabs official SDK / examples as implementation references
```

使い分け:

| 用途 | API | 理由 |
|---|---|---|
| 承認済みscript | HTTP TTS + cache | 入力全文が事前にあるため安定性重視 |
| Live Q&A | WebSocket TTS | LLM token streamから低遅延で音声化 |
| 字幕同期 | WebSocket alignment | chunk alignmentをsubtitle timingへ利用 |

ElevenLabs implementation contract:

```text
Pre-render script TTS:
  Backend -> ElevenLabs HTTP streaming endpoint
  Input:
    voice_id
    model_id
    language_code: ja | en
    output_format: mp3_44100_128
    voice_settings
    text from approved script segment
    previous_text / next_text when stitching adjacent segments
  Output:
    streamed audio bytes stored as private tts asset
    tts_assets row with cache_key
    signed audio URL in runtime manifest

Live Q&A TTS:
  Backend gateway -> ElevenLabs WebSocket stream-input
  Input:
    sentence-sized text chunks from LLM stream
    voice_settings and generation config
  Output:
    base64 audio chunks
    optional alignment / normalizedAlignment
  Unity:
    connects to Chimera runtime WebSocket, not directly to ElevenLabs
```

Security boundary:

- ElevenLabs API key is never sent to Dashboard browser or Unity.
- Unity receives Chimera signed audio URLs for pre-rendered segments.
- Unity receives live audio through Chimera runtime WebSocket for Q&A.
- Raw user audio is not persisted unless an evaluation mode explicitly enables it.
- `voice_id`, `model_id`, `language_code`, `output_format`, `cache_key`, and provider metadata are server-side data.

Unityへ送る音声形式:

```text
P0: mp3 chunks + subtitle text event
P1: PCM streaming検証
P2: lip-sync marker / viseme support検証
```

検証:

| Test | 合格条件 |
|---|---|
| pre-render TTS | approved segmentごとにaudio assetが生成される |
| live TTS | first text chunkから2秒以内にUnityへaudio chunkが届く |
| cache hit | 同一voice/text/settingsで再生成しない |
| consent | cloned voice使用時にvoice_consentsが存在する |

参考実装:

- ElevenLabs official examples repositoryのTTS quickstart / WebSocket latency demoを参照する。
- HTTP streamingはSDK利用でもよいが、Chimera側のAPI契約は「audio bytesをprivate assetへ保存し、manifestにはsigned URLだけを出す」に固定する。
- WebSocket TTSはBackend gatewayで吸収し、Unity clientへprovider API keyやElevenLabs固有payloadを露出しない。

## 9. Unity XR Client設計

Path:

```text
apps/xr-client/
```

Target devices:

```text
Primary AR test:    Meta Quest 3        (passthrough AR, OpenXR + Meta XR SDK)
Secondary AR test:  XREAL One Pro + XREAL Eye + XREAL Beam Pro  (XREAL SDK 3.1.0)
Editor smoke:       StubPosterAnchorRoot
```

Anchor backendは `Assets/ChimeraRuntime/Runtime/Anchor/` 直下にbackend別フォルダ
(`Stub/`, `Manual/`, `Meta/`, `Xreal/`)で実装する。`PosterAnchorRoot`抽象を共通契約に、
`AnchorBackendFactory`が`RuntimeBuildProfile` ScriptableObjectのpreferred backend順で解決する。
Meta XR SDK / XREAL SDK固有コードは `CHIMERA_META_XR` / `CHIMERA_XREAL_SDK` define gateで
import済みかどうかをコンパイル時に切り替える。

詳細なdual-target判断は[ADR 0003](adr/0003-dual-target-xreal-and-meta-quest.md)を正本にする。

採用Unity packages:

```text
XREAL SDK 3.1.0
UniVRM v0.131+ package line
Input System
XR Interaction Toolkit
AR Foundation
Universal Render Pipeline
Animation Rigging
Timeline
com.unity.nuget.newtonsoft-json
Unity Test Framework
Unity Performance Testing
```

Graphics:

```text
Android graphics API: Vulkan primary
Render pipeline: URP
Color space: Linear
Stereo: XREAL SDK settingに従う
```

Unity GVRM Runtime module:

```text
Assets/Chimera/Runtime/Avatar/Gvrm/
  GvrmZipLoader.cs
  GvrmMetadata.cs
  VrmRigLoader.cs
  PlySplatLoader.cs
  GvrmBindingData.cs
  SplatBatch.cs
  SplatBoneSorter.cs
  SkinnedGaussianRenderer.cs
  PosterGestureController.cs
  AvatarLookAtController.cs
  GvrmAvatarController.cs
```

Asset入力:

```text
Scaniverse human Gaussian Splatting scan
  -> PLY or SPZ export
  -> Gaussian-VRM authoring / bone binding
  -> .gvrm
  -> optimized avatar.bundle
  -> Unity 6000 LTS runtime
```

モデル未登録時:

```text
default human VRM
  -> UniVRM runtime load
  -> Humanoid Animator
  -> same presentation motion controller
```

参考にするGaussian Splatting実装:

| Library | 位置づけ | 採用判断 |
|---|---|---|
| `naruya/gaussian-vrm` | `.gvrm` format、`data.json`、bone binding、skinning logicの仕様元 | 資産生成・互換性検証で使用 |
| `wuyize25/gsplat-unity` | Android/XR/Vulkan検証の第一候補 | renderer性能spikeで使用 |
| `aras-p/UnityGaussianSplatting` | importer/compression/compute shader参考 | desktop importerやshader設計の参考として使用 |

Motion実装:

| Unity機能 | 用途 | 検証 |
|---|---|---|
| Animator / Mecanim Humanoid | Idle、Listening、Pointingなどのclip retarget | default VRMとGVRM rigで同じclipが再生できる |
| Timeline | script segment、audio、caption、gesture cueの同期 | segment再生でmotion/audio/captionが同期する |
| Animation Rigging Multi-Aim Constraint | 頭・視線をviewerまたはposterへ向ける | target変更時に破綻しない |
| Animation Rigging Two Bone IK | 腕をposter panel / figure regionへ向ける | panel boundsへ指差しできる |
| XR Interaction Toolkit | controller inputとruntime操作 | Tap/Swipe/Long press相当の入力eventが届く |

GVRM Runtimeの検証順:

```text
1. .gvrm zip展開
2. data.json schema validation
3. model.vrmをUniVRMでload
4. model.plyをSplatBufferへparse
5. splatVertexIndices / splatBoneIndices / splatRelativePosesの整合性検査
6. static splat描画
7. bone batch grouping
8. 1 boneだけskinned deformation
9. humanoid animationで全身追従
10. presentation gesture controllerでposter pointing
11. XREAL stereo / poster image anchor上で描画
```

性能合格ライン:

```text
LOD2 60k splats:
  XREAL target deviceで安定動作

LOD1 150k splats:
  短時間demoで熱暴走せず動作

LOD0 300k splats:
  high quality mode。target deviceで不安定なら非default
```

## 10. Runtime Manifest設計

Manifest schemaの正本:

```text
packages/schema/runtime-manifest.schema.json
```

ManifestはUnity clientがsessionを起動する唯一の入口にする。

Unity download policy:

```text
1. Unity user enters session code and runtime credential
2. Unity GET /runtime/{session_code}/manifest with runtime bearer token
3. Manifest contains short-lived signed URLs only
4. Poster image:
   - DownloadHandlerTexture for immediate reference texture
   - DownloadHandlerFile for persistent cache when needed
5. Avatar bundle:
   - UnityWebRequestAssetBundle.GetAssetBundle
   - manifest includes hash/crc when bundle pipeline is ready
6. Script audio:
   - DownloadHandlerAudioClip for immediate AudioClip use
   - DownloadHandlerFile for cached playback
7. Expired URL:
   - Unity refreshes manifest or requests a fresh asset URL
```

Unity coordinate contract:

```text
Unity unit: 1.0 = 1 meter
Poster origin: center of A0 poster plane
Poster local X: right
Poster local Y: up
Poster local Z: forward from poster plane
posterPanels[].bbox: normalized top-left image coordinates for UI/editor
posterPanels[].localBoundsM: meter-space center/size for Unity placement and pointing
```

Manifestに入れてはいけないもの:

- ElevenLabs API key
- Supabase service role key
- Dashboard JWT
- long-lived storage URL
- raw private bucket path

必須検証:

```text
jsonschema validates runtime-manifest.example.json
Unity C# DTO deserializes manifest
signed URL expiry is enforced
unapproved script segment is absent
```

## 11. Security設計

Auth:

- Supabase Auth JWTをFastAPIで検証する。
- Dashboard操作はJWT必須。
- Unity mobile clientはモバイルデータ通信上で認証情報を入力し、APIからruntime token、manifest、signed asset URLを取得する。
- Runtime clientはAPI経由でmanifestを取得し、private assetは短期限signed URLでdownloadする。
- DashboardのDB read/writeはFastAPI経由に限定する。

```text
admin:
  契約DB内の全session、全asset、全logにアクセスできる。
  migration、debug、復旧、削除、user管理を行う。

member:
  自分が作成したsessionだけ管理できる。
```

監査:

- `event_logs`にactor、action、resource、request_idを保存する。
- raw audio、camera frame、face imageは保存しない。

## 12. まず実装するVertical Slice

最初の検証可能なend-to-endは、Gaussian runtimeを待たずに作る。空のスケルトンだけのmilestoneは置かない。

```text
Slice 1: Admin login + session + A0 poster
  - Supabase Auth login
  - admin/member access
  - Dashboardでsession作成
  - A0 poster upload
  - poster preview canvas
  - panel region editor

Slice 2: Knowledge -> Script
  - PDF/text upload
  - author notes
  - chunking + embedding
  - master/ja script generation
  - evidence checker
  - approval

Slice 3: Non-avatar preview + TTS
  - poster panel focus preview
  - pointer/caption/audio cue preview
  - TTS cache
  - runtime manifest publish

Slice 4: XREAL shell
  - XREAL SDK import
  - Poster Image Tracking sample
  - PosterAnchorRoot
  - default VRM avatar placement
  - Animator / Timeline / Animation Rigging gestures
  - cached audio playback + captions

Slice 5: GVRM Runtime proof
  - .gvrm parse
  - static splat render
  - 1 bone deformation
  - humanoid animation追従

Slice 6: Live Q&A
  - Unity -> API WebSocket question
  - retrieval with session filter
  - streaming answer
  - ElevenLabs live TTS
  - escalation
```

## 13. 参照元

- Unity release support: https://unity.com/releases/release-overview
- Unity Animation Rigging manual: https://docs.unity3d.com/Packages/com.unity.animation.rigging@latest
- Unity Timeline manual: https://docs.unity3d.com/Packages/com.unity.timeline@latest
- Unity humanoid animation retargeting: https://docs.unity3d.com/Manual/Retargeting.html
- XREAL SDK overview: https://docs.xreal.com/
- XREAL SDK download/release notes: https://developer.xreal.com/download/
- UniVRM documentation: https://vrm.dev/en/univrm/
- Gaussian-VRM repository: https://github.com/naruya/gaussian-vrm
- Scaniverse Gaussian Splatting: https://scaniverse.com/news/scaniverse-introduces-support-for-3d-gaussian-splatting
- Scaniverse SPZ format: https://scaniverse.com/spz
- gsplat-unity repository: https://github.com/wuyize25/gsplat-unity
- UnityGaussianSplatting repository: https://github.com/aras-p/UnityGaussianSplatting
- Next.js docs: https://nextjs.org/docs
- Turborepo docs: https://turborepo.com/docs
- Node.js release schedule: https://github.com/nodejs/Release
- Python downloads/release status: https://www.python.org/downloads/
- uv docs: https://docs.astral.sh/uv/
- FastAPI docs: https://fastapi.tiangolo.com/
- Supabase Auth docs: https://supabase.com/docs/guides/auth
- Supabase pgvector docs: https://supabase.com/docs/guides/database/extensions/pgvector
- Supabase signed URL docs: https://supabase.com/docs/reference/javascript/storage-from-createsignedurls
- pgvector repository: https://github.com/pgvector/pgvector
- OpenAI Responses streaming docs: https://platform.openai.com/docs/guides/streaming-responses
- OpenAI embeddings docs: https://platform.openai.com/docs/guides/embeddings
- ElevenLabs WebSocket docs: https://elevenlabs.io/docs/api-reference/websocket
- ElevenLabs streaming docs: https://elevenlabs.io/docs/api-reference/streaming
- Playwright docs: https://playwright.dev/docs/intro
- pytest docs: https://docs.pytest.org/en/stable/
- Unity Test Framework docs: https://docs.unity.cn/Packages/com.unity.test-framework%402.0/manual/edit-mode-vs-play-mode-tests.html
