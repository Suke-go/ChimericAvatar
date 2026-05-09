# Workers

P0では独立workerを起動しない。FastAPI内の`jobs` table + task実行で開始する。

分離条件:

```text
複数sessionのscript/TTS生成が詰まる
poster OCRやavatar bundle buildが長時間化する
API応答とjob実行を別scaleにしたい
```

分離時の採用stack:

```text
Python 3.13
Celery
Redis
OpenAI Python SDK
ElevenLabs SDK
PyMuPDF
Pillow
numpy
jsonschema
```

Job:

- `ingest_paper`
- `extract_poster_text`
- `embed_knowledge_chunks`
- `generate_script_plan`
- `write_script_segments`
- `check_script_evidence`
- `synthesize_tts`
- `build_avatar_bundle`
- `convert_scaniverse_asset`

Job設計:

- job inputはDB idだけにする。
- asset bytesはprivate storageから読む。
- retry可能にする。
- outputはidempotentにする。
- event logへstate transitionを書く。
- Scaniverse由来PLY/SPZはGaussian-VRM authoring互換のintermediateへ変換する。
