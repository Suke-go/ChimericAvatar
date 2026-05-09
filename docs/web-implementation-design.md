# Web実装詳細設計

## 1. 方針

研究室レベルの運用では、大規模な分散構成にしない。

最初は次の構成で進める。

```text
Next.js Dashboard
  -> FastAPI API
  -> Supabase Auth / PostgreSQL / Storage
  -> OpenAI / ElevenLabs
```

P0では`worker`、`realtime`、`queue`を分けない。重い処理はFastAPI内のjob table + async taskで開始し、必要になった時点でCelery/Redisへ分離する。

空のスケルトンだけを作るmilestoneは置かない。各実装stepは、画面、API、DB保存、最低限の検証が揃って初めて完了とする。

## 2. Lab-first Stack

| 領域 | 採用 | 備考 |
|---|---|---|
| Dashboard | Next.js App Router + TypeScript | 研究室メンバーが使う管理画面 |
| UI | Tailwind CSS + shadcn/ui + Radix UI + lucide-react | 低コストに実装 |
| Form | React Hook Form + Zod | session/paper/poster/script編集 |
| API client | openapi-fetch + openapi-typescript | FastAPI OpenAPIから型生成 |
| Auth | Supabase Auth | email/passwordまたはmagic link |
| DB | Supabase PostgreSQL + pgvector | evidence storeとsession metadata |
| Storage | Supabase Storage private bucket | poster、paper、gvrm、audio、bundle |
| API | FastAPI + Pydantic v2 | OpenAPI、SSE、WebSocket |
| Jobs | DB job table + FastAPI async task | P0。Celeryは後段 |
| LLM | OpenAI Responses API | structured outputs |
| TTS | ElevenLabs | pre-render TTSとlive TTS |

## 3. Auth / DB Access

認証は細かくしすぎない。研究室運用では次の2 roleだけにする。

```text
admin:
  契約したSupabase DB内の全session、全asset、全logへアクセスできる。
  user管理、全session復旧、削除、debug、migration確認を行う。

member:
  自分が作成したsessionだけ管理できる。
  共同編集はP0では扱わない。
```

Dashboard frontendはSupabase Authでloginするが、DBへ直接read/writeしない。実データ操作はFastAPI経由にする。

```text
Dashboard
  -> Supabase AuthでJWT取得
  -> FastAPIへJWT付きrequest
  -> FastAPIがrole確認
  -> FastAPIだけがservice role / DATABASE_URLでDB・Storageへアクセス
```

DBの全体アクセス権を持つもの:

```text
admin user
FastAPI service role
Supabase project owner
```

member userは、API上で`owner_id = auth.user_id`のsessionだけを扱う。

## 4. Dashboard Routes

```text
/login
/sessions
/sessions/new
/sessions/[sessionId]
/sessions/[sessionId]/knowledge
/sessions/[sessionId]/poster
/sessions/[sessionId]/avatar
/sessions/[sessionId]/preview
/sessions/[sessionId]/scripts
/sessions/[sessionId]/scripts/[profile]/[language]
/sessions/[sessionId]/tts
/sessions/[sessionId]/publish
/sessions/[sessionId]/runtime
/sessions/[sessionId]/questions
/settings/voices
```

| Page | 目的 | 主な操作 |
|---|---|---|
| `/sessions` | セッション一覧 | 作成、検索、公開状態確認 |
| `/sessions/new` | セッション作成 | title、poster orientation、language、creator |
| `/knowledge` | 論文・発表メモ登録 | PDF upload、notes入力、ingest開始 |
| `/poster` | A0ポスター設定 | poster image upload、A0縦/横、panel region登録、QR発行 |
| `/avatar` | Avatar asset管理 | `.gvrm` upload、Scaniverse PLY/SPZ upload、default VRM指定、変換job確認 |
| `/preview` | 非アバター要素の動作preview | poster panel focus、caption、pointer、audio cue、QR、runtime UIの確認 |
| `/scripts` | profile/language別script一覧 | beginner/master/professional x ja/enの生成状態 |
| `/scripts/[profile]/[language]` | script review | evidence確認、編集、承認、差し戻し |
| `/tts` | 音声生成 | voice選択、TTS cache生成、再生成 |
| `/publish` | runtime manifest発行 | approved assets/scripts確認、publish |
| `/runtime` | Unity接続情報 | session code、manifest URL、QR、download状態 |
| `/questions` | Q&A管理 | escalation確認、手動回答、ログexport |
| `/settings/voices` | voice管理 | ElevenLabs voice id、consent、default voice |

## 5. Non-avatar Preview

