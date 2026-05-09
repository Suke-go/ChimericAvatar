# Chimera Presenter 検証計画

## 1. 目的

本番設計の不確実性を、実装前または実装初期に潰す。

特に検証するもの:

- XREAL SDK + Unity 6000 LTS + Android build。
- XREAL One Pro + XREAL Eye + XREAL Beam Pro。
- A0 poster portrait / landscape image tracking。
- UniVRM runtime load。
- Gaussian Splatting rendererのAndroid/XR性能。
- OpenSource Gaussian-VRM由来`.gvrm` metadataの互換性。
- Scaniverse由来human Gaussian Splatting assetの変換経路。
- Poster image recognitionとQR fallback。
- Poster presentation motion。
- Supabase Auth / Storage / pgvectorの本番境界。
- Script Agentのevidence-linked structured output。
- ElevenLabs live TTS latency。

## 2. P0 Spike

| ID | 検証 | 実装場所 | 合格条件 | 失敗時 |
|---|---|---|---|---|
| U-01 | Unity 6000 LTS + XREAL SDK 3.1.0 import | `apps/xr-client` | compile errorなし、Android build可能 | package/version conflictを記録しUnity 6000系で解消 |
| U-02 | XREAL One Pro + Eye + Beam Pro setup | `apps/xr-client` | firmware更新済み、Eye認識、Beam Pro hostでsample起動 | firmware/MyGlasses更新手順を修正 |
| U-03 | A0 Poster Image Tracking | `apps/xr-client` | A0縦`[0.841,1.189]`またはA0横`[1.189,0.841]`で`PosterAnchorRoot`生成 | Dashboard発行QRを補助anchorにする |
| U-04 | UniVRM load | `apps/xr-client` | default VRMがruntime loadされAnimator制御可能 | UniVRM versionを固定しUnity 6000 LTS内で解消 |
| U-05 | gsplat-unity Android Vulkan benchmark | `apps/xr-client` | 60k splatsがtarget deviceで実用fps | custom minimal rendererを先行 |
| U-06 | Gaussian-VRM `.gvrm` unzip + metadata parse | `apps/xr-client` | `naruya/gaussian-vrm`由来fixtureの`data.json`がschema validationを通る | converterでmetadata normalize |
| U-07 | Presentation motion smoke | `apps/xr-client` | Idle/PointToPoster/Listeningがdefault VRMで動く | Animator clipとRig constraintを分離して調整 |
| U-08 | Scaniverse human asset path | `apps/workers` | PLY/SPZからruntime bundle用intermediateを作れる | PLY経路を先行しSPZは後段 |
| B-01 | Supabase JWT verification in FastAPI | `apps/api` | invalid/expired JWT拒否、valid JWT許可 | Auth provider再検討 |
| B-02 | Supabase signed URL | `apps/api` | private asset URLが期限切れ後に拒否 | R2/S3 signed URLへ切替 |
| B-03 | Unity mobile authenticated download | `apps/api`, `apps/xr-client` | mobile data前提で認証情報入力、manifest、signed assetsをdownloadできる | device code flow / magic linkを追加 |
| R-01 | pgvector session-filtered retrieval | `apps/api` | 他session chunkが混入しない | DB policy/query guard追加 |
| A-01 | Script Agent structured output | `apps/workers` | beginner/master/professional x ja/enでschema-valid JSON、evidence id実在 | prompt分割/validator強化 |
| T-01 | ElevenLabs live TTS | `apps/api` | first audio chunk <= 2 sec | HTTP streaming/cache優先 |

## 3. P1 Spike

| ID | 検証 | 合格条件 |
|---|---|---|
| U-09 | 1 bone skinned splat deformation | bone回転でsplat cloudが追従 |
| U-10 | full humanoid animation deformation | Idle/Pointingで破綻しない |
| U-11 | Animation Rigging pointing | poster panel / figure regionへ腕IKで指差しできる |
| U-12 | Timeline segment sync | audio、caption、gesture cueがsegment単位で同期 |
| U-13 | bone batch culling | batch boundsで描画数削減が測定可能 |
| U-14 | LOD switching | frame timeに応じてLODが上下する |
| B-03 | Job retry/idempotency | DB job再実行でasset重複なし。必要になったらCeleryへ移しても同じjob tableを使える |
| R-02 | hybrid retrieval eval | vector + full textでtop-k改善 |
| A-02 | Evidence checker regression | unsupported fixtureを検出 |
| T-02 | TTS cache determinism | same inputでcache hit |

## 4. P2 Spike

| ID | 検証 | 合格条件 |
|---|---|---|
| U-15 | XREAL stereo splat rendering | 片目だけ欠ける、depth破綻がない |
| U-16 | thermal test | 10分demoで端末が実用範囲 |
| R-03 | escalation load | 複数質問の並行処理でDashboard sync維持 |
| S-01 | security audit | session_id漏れ、asset直リンク、JWT bypassなし |
| E-01 | analytics export | latency、QA、profile選択、escalationをCSV/JSON出力 |

## 5. CI Gate

最低限のCI gate:

```text
pnpm install
pnpm lint
pnpm typecheck
pnpm test
pnpm test:e2e
uv sync
uv run pytest
jsonschema validate packages/schema/runtime-manifest.schema.json packages/schema/runtime-manifest.example.json
```

UnityはCI導入前でも、ローカル検証ログを保存する。

```text
Unity Editor version, fixed to Unity 6000 LTS
XREAL SDK version
UniVRM version
Animation Rigging package version
Android graphics API
Device model
Splat count
Average FPS
Frame time p95
Thermal/throttle observation
```

## 6. 実装停止条件

次の場合は先に設計を戻す。

- XREAL + Unity 6000 LTS + VulkanでGaussian splat描画が成立しない。
- UniVRMとGVRM bindingに必要なskinned mesh情報がruntimeで安全に取れない。
- Supabase Storage signed URLだけではasset保護要件を満たせない。
- Script Agentのunsupported claim検出率が低く、承認前提でも危険。
- Live TTS latencyが展示体験として許容できない。
