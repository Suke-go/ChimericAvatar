# Chimera Presenter — セットアップ & デプロイ詳細手順

```text
[Browser]                     [Mobile XREAL]
    │                              │
    │ HTTPS                        │ HTTPS / WSS
    ▼                              ▼
┌────────────────────┐    HTTPS    ┌────────────────────┐
│ Dashboard          │ ──────────▶ │ API (FastAPI)      │
│ Vercel Hobby       │             │ Fly.io 256MB       │
└────────────────────┘             └─────────┬──────────┘
                                              │
                                              ▼
                                   ┌────────────────────┐
                                   │ Supabase (Free)    │
                                   │ Auth + Postgres    │
                                   │ + Storage          │
                                   └────────────────────┘
```

ベース月額: **$0〜7** + OpenAI/ElevenLabs従量。

---

## 0. 前提環境

ローカルマシン (Windows/macOS/Linux) に以下が入っていること。

| ツール | 推奨バージョン | 確認コマンド |
|---|---|---|
| Python | 3.11+ | `python --version` |
| Node.js | 20.x LTS | `node -v` |
| Git | any | `git --version` |
| flyctl (Fly CLI) | 最新 | `fly version` (Phase 3で導入可) |

flyctl 導入(まだなら):

```bash
# macOS / Linux
curl -L https://fly.io/install.sh | sh

# Windows (PowerShell)
iwr https://fly.io/install.ps1 -useb | iex
```

GitHubアカウントとSupabaseアカウント・Vercelアカウント・Fly.ioアカウントを事前作成しておく。

---

## 1. ローカル動作確認 (15分)

ローカルで一通り動くことを確認してから本番へ進む。

### 1-1. リポジトリ取得

```bash
git clone <this-repo-url>
cd ChimericAvatar
```

### 1-2. API (FastAPI) 起動

```bash
cd apps/api

# 仮想環境
python -m venv .venv
# Windows
.venv\Scripts\activate
# macOS/Linux
source .venv/bin/activate

# 依存をeditableで入れる
pip install -e .

# 環境変数
cp .env.example .env
# 必要なら編集 (デフォルトでローカル動作可: SQLite + LocalStorage + DEBUG_AUTH_ENABLED=true)
# OPENAI_API_KEY と ELEVENLABS_API_KEY を入れると台本AI生成・TTSが効く
```

`.env` 最低構成:

```env
APP_ENVIRONMENT=local
DEBUG_AUTH_ENABLED=true
DATABASE_URL=sqlite:///./chimera_lab.db
STORAGE_BACKEND=local
ASSET_URL_SECRET=local-dev-asset-secret-change-me
API_PUBLIC_BASE_URL=http://localhost:8000
CORS_ALLOW_ORIGINS=http://localhost:3000,http://127.0.0.1:3000
```

起動:

```bash
uvicorn app.main:app --reload --port 8000
```

別ターミナルで:

```bash
curl http://localhost:8000/health
# → {"status":"ok"}
```

### 1-3. Dashboard 起動

```bash
cd apps/dashboard
npm install
cp .env.example .env.local
# Supabaseは未設定でOK (debugログイン)
npm run dev
```

ブラウザで `http://localhost:3000` を開く。

1. `/login` → "lab-admin" / "admin" → Continue (debug)
2. `/sessions` → "+ Create session" → タイトル入力 → 作成
3. `/sessions/[id]/poster` で画像 + パネル登録
4. `/sessions/[id]/avatar` で `.gvrm` または default-vrm 設定 (PLY のみ必須、Base VRM は同梱の `fem_vroid.vrm` を使用、`apps/api/avatar-base/` に配置必要)
5. `/sessions/[id]/knowledge` でKnowledgeアップロード+台本+QA生成+承認
6. `/sessions` に戻り Publish → manifest URL確認

これが全部通ればローカルOK。問題があれば本番へ進めない。

---

## 2. Supabase プロジェクト作成 (10分)