アバターの描画が未完成でも、発表体験の大部分はWebでpreviewできるようにする。

Preview対象:

```text
poster image
A0 portrait / landscape scaling
poster panel focus
figure / panel pointer target
caption position
script segment timing
audio playback timing
QR fallback placement
Unity runtime manifest content
```

P0のpreviewは2Dでよい。Gaussian avatarやUnity rendererは使わない。

採用:

```text
react-konva / konva:
  poster上のpanel bbox、focus、pointer、caption位置を表示・編集する。

HTMLAudioElement:
  generated TTS mp3を再生し、segment timingを確認する。

CSS animation:
  panel highlight、pointer movement、caption fadeを確認する。
```

Preview player:

```text
Timeline:
  segment start/end
  audio currentTime
  active panel
  caption text
  pointer target
  cue events
```

Preview cue types:

```text
panel_focus
pointer_move
caption_show
caption_hide
audio_play
qr_show
ui_panel_show
```

このpreviewは「アバター以外の動き」を確認するためのもの。アバター本体はdefault VRM / GVRM Runtime側で別途検証する。

## 6. Session Wizard

```text
Step 1: Basic
  title
  abstract
  presenter names
  event name

Step 2: Poster
  poster image
  orientation: A0 portrait / A0 landscape
  tracking mode: poster image primary
  QR fallback: enabled by default

Step 3: Knowledge
  paper PDF
  author notes
  do-not-say notes
  expected questions

Step 4: Avatar
  use default human VRM
  or upload .gvrm
  or upload Scaniverse PLY/SPZ for later conversion

Step 5: Profiles
  beginner ja/en
  master ja/en
  professional ja/en
```

P0ではpanel regionは手動で登録する。poster OCRや自動panel検出は後段にする。

## 7. API Design

Base:

```text
/api/v1
```

Session:

```text
GET    /sessions
POST   /sessions
GET    /sessions/{session_id}
PATCH  /sessions/{session_id}
DELETE /sessions/{session_id}
```

Assets:

```text
POST   /sessions/{session_id}/assets/upload-url
POST   /sessions/{session_id}/assets/complete
GET    /sessions/{session_id}/assets
GET    /sessions/{session_id}/assets/{asset_id}/signed-url
```

Knowledge:

```text
POST   /sessions/{session_id}/knowledge/ingest
GET    /sessions/{session_id}/knowledge/documents
GET    /sessions/{session_id}/knowledge/chunks
GET    /sessions/{session_id}/evidence/claims
```

Poster:

```text
GET    /sessions/{session_id}/poster
PATCH  /sessions/{session_id}/poster
POST   /sessions/{session_id}/poster/panels
PATCH  /sessions/{session_id}/poster/panels/{panel_id}
POST   /sessions/{session_id}/poster/qr
```

Preview:

```text
GET    /sessions/{session_id}/preview/cues
PUT    /sessions/{session_id}/preview/cues
POST   /sessions/{session_id}/preview/render-check
```

Avatar:

```text
GET    /sessions/{session_id}/avatar
PATCH  /sessions/{session_id}/avatar
POST   /sessions/{session_id}/avatar/build
GET    /sessions/{session_id}/avatar/builds/{job_id}
```

Scripts:

```text
POST   /sessions/{session_id}/scripts/generate
GET    /sessions/{session_id}/scripts
GET    /sessions/{session_id}/scripts/{profile}/{language}
PATCH  /sessions/{session_id}/scripts/segments/{segment_id}
POST   /sessions/{session_id}/scripts/segments/{segment_id}/approve
POST   /sessions/{session_id}/scripts/segments/{segment_id}/reject
```

TTS:

```text
POST   /sessions/{session_id}/tts/generate
GET    /sessions/{session_id}/tts/assets
GET    /sessions/{session_id}/tts/assets/{tts_asset_id}/signed-url
POST   /sessions/{session_id}/tts/assets/{tts_asset_id}/regenerate
```

TTS generation request:

```json
{
  "profile": "master",
  "language": "ja",
  "voiceId": "elevenlabs_voice_id",
  "segmentIds": ["seg_001"],
  "outputFormat": "mp3_44100_128",
  "forceRegenerate": false
}
```

TTS cache key:

```text
sha256(provider + voice_id + model_id + language_code + output_format + normalized_text + voice_settings_json)
```

Pre-render TTSはElevenLabs HTTP streamingをBackendで受け、音声bytesをprivate assetとして保存する。DashboardとUnityにはElevenLabs URLやAPI keyを渡さない。

Live Q&A TTSはElevenLabs WebSocketをBackend gatewayから呼ぶ。UnityはChimera runtime WebSocketだけに接続し、provider差分とAPI keyを持たない。

