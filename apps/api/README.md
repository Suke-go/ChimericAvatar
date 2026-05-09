# API App

詳細設計:

- ../../docs/web-implementation-design.md

採用stack:

```text
Python 3.11+
FastAPI
Pydantic v2
SQLAlchemy 2
asyncpg / psycopg
pgvector
OpenAI Python SDK
httpx
websockets
PyJWT
jsonschema
pytest
```

P0ではCeleryを入れず、DBの`jobs` table + FastAPI taskで開始する。長時間処理が詰まり始めたらCelery/Redisへ移す。

責務:

- Supabase JWT verification。
- admin/member access control。
- session / asset / knowledge / script / manifest API。
- Dashboard SSE。
- Unity runtime manifest。
- Runtime Q&A WebSocket。
- Unity mobile clientの認証情報入力、runtime token発行、signed asset download。
- Evidence-Governed Knowledge Systemのanswerability判定とescalation。

P0では`apps/realtime`を分けず、FastAPI内にWebSocket/SSEを実装する。負荷やdeploy境界が必要になった段階で`apps/realtime`へ分離する。

TTS設定:

```text
ELEVENLABS_API_KEY=...
ELEVENLABS_DEFAULT_VOICE_ID=...
ELEVENLABS_MODEL_ID=eleven_multilingual_v2
ELEVENLABS_STREAMING_MODEL_ID=eleven_flash_v2_5
ELEVENLABS_OUTPUT_FORMAT=mp3_44100_128
```

事前生成TTSはFastAPIがElevenLabs Text-to-Speech streaming endpointを呼び、音声をprivate assetとして保存する。Unity/XREALはElevenLabsへ直接接続せず、manifest内の署名付きURLまたは将来のChimera WebSocket gatewayから受け取る。

Knowledge自動取り込み:

```text
OPENAI_API_KEY=...
OPENAI_MODEL=gpt-5.4-mini
OPENAI_OCR_MODEL=gpt-5.4-mini
OPENAI_REASONING_EFFORT=low
```

- PDF: PyMuPDFでselectable textを抽出する。
- text/markdown: UTF-8 textとして直接chunk化する。
- poster image: OpenAI Responses APIのimage input + Structured OutputsでOCR JSONを受け取る。
- Script Agent: OpenAI Responses APIのStructured Outputsでsegment配列を生成する。API key未設定時はdeterministic generatorへfallbackする。

Supabase設定:

```text
SUPABASE_URL=https://<project-ref>.supabase.co
SUPABASE_SECRET_KEY=sb_secret_...
SUPABASE_JWKS_URL=https://<project-ref>.supabase.co/auth/v1/.well-known/jwks.json
SUPABASE_JWT_AUDIENCE=authenticated
SUPABASE_STORAGE_BUCKET=session-assets-private
STORAGE_BACKEND=supabase
```

- Web/モバイル公開側は `sb_publishable_...` を使う。
- Backendだけが `sb_secret_...` を持つ。旧projectでは `SUPABASE_SERVICE_ROLE_KEY` でも動くが、新規設定では使わない。
- Auth JWTはSupabaseのJWT Signing Keysで発行し、FastAPIはJWKSで検証する。旧HS256 projectだけ `SUPABASE_JWT_SECRET` をfallbackとして使う。