UIのクリック単位で手順化。すべて [supabase.com/dashboard](https://supabase.com/dashboard) 上で行う。

### 2-1. プロジェクト作成

1. New Project
2. Project Name: `chimera-presenter`(任意)
3. Database Password: 強いものを生成して**控える**(後で `DATABASE_URL` に必要)
4. Region: `Northeast Asia (Tokyo)` を推奨(XREAL接続レイテンシ最小)
5. Pricing Plan: Free
6. Create new project (3〜5分待つ)

### 2-2. pgvector拡張を有効化

左サイドバー → **Database** → **Extensions**

- 検索ボックスに `vector`
- `vector` の行で **Enable extension** をクリック
- 緑のチェックがつけばOK

### 2-3. Storage bucket 作成

左サイドバー → **Storage** → **New bucket**

- Name: `session-assets-private`
- Public bucket: **OFF** (private のまま)
- Allowed MIME types: 空のまま (任意)
- **File size limit**: 50MB が既定で OK (avatar `.gvrm` は SPZ 前提なら ~15MB に収まる)。素の PLY のままだと 50MB を超えて build が 413 で reject される — Scaniverse は **SPZ 形式で export** してもらう運用
- Save

(必要であれば `runtime-assets-private` も追加するが、P0は1個で足りる)

**Avatar ストレージはハイブリッド (sources=local、output=Supabase)**:

`apps/api/app/storage.py` の `LOCAL_AVATAR_KINDS` で:

```python
LOCAL_AVATAR_KINDS = {
    "avatar-scaniverse-ply",   # ← 一時的、build 後に削除
    "avatar-scaniverse-spz",   # ← 一時的、build 後に削除
    "avatar-base-vrm",         # ← カスタム VRM upload、build 後に削除
}
# avatar-gvrm はここに含まれない → STORAGE_BACKEND を尊重 (= Supabase)
```

| Kind | 行き先 | 寿命 | 理由 |
|---|---|---|---|
| `avatar-scaniverse-ply/spz` | **LocalStorage (Fly volume)** | build 後削除 | 77MB の PLY 等は Supabase 50MB cap を超える。一時データなので冗長性不要 |
| `avatar-base-vrm` (custom upload) | **LocalStorage (Fly volume)** | build 後削除 | サイズ任意 + 一時データ |
| `avatar-gvrm` | **Supabase Storage** (configured backend) | 長期保管 | 出力アーティファクト、Supabase の 11 nines durability に乗せる |

**サイズ guard**: `avatar_build.py` が出力 .gvrm が 48MB を超えたら build を 413 で fail させる(Supabase Free 50MB cap の手前)。ユーザーには「Scaniverse から SPZ 形式で再 export してね」と案内。

### バンドル VRM (デフォルト skeleton)
`apps/api/avatar-base/fem_vroid.vrm` (CC0, madjin/vrm-samples)。デプロイ時に Docker image に同梱、ユーザーは自分の VRM をアップしなくても良い。これは DB に登録されない static asset。

### 配信 URL
- LocalStorage 保管 (sources): FastAPI HMAC URL (`/api/v1/assets/{id}/download?token=…`)
- Supabase 保管 (.gvrm): Supabase signed URL (`storage.supabase.co/...?token=…`)

`storage_for_asset(asset).signed_download_url(...)` が kind に応じて自動で適切な URL タイプを返す。

### 何が Supabase Storage に乗るか
- `avatar-gvrm` (≤50MB の出力アーティファクト) ← **NEW**
- `poster-image` (JPEG数百KB)
- `knowledge-*` (PDF / text)
- `tts-audio` (mp3、1セグメント数百KB)

すべて 50MB cap 以下、Free tier 1GB 枠内。

### 制約: 単一インスタンス前提

ソース avatar アセットが Fly volume にあるため、**FastAPI を horizontal scale させない**こと。`apps/api/fly.toml` で以下を強制:

```toml
[[vm]]
  cpus = 1
  memory_mb = 1024   # preprocess の numpy + scipy + spz heap で 256MB は OOM
[deploy]
  strategy = "immediate"   # 単一 machine 入れ替え
[http_service]
  auto_stop_machines = "off"   # BackgroundTask が build 中に kill されないよう
  min_machines_running = 1
```

`fly scale count >1` は今のところ NG (将来 R2/S3 adapter を入れた時に解禁)。

### 永続ボリューム

avatar source 用に **必ずマウント** (build 中の中間ファイルの永続化):

```bash
fly volumes create chimera_uploads --size 3 --region nrt --yes
# fly.toml に [mounts] source="chimera_uploads" destination="/data" は記述済み
# Dockerfile が ENV UPLOAD_ROOT=/data/uploads / AVATAR_BASE_DIR=/app/avatar-base を設定
```

Volume サイズ目安: 3GB で 30〜40 アバター分。一時 build データのみなので大容量は不要。

Volume が消えても **.gvrm 出力は Supabase 側に残ってる**ため、avatar config の `gvrm_asset_id` 経由でユーザーは引き続き利用可能。Source PLY/VRM は再 upload + re-build で復旧。

### バンドル VRM (デフォルト skeleton)

`apps/api/avatar-base/fem_vroid.vrm` を Dockerfile が `/app/avatar-base/` に COPY。**ファイルが repo に存在することを CI で確認**するか、初回 build 前に手動配置:

```bash
cd apps/api/avatar-base
curl -fsSL -o fem_vroid.vrm \
  https://github.com/madjin/vrm-samples/raw/master/vroid/fem_vroid.vrm
```

無いと preprocess が "Default base VRM not found" で fail。

### SPZ encoder ビルド (本番のみ)

`Dockerfile` が `[spz]` extra と build deps (`cmake / build-essential / libzstd-dev / zlib1g-dev / python3-dev / git`) をインストール。Linux Docker でのみ有効。Windows dev では graceful fallback (PLY を SPZ 変換せずそのまま .gvrm に詰める)。

PLY → SPZ 変換が効くことで .gvrm が ~10× 小さくなり Supabase Free 50MB cap 内に収まる。

**設計上の決定 — サーバー側gzip圧縮はしない**:
- アップロードされたbinary blob (.ply/.vrm/.gvrm/.spz) はパススルー保存
- 理由: GVRM自体がブラウザで動く軽量設計、Content-Encodingヘッダの整合性管理(再アップロード時の二重圧縮、CDNプロキシのre-encode、Unity/ブラウザの自動展開保証)を全て正しく扱うコストの方が、得られる削減分より高い
- 大きすぎる場合はSupabase bucket側の File size limit を上げるか、SPZ形式での再アップロードで対処
- 例外: ポスター画像はサーバ側でPDF→JPEGの**コンテンツ変換**を行う(中身を変えるので encoding ではなく canonicalization)

### 2-4. Email認証を有効化

左サイドバー → **Authentication** → **Sign In / Up**

- **Email** → Enable Email Provider: ON
- **Confirm email**: 検証中はOFFにしておくと招待時のメール認証をスキップできる(本番イベントではONを推奨)
- Save

### 2-5. 管理者ユーザーを作成

左サイドバー → **Authentication** → **Users** → **Add user** → **Create new user**

- Email: 自分のメール
- Password: 強いものを設定
- Auto Confirm User: ON (Confirm emailをONにしている場合は必要)
- Create user

作成後、ユーザー行をクリック → **Edit** → **App Metadata** に下記を貼って Save:

```json
{ "app_role": "admin" }
```

これがないと、APIは `member` 扱いで他人のセッションが見えない。

### 2-6. 接続情報を控える (2025年11月以降の新キー体系)

左サイドバー → **Project Settings** (歯車アイコン) → **API Keys**

新プロジェクトには2種類のキーがある:

| キー | 形式 | 用途 | 控える先 |
|---|---|---|---|
| **Publishable key** | `sb_publishable_...` | ブラウザ・モバイル・CLIに**埋めてOK** | `NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY` |
| **Secret key** | `sb_secret_...` | **サーバ専用**。RLSバイパス相当 | `SUPABASE_SECRET_KEY` |

「Reveal」を押して値をコピー。Secret keyは複数発行可能(コンポーネントごとに別keyにすると流出時の影響限定化が容易)。

**Project URL** は同画面の上部:
```
https://<ref>.supabase.co
```
→ `SUPABASE_URL` および `NEXT_PUBLIC_SUPABASE_URL`

#### JWT検証 (新方式: 非対称鍵 + JWKS)

新プロジェクトのJWTは**RS256(非対称)**で署名されているため、共有JWT secretの管理は不要。サーバはJWKSエンドポイントから公開鍵を取得して検証する。

JWKS URL:
```
https://<ref>.supabase.co/auth/v1/.well-known/jwks.json
```

`SUPABASE_URL` を設定しておけば自動でこのURLが導出される。明示指定したい場合は `SUPABASE_JWKS_URL` を設定する。

#### Legacy projectsの場合 (2025年11月より前に作ったプロジェクト)

旧キー方式のままでも動く:
- `Settings → API Keys → Legacy anon, service_role API keys` タブから:
  - `anon` `public` → `NEXT_PUBLIC_SUPABASE_ANON_KEY`
  - `service_role` `secret` → `SUPABASE_SERVICE_ROLE_KEY`
- `Settings → API → JWT Keys → JWT Secret` → `SUPABASE_JWT_SECRET`

新keyへの移行(推奨): `Settings → API Keys` で **"Migrate to new API keys"** ボタン → 両方のkey同時生成 → アプリ側を新keyに切替 → 安定後にlegacy keyを失効。

#### Database接続文字列

**Project Settings → Database → Connection String** タブ:
- **Connection Pooling: ON** + **Transaction Mode** (`:6543`) を選んでコピー
- `[YOUR-PASSWORD]` を 2-1 で控えたパスワードに置換 → `DATABASE_URL`

例:
```
postgresql://postgres.abcdefgh:<your-password>@aws-0-ap-northeast-1.pooler.supabase.com:6543/postgres
```

#### 控えるべき値の最終リスト (新プロジェクト)

```
SUPABASE_URL=https://<ref>.supabase.co
NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY=sb_publishable_...
SUPABASE_SECRET_KEY=sb_secret_...        ← サーバ専用
DATABASE_URL=postgresql://postgres.<ref>:<password>@aws-0-...:6543/postgres
```

JWKS自動導出されるので `SUPABASE_JWT_SECRET` は不要。

#### Legacy projectの場合
```
SUPABASE_URL=https://<ref>.supabase.co
NEXT_PUBLIC_SUPABASE_ANON_KEY=eyJhbGc...
SUPABASE_SERVICE_ROLE_KEY=eyJhbGc...     ← サーバ専用
SUPABASE_JWT_SECRET=<long secret>
DATABASE_URL=postgresql://postgres.<ref>:<password>@aws-0-...:6543/postgres
```

### 2-7. (任意) Auth → URL Configuration

将来 magic link 等を使う場合、左 **Authentication → URL Configuration**:
- Site URL: `https://chimera-dashboard.vercel.app` (Phase 4で確定後に設定)
- Redirect URLs: `https://chimera-dashboard.vercel.app/**`

P0では未設定でOK (email/password loginにリダイレクト不要)。

---

## 3. Fly.io への API デプロイ (15分)

### 3-1. アカウント / ログイン

```bash
# 初回サインアップ
fly auth signup
# 既にアカウントあるなら
fly auth login
```

### 3-2. アプリ登録

`apps/api/fly.toml` は既に同梱済み。

```bash
cd apps/api

# fly.tomlの内容で登録 (deployは保留)
fly launch --copy-config --name chimera-api --region nrt --no-deploy

# Postgres足すか聞かれたら "no" (Supabaseを使うので)
# Redis足すか聞かれたら "no"
```

成功すると `chimera-api.fly.dev` ドメインが確保される。

### 3-3. シークレット投入

ローカルマシン上で(Powershellでも bashでも可):

```bash
fly secrets set \
  APP_ENVIRONMENT=production \
  DEBUG_AUTH_ENABLED=false \
  API_PUBLIC_BASE_URL=https://chimera-api.fly.dev \
  CORS_ALLOW_ORIGINS=https://placeholder-dashboard.vercel.app \
  ASSET_URL_SECRET=$(openssl rand -hex 32) \
  STORAGE_BACKEND=supabase \
  SUPABASE_URL='https://<ref>.supabase.co' \
  SUPABASE_SECRET_KEY='sb_secret_...' \
  SUPABASE_STORAGE_BUCKET=session-assets-private \
  DATABASE_URL='postgresql://postgres.<ref>:<password>@aws-0-ap-northeast-1.pooler.supabase.com:6543/postgres' \
  OPENAI_API_KEY=sk-... \
  OPENAI_MODEL=gpt-5.4-mini \
  OPENAI_EMBEDDING_MODEL=text-embedding-3-large \
  OPENAI_EMBEDDING_DIMENSIONS=1536 \
  ELEVENLABS_API_KEY=sk_... \
  ELEVENLABS_DEFAULT_VOICE_ID=<voice-id>

# Legacy projects (anon/service_role/HS256 JWT secret) — use these instead:
# fly secrets set \
#   SUPABASE_SERVICE_ROLE_KEY='eyJ...service-role-key...' \
#   SUPABASE_JWT_SECRET='<jwt-secret>'
```

`CORS_ALLOW_ORIGINS` は Phase 4 で実 Vercel URL に置換するので、この時点ではプレースホルダで構わない。

Windows PowerShell で `openssl` がない場合:
```powershell
$secret = -join ((48..57) + (97..102) | Get-Random -Count 64 | % {[char]$_})
fly secrets set ASSET_URL_SECRET=$secret
```

### 3-4. デプロイ

```bash
fly deploy
# 初回ビルドは3-5分(Dockerビルド + push + machine起動)
```

成功すると `https://chimera-api.fly.dev` が稼働。

### 3-5. ヘルスチェック

```bash
curl https://chimera-api.fly.dev/health
# → {"status":"ok"}

# DBスキーマがpostgresに作られたか確認
fly logs
# 起動ログに "INFO: Application startup complete" があればOK
```

Supabase側 → Database → Tables を見ると `sessions`, `assets`, `knowledge_chunks` 等が出来ている。`knowledge_chunks` には `embedding vector(1536)` カラムがあるはず。

### 3-6. 起動失敗時のチェック

`fly logs` で頻出の原因:

| ログメッセージ | 原因 | 対処 |
|---|---|---|
| `RuntimeError: DEBUG_AUTH_ENABLED must be false` | secret抜け | `fly secrets set DEBUG_AUTH_ENABLED=false` |
| `RuntimeError: ASSET_URL_SECRET must be set` | secret抜け | `fly secrets set ASSET_URL_SECRET=$(openssl rand -hex 32)` |
| `psycopg.OperationalError ... could not translate host name` | DATABASE_URL 誤り | URI再確認、`<password>`置換忘れ |
| `Supabase upload failed: 400` | bucket名違い | `fly secrets set SUPABASE_STORAGE_BUCKET=session-assets-private` |
| `relation "sessions" does not exist` | 起動ログ前に死んでる | 上のいずれか |

スケール調整(無料枠で**動かしっぱなし**にしたい場合):

```bash
# auto-stopを切って常時起動 (月数百円かかる)
fly scale count 1
fly machines update --metadata auto_stop_machines=off

# auto-stop に戻す
fly machines update --metadata auto_stop_machines=stop
```

---

## 4. Vercel への Dashboard デプロイ (10分)

### 4-1. Git push

```bash
cd <repo root>
git add .
git commit -m "deploy chore"
git push origin main
```

### 4-2. Vercel project import

[vercel.com/new](https://vercel.com/new) を開く

1. **Import Git Repository** → 該当リポジトリ選択
2. **Configure Project**:
   - **Project Name**: `chimera-dashboard` (任意)
   - **Framework Preset**: Next.js (自動)
   - **Root Directory**: **`apps/dashboard`** ← ここがmonorepoの肝
   - **Build Command**: (デフォルトでOK、`vercel.json` が指定済)
3. **Environment Variables** を3つ追加:

| Name | Value |
|---|---|
| `NEXT_PUBLIC_API_BASE_URL` | `https://chimera-api.fly.dev` |
| `NEXT_PUBLIC_SUPABASE_URL` | `https://<ref>.supabase.co` |
| `NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY` | `sb_publishable_...` |

Legacy projectの場合は `NEXT_PUBLIC_SUPABASE_ANON_KEY=eyJhbGc...` を代わりに設定。

4. **Deploy** (2〜3分)

成功すると `https://chimera-dashboard.vercel.app` (実URLはVercelが付ける) が稼働。

### 4-3. Vercel Cliから入れる場合

```bash
cd apps/dashboard
npx vercel link
# プロジェクト紐付け
npx vercel env add NEXT_PUBLIC_API_BASE_URL production
# 値貼り付け
npx vercel env add NEXT_PUBLIC_SUPABASE_URL production
npx vercel env add NEXT_PUBLIC_SUPABASE_ANON_KEY production
npx vercel deploy --prod
```

### 4-4. カスタムドメイン (任意)

Vercel project → **Settings → Domains** → Add → `chimera.your-domain.com`
DNS CNAME設定後、自動でTLS。

---

## 5. CORS と連携の最終調整 (5分)

### 5-1. Fly側にVercel URLを反映

```bash
fly secrets set CORS_ALLOW_ORIGINS=https://chimera-dashboard.vercel.app
fly deploy   # 環境変数反映のため再デプロイ (高速、Dockerビルドは差分)
```

### 5-2. Supabase Auth → Site URL 設定

Supabase Dashboard → **Authentication → URL Configuration** に Vercel URLを設定(magic link等を将来使うため):
- Site URL: `https://chimera-dashboard.vercel.app`
- Additional Redirect URLs: `https://chimera-dashboard.vercel.app/**`

### 5-3. ブラウザ確認

1. `https://chimera-dashboard.vercel.app/login` を開く
2. **"Sign in with the email/password..."** が表示される(supabaseEnabled判定が効いている証)
3. 2-5で作った admin email/password でログイン
4. `/sessions` に飛ぶ。空リストでもエラーは出ない
5. Network タブで `/api/v1/me` 呼び出しに `Authorization: Bearer <jwt>` が乗っていることを確認

### 5-4. End-to-End試し

ローカルで通したフローと全く同じ操作を本番で:

1. New session
2. Poster image upload (Supabase Storageに保存される)
3. Panel登録
4. Avatar `.gvrm` upload
5. Knowledge upload (PDF or text)
6. Script + Simulated Q&A生成 + 全Approve
7. TTS一括生成
8. Publish
9. `/sessions` 一覧の "View Manifest" でJSON確認 → `assets.posterImage.url` がSupabase Storageの `https://<ref>.supabase.co/storage/v1/object/sign/...` 形式になっていればOK

### 5-5. ログ監視

```bash
fly logs --app chimera-api
```

エラーが出る代表例とその意味:
- `Supabase sign failed: 404 ... not found` → bucket名違い or path違い
- `403 Forbidden` from /api/v1/sessions → Supabase userの`app_metadata.app_role`未設定 (memberのまま)

---

## 6. Unity Client側の設定 (5分)

`apps/xr-client` を Unity Hub で開いた状態で:

1. シーン上の `ChimeraSession` GameObject Inspector:
   - **Api Base Url**: `https://chimera-api.fly.dev`
   - **Session Code**: ダッシュボードでpublishしたsessionの `session_code` (例 `6S2C76`)
   - **Default Profile**: `master`
   - **Default Language**: `ja`
   - **Auto Boot On Start**: ON
2. Play

`ChimeraSession.OnManifestLoaded` が発火 → `_avatar_manifest` のbundleがダウンロード → `GvrmZipReader` が分解 → `OnGvrmLoaded`で UniVRM/Splat描画(これはUnity実装側で)。

`OnQaAnswer` が発火する確認:
```csharp
chimeraSession.OnQaAnswer += a => Debug.Log($"answer={a.DraftAnswer}");
await chimeraSession.AskAsync("この研究の主な貢献は？");
```

---

## 7. 本番動作確認チェックリスト

| 項目 | 確認方法 | 期待 |
|---|---|---|
| API health | `curl https://chimera-api.fly.dev/health` | `{"status":"ok"}` |
| Postgres接続 | `fly logs` | `Application startup complete`、エラーなし |
| pgvector | Supabase → Database → `knowledge_chunks` のカラム | `embedding vector(1536)` あり |
| Storage write | Dashboard でposter upload | Supabase → Storage → bucketに `sessions/<id>/poster/<uuid>.png` |
| Storage read | Dashboard で /poster page にposterが表示される | サインドURLから画像が返る |
| Auth | dashboardでログイン後、`/api/v1/me` | `{"user_id":"<supabase uuid>","role":"admin"}` |
| TTS | TTS生成後 `/scripts` の audio.url を踏む | mp3が再生される |
| Manifest | `curl https://chimera-api.fly.dev/api/v1/runtime/<code>/manifest` | 全フィールド埋まる |
| WebSocket | Unity or `wscat -c "wss://chimera-api.fly.dev/api/v1/runtime/<code>/questions?token=..."` | `{"type":"ready",...}` |

---

## 7.5. データベースマイグレーション (Alembic)

スキーマ変更は **Alembic** で管理されます。Dockerコンテナの起動時に毎回 `alembic upgrade head` が走るので、新しいマイグレーションは `git push → fly deploy` で自動適用されます。

### 既存のSupabase Postgresで初回だけ必要な作業(stampコマンド)

最初の `fly deploy` でmigrationが流れる前に、現状のスキーマを「最新版」と alembic に教える必要があります。テーブルは既に存在しているので、`alembic upgrade head` を素朴に走らせると `relation "sessions" already exists` で失敗します。

ローカルから1回だけ実行:

```bash
cd apps/api
DATABASE_URL='postgresql://postgres.<ref>:<password>@aws-0-...:6543/postgres' \
  .venv/Scripts/python.exe -m alembic stamp head
```

これで `alembic_version` テーブルが作られて current revision が登録されます。次回以降は通常通り migration 追加→`fly deploy` で前進。

### 新しいマイグレーションを生成する流れ

1. モデル(`apps/api/app/models.py`)を編集
2. `alembic revision --autogenerate -m "add foo column"` でmigrationスケルトン生成
3. 生成されたファイルを目視で確認(autogenerateは完璧ではない)
4. ローカル(SQLite)で `alembic upgrade head` → 動作確認
5. commit + push → Fly.io が deploy 時に自動で `alembic upgrade head`

### 注意

- `pgvector` 拡張と `knowledge_chunks.embedding vector(N)` カラムだけは alembic 管理外(動的次元のため)。`_ensure_postgres_schema()` が起動時に冪等保証
- ローカル開発(SQLite)は `Base.metadata.create_all` で動くので alembic 操作は本番(Postgres)時のみ気にすればOK


## 8. 運用 (ログ・更新・rollback)

### 8-1. ログを見る

```bash
# Fly: 直近ストリーム
fly logs --app chimera-api

# Vercel: project → Logs タブ
```

### 8-2. シークレット差し替え

```bash
fly secrets set OPENAI_API_KEY=sk-newkey
# 自動でreloadされる(machine再起動)
```

### 8-3. コード更新の再デプロイ

```bash
# API
cd apps/api && fly deploy

# Dashboard (Vercel)
git push origin main
# main へのpushで自動デプロイ
```

### 8-4. Rollback

```bash
# Fly
fly releases --app chimera-api
fly releases rollback v<n> --app chimera-api

# Vercel
# Vercel UI → Deployments → 過去のdeployの「⋯」→ Promote to Production
```

### 8-5. DB バックアップ (Supabase)

Supabase Free planは自動バックアップが7日。手動ダンプ:
```bash
# Supabase Database → Connect → psql接続例をコピーして
pg_dump "<connection-string>" > backup-$(date +%Y%m%d).sql
```

### 8-6. スケールアップ判断ライン

| 症状 | 対応 |
|---|---|
| Free DB 500MB枠超 | Supabase Pro $25/月、または別Postgres(Neon $0/3GB) |
| Storage 1GB 枠超 | Supabase Pro $25/月、または S3互換でadapter拡張 |
| Fly machine OOM(256MB不足) | `fly scale memory 512` ($1.94→$3.89/月) |
| Cold start体感遅い | `fly machines update --metadata auto_stop_machines=off` で常時起動($約$2/月) |

---

## 9. トラブルシュート Quick Ref

| 症状 | 原因候補 | 一次対応 |
|---|---|---|
| Vercel build error `Module not found '@supabase/ssr'` | npm install済んでない | Vercelで再Build または `vercel --force` |
| Login時 "Invalid login credentials" | Confirm email がONなのに招待メール未確認 | Auth → Users → "Send invite" / Confirm email を一時OFFに |
| `/api/v1/me` で 401 | `Authorization: Bearer` が飛んでない | Browser Devtools NetworkタブでHeader確認、`NEXT_PUBLIC_SUPABASE_URL` が空ならdebug fallbackになっている |
| `/api/v1/me` で 403 | role=member 扱い | Supabase user の `app_metadata.app_role = "admin"` を確認 |
| poster upload で 500 | Supabase bucket 名違い | `fly secrets set SUPABASE_STORAGE_BUCKET=session-assets-private` |
| asset URL クリック時 401 | tokenの期限 (300sec) 切れ | manifestを再取得 |
| WebSocket connect closes 4401 | runtimeToken期限切れ or 未publish | 直前にmanifestを取得しなおす |
| WebSocket connect closes 4403 | session.status != published | dashboardから publish 押す |
| Fly machine が止まる | `auto_stop_machines=stop` でアイドル | XREAL接続前に `curl /health` を1回叩いてwakeup or auto_start onで自動 |
| pgvector index作成失敗 | extension未有効 | Supabase → Database → Extensions → vector ON |
| OpenAI 429 | API key の rate limit | ダッシュボードからretry / 別keyに切替 |

---

## 10. シークレットの一覧 (cheat sheet)

```text
[Fly.io API secrets]
APP_ENVIRONMENT=production
DEBUG_AUTH_ENABLED=false
API_PUBLIC_BASE_URL=https://chimera-api.fly.dev
CORS_ALLOW_ORIGINS=https://chimera-dashboard.vercel.app
ASSET_URL_SECRET=<openssl rand -hex 32>
STORAGE_BACKEND=supabase
SUPABASE_URL=https://<ref>.supabase.co
SUPABASE_SERVICE_ROLE_KEY=<service-role-key>     ★ サーバ専用
SUPABASE_JWT_SECRET=<jwt secret>
SUPABASE_STORAGE_BUCKET=session-assets-private
DATABASE_URL=postgresql://postgres.<ref>:<password>@aws-0-ap-northeast-1.pooler.supabase.com:6543/postgres
OPENAI_API_KEY=sk-...
OPENAI_MODEL=gpt-5.4-mini
OPENAI_EMBEDDING_MODEL=text-embedding-3-large
OPENAI_EMBEDDING_DIMENSIONS=1536
ELEVENLABS_API_KEY=sk_...
ELEVENLABS_DEFAULT_VOICE_ID=...

[Vercel Dashboard env]
NEXT_PUBLIC_API_BASE_URL=https://chimera-api.fly.dev
NEXT_PUBLIC_SUPABASE_URL=https://<ref>.supabase.co
NEXT_PUBLIC_SUPABASE_ANON_KEY=<anon public key>
```

`service_role` は **絶対** ブラウザ・Vercel `NEXT_PUBLIC_*`・Unity client・gitに**出さない**。
`anon public` は前面OK(rowレベルセキュリティで守る前提)。

---

## 11. 撤収・再構築

```bash
# Fly app削除
fly apps destroy chimera-api

# Vercel: プロジェクトSettings → Advanced → Delete Project

# Supabase: Project Settings → Danger Zone → Delete Project

# 再構築は本ドキュメント Phase 2 から再実行で5-10分
```

---

## 付録: Manifest snapshot を Unity 開発に渡す

開発者が API起動なしで Unity 動かしたい場合:

```bash
# 公開済みmanifestをUnity StreamingAssetsへコピー
curl https://chimera-api.fly.dev/api/v1/runtime/<code>/manifest \
  -o apps/xr-client/Assets/StreamingAssets/manifest.example.json
```

`ChimeraSession` を一時的に bypass して直接 deserializeするコードはREADME `apps/xr-client/README.md` 「Offline development」項参照。