Publish / Runtime:

```text
POST   /sessions/{session_id}/publish
GET    /sessions/{session_id}/manifest
GET    /runtime/{session_code}/manifest
POST   /runtime/auth/login
GET    /runtime/assets/{asset_id}/signed-url
GET    /runtime/tts-assets/{tts_asset_id}/signed-url
```

Realtime:

```text
GET    /sessions/{session_id}/events
WS     /runtime/{session_code}/questions
WS     /runtime/{session_code}/audio
```

P0ではDashboardの更新通知はSSE、Unityの質問はWebSocketにする。

## 8. DB Minimum Schema

```sql
app_users (
  user_id uuid primary key,
  role text not null check (role in ('admin', 'member')),
  display_name text,
  created_at timestamptz not null default now()
);

sessions (
  id uuid primary key,
  owner_id uuid not null,
  title text not null,
  abstract text,
  event_name text,
  status text not null default 'draft',
  session_code text unique,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

poster_configs (
  session_id uuid primary key references sessions(id),
  poster_asset_id uuid,
  poster_format text not null default 'A0',
  orientation text not null check (orientation in ('portrait', 'landscape')),
  physical_size_m numeric[] not null,
  tracking_reference_name text,
  qr_fallback_enabled boolean not null default true,
  qr_asset_id uuid
);

assets (
  id uuid primary key,
  session_id uuid not null references sessions(id),
  kind text not null,
  storage_path text not null,
  file_name text not null,
  mime_type text,
  size_bytes bigint,
  status text not null default 'uploaded',
  created_at timestamptz not null default now()
);

knowledge_documents (
  id uuid primary key,
  session_id uuid not null references sessions(id),
  asset_id uuid references assets(id),
  kind text not null,
  title text,
  text text,
  status text not null default 'pending'
);

knowledge_chunks (
  id uuid primary key,
  session_id uuid not null references sessions(id),
  document_id uuid not null references knowledge_documents(id),
  panel_id uuid,
  chunk_order int not null,
  text text not null,
  language text,
  embedding vector(1536),
  metadata jsonb not null default '{}'
);

evidence_claims (
  id uuid primary key,
  session_id uuid not null references sessions(id),
  claim text not null,
  support_status text not null,
  evidence_chunk_ids uuid[] not null default '{}',
  limitation text
);

poster_panels (
  id uuid primary key,
  session_id uuid not null references sessions(id),
  label text not null,
  bbox_norm numeric[] not null,
  order_index int not null
);

presentation_cues (
  id uuid primary key,
  session_id uuid not null references sessions(id),
  segment_id uuid references script_segments(id),
  cue_type text not null,
  start_ms int not null,
  duration_ms int,
  payload jsonb not null default '{}',
  created_at timestamptz not null default now()
);

script_segments (
  id uuid primary key,
  session_id uuid not null references sessions(id),
  profile text not null check (profile in ('beginner', 'master', 'professional')),
  language text not null check (language in ('ja', 'en')),
  panel_id uuid references poster_panels(id),
  segment_order int not null,
  segment_type text not null,
  text text not null,
  evidence_chunk_ids uuid[] not null default '{}',
  status text not null default 'draft',
  tts_asset_id uuid,
  duration_estimate_sec int
);

tts_assets (
  id uuid primary key,
  session_id uuid not null references sessions(id),
  segment_id uuid references script_segments(id),
  voice_id text not null,
  language text not null,
  storage_path text not null,
  duration_sec numeric,
  cache_key text not null
);

jobs (
  id uuid primary key,
  session_id uuid references sessions(id),
  kind text not null,
  status text not null default 'queued',
  input jsonb not null default '{}',
  output jsonb not null default '{}',
  error text,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

questions (
  id uuid primary key,
  session_id uuid not null references sessions(id),
  transcript text not null,
  profile text not null,
  language text not null,
  active_panel_id uuid,
  answerability text,
  answer text,
  evidence_chunk_ids uuid[] not null default '{}',
  escalation_reason text,
  created_at timestamptz not null default now()
);
```

## 9. Job Execution

P0ではDBの`jobs` tableで管理する。

```text
POST endpoint
  -> jobs row作成
  -> FastAPI BackgroundTasks or asyncio task
  -> status更新
  -> Dashboard SSEで通知
```

Job kinds:

```text
ingest_paper
ingest_notes
extract_claims
generate_scripts
check_evidence
synthesize_tts
build_avatar_bundle
generate_qr
```

研究室運用で十分な間はこれでよい。複数発表・長時間OCR・asset変換が詰まり始めたら、同じjob tableを使ってCelery/Redisへ移す。

