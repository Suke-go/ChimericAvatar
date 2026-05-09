# Realtime App

P0ではこのappは分離しない。FastAPI内のWebSocket/SSEで開始する。

分離条件:

- Live Q&A接続数がAPI workerと独立scalingを必要とする。
- Audio streaming latencyがAPIの通常request負荷に影響される。
- Dashboard SSEとUnity WebSocketを別deploy unitにしたい。

分離時の採用候補:

```text
FastAPI WebSocket service
Redis Streams / PubSub
OpenTelemetry
```

Node/NestJSへ寄せる案は保留。LLM/TTS provider clientとPython workerの再利用を優先し、まずPython realtimeで検証する。