## 10. Asset Storage

Supabase Storage buckets:

```text
session-assets-private
runtime-assets-private
```

Storage path:

```text
sessions/{session_id}/poster/original.png
sessions/{session_id}/paper/paper.pdf
sessions/{session_id}/avatar/source.gvrm
sessions/{session_id}/avatar/scaniverse/source.ply
sessions/{session_id}/avatar/scaniverse/source.spz
sessions/{session_id}/runtime/avatar.bundle
sessions/{session_id}/tts/{segment_id}.mp3
sessions/{session_id}/qr/poster_qr_v1.png
```

Dashboard upload:

```text
1. Dashboard requests upload URL
2. API verifies admin/member access
3. API returns signed upload URL or creates upload route
4. Dashboard uploads file
5. Dashboard calls complete endpoint
6. API creates assets row
```

Unity download:

```text
1. Unity user enters email/password or session credential
2. API returns runtime token
3. Unity requests manifest
4. API returns manifest with short-lived signed asset URLs
5. Unity downloads avatar/audio/poster assets over mobile data
```

Unity download contract:

```text
manifest:
  JSON only
  short-lived signed URLs only
  no provider API keys
  no dashboard JWT

poster image:
  signed URL in manifest.assets.posterImage.url
  UnityWebRequestTexture.GetTexture for preview/reference
  DownloadHandlerFile for persistent local cache

panel data:
  manifest.posterPanels[]
  normalized bbox: x, y, width, height
  Unity converts bbox to local poster coordinates using A0 physical size

avatar bundle:
  manifest.avatar.assetBundle.url
  UnityWebRequestAssetBundle.GetAssetBundle

script audio:
  manifest.scripts[].segments[].audio.url
  DownloadHandlerAudioClip or DownloadHandlerFile

expired URL:
  Unity refreshes manifest or calls runtime signed-url endpoint
```

## 11. Dashboard Components

```text
features/sessions/
  SessionList.tsx
  SessionWizard.tsx
  SessionStatusBadge.tsx

features/poster/
  PosterUploader.tsx
  PosterOrientationControl.tsx
  PosterPanelEditor.tsx
  QrFallbackPanel.tsx

features/avatar/
  AvatarAssetUploader.tsx
  GvrmPreviewPanel.tsx
  ScaniverseUploadPanel.tsx
  AvatarBuildStatus.tsx

features/preview/
  PosterPreviewCanvas.tsx
  PreviewTimeline.tsx
  CueEditor.tsx
  CaptionPreview.tsx
  PointerPreview.tsx
  AudioPreviewPlayer.tsx

features/scripts/
  ProfileLanguageTabs.tsx
  ScriptSegmentEditor.tsx
  EvidenceDrawer.tsx
  ApprovalToolbar.tsx

features/tts/
  VoiceSelector.tsx
  TtsAssetList.tsx
  TtsGenerateButton.tsx

features/runtime/
  ManifestPreview.tsx
  SessionCodeCard.tsx
  UnityDownloadStatus.tsx

features/questions/
  QuestionList.tsx
  EscalationQueue.tsx
  ManualAnswerComposer.tsx
```

## 12. Concrete Implementation Plan

実装は、空のfolderやplaceholderだけでは完了扱いにしない。各stepで実際にブラウザから操作できるものを作る。

### Step 1: Admin Login + Session Create

Deliverable:

```text
Supabase Auth login
app_users role確認
adminだけ全session表示
memberは自分のsessionだけ表示
session作成
```

Done:

```text
/loginから入れる
/sessionsでadmin/memberの表示範囲が変わる
POST /sessionsがDBへ保存する
pytestでadmin/member accessを確認する
```

### Step 2: A0 Poster Upload + Poster Preview

Deliverable:

```text
poster image upload
A0 portrait / landscape選択
physicalSizeM自動設定
poster_configs保存
poster preview canvas表示
```

Done:

```text
/posterでA0縦横を切り替えられる
poster画像がprivate storageへ入る
preview canvasに実寸比で表示される
manifest trackingにposterFormat/orientation/physicalSizeMが入る
```

### Step 3: Panel Region Editor

Deliverable:

```text
poster上にpanel bboxを作る
label/orderを編集する
poster_panelsへ保存する
```

Done:

```text
react-konva上でpanelを追加/移動/resizeできる
DBへbbox_normが保存される
reload後も同じpanelが表示される
```

### Step 4: Knowledge Ingest

Deliverable:

```text
paper PDF upload
author notes入力
do-not-say notes入力
PyMuPDFでtext抽出
knowledge_documents / knowledge_chunks保存
```

Done:

```text
/knowledgeからPDFとnotesを登録できる
chunksがsession_id付きで保存される
memberは他sessionのchunksを読めない
```

### Step 5: Script Generate + Review

Deliverable:

```text
master/jaから先に生成
script_segments保存
evidence_chunk_ids表示
segment編集
approve/reject
```

Done:

```text
/scripts/master/jaでsegment一覧が出る
Evidence drawerで根拠chunkを見られる
approved segmentだけpublish候補になる
```

### Step 6: Non-avatar Motion Preview

Deliverable:

```text
PreviewTimeline
PosterPreviewCanvas
caption preview
panel focus animation
pointer movement
audio preview
presentation_cues保存
```

Done:

```text
/previewで再生buttonを押すとsegment順に動く
active panelがhighlightされる
captionが切り替わる
pointerがpanel/figureへ動く
audioがあれば同期再生される
cue編集後にreloadしても再現される
```

### Step 7: TTS Generate

Deliverable:

```text
approved segmentsからElevenLabs TTS生成
tts_assets保存
mp3をprivate storageへ保存
previewでaudio再生
```

Done:

```text
/ttsでmaster/jaのTTS生成が走る
cache_key一致時は再生成しない
/previewで音声付きpreviewできる
```

### Step 8: Publish Runtime Manifest

Deliverable:

```text
approved script
poster tracking config
presentation cues
avatar config
signed asset URLs
runtime manifest生成
```

Done:

```text
/publishでvalidation結果が見える
publish後に/runtime/{session_code}/manifestがschema-valid JSONを返す
unapproved segmentはmanifestに入らない
```

### Step 9: Unity Mobile Download Check

Deliverable:

```text
runtime login
manifest取得
signed URL取得
asset download status
```

Done:

```text
Unityまたは簡易HTTP clientからruntime authできる
manifestとposter/audio/avatar asset URLを取得できる
期限切れsigned URLは拒否される
```

### Step 10: Q&A Escalation

Deliverable:

```text
Unity/Webから質問送信
session内evidence retrieval
answerability判定
escalation dashboard表示
manual answer保存
```

Done:

```text
evidence不足の質問はescalationになる
/questionsに表示される
adminは全sessionの質問を見られる
memberは自分のsessionだけ見られる
```

## 13. Implementation Priority

P0:

```text
login
session list/create
poster upload + A0 orientation
poster preview canvas
panel region editor
paper PDF upload
author notes
default VRM / .gvrm upload
script generation
script review/approval
non-avatar motion preview
TTS generation
manifest publish
runtime manifest preview
```

P1:

```text
evidence drawer
QR fallback generator
Scaniverse PLY/SPZ upload
avatar bundle build status
Q&A escalation dashboard
analytics export
```

P2:

```text
Gaussian-VRM avatar preview
poster OCR
automatic panel detection
voice consent workflow
lab member management
```

## 14. Local Development

最小構成:

```text
Supabase local or hosted Supabase
FastAPI local
Next.js local
```

Commands:

```text
pnpm install
pnpm --filter @chimera/schema check
pnpm --filter dashboard dev
uv sync
uv run uvicorn app.main:app --reload
```

Environment:

```text
NEXT_PUBLIC_SUPABASE_URL=
NEXT_PUBLIC_SUPABASE_ANON_KEY=
NEXT_PUBLIC_API_BASE_URL=
SUPABASE_URL=
SUPABASE_SERVICE_ROLE_KEY=
SUPABASE_JWT_SECRET=
DATABASE_URL=
OPENAI_API_KEY=
ELEVENLABS_API_KEY=
```

## 15. Deployment

研究室運用の推奨:

```text
Dashboard:
  Vercel

API:
  Render / Fly.io / Railway / lab server

DB/Auth/Storage:
  Supabase hosted

Unity runtime:
  API endpoint + Supabase signed URLs
```

最初からKubernetes、独立realtime server、複数worker queue、CDN最適化は不要。

## 16. E2E検証

Playwrightで最低限のflowを確認する。

```text
1. Login
2. Create session
3. Upload A0 poster
4. Create poster panels
5. Upload paper PDF or text fixture
6. Add author notes
7. Generate master/ja script
8. Approve all required segments
9. Create non-avatar preview cues
10. Generate TTS
11. Preview audio/caption/panel focus
12. Publish manifest
13. Validate manifest schema
```

API側はpytestで確認する。

```text
JWT required
admin can access all sessions
member can access only own sessions
session isolation
manifest contains only approved scripts
signed URL expires
evidence missing -> escalation
```
