# Chimera Presenter 本番設計方針

## 1. 設計概要

Chimera Presenterでは、`.gvrm`を共通アバター資産形式として採用する。

OpenSourceの[Gaussian-VRM](https://github.com/naruya/gaussian-vrm)を、Gaussian Splatting人間scanをVRM骨格へbindingする資産生成・仕様参照の中心に置く。本番のXREALクライアントでは、Unity 6000 LTS上のGVRM Runtimeで`.gvrm`または最適化済みruntime bundleを描画する。

実装背景、採用ライブラリ、検証spike、schema、CI gateは次を正本にする。

- [実装背景と参照文献](implementation-background-and-references.md)
- [技術詳細設計](technical-blueprint.md)
- [Web実装詳細設計](web-implementation-design.md)
- [検証計画](verification-plan.md)
- [Evidence-Governed Knowledge設計](knowledge-governance-design.md)

```text
Web Dashboard:
  Authoring / Preview / 変換確認 / Script承認

Backend:
  Auth / Knowledge ingestion / Script Agent / Q&A / TTS / Asset signing / Analytics

XREAL Unity Client:
  XREAL One Pro + XREAL Eye + XREAL Beam Pro /
  Poster Image Tracking / Poster Anchor / Unity Native GVRM Runtime /
  Audio / Captions / Controller Input / Secure Session
```

この判断により、AR座標、ステレオ描画、音声、字幕、スマホ/Beam Pro操作、セキュリティをUnity側で一貫して管理する。

## 2. プロダクト構成

Chimera Presenterは、研究ポスター前にARアバターを配置し、来場者に研究内容を説明し、質問にはEvidence-Governed Knowledge Systemで回答し、答えられない質問は発表者Dashboardへエスカレーションするシステムである。

```text
Chimera Presenter Production

[Web Dashboard]
  - 論文、ポスター、発表メモ、対象聴衆を登録
  - Gaussian-VRM / VRM / animation / voice asset管理
  - 説明スクリプト生成、編集、承認
  - ElevenLabs voice_id / TTS cache管理
  - Q&A escalation / analytics
  - runtime session manifest発行

[Backend]
  - Auth / Admin-gated data access
  - Paper knowledge ingestion
  - Script Agent
  - Q&A Agent
  - Evidence Store / Retrieval / Approval Boundary
  - ElevenLabs streaming gateway
  - Asset signing / CDN
  - Event log / Dashboard sync

[XREAL Unity Client]
  - XREAL One Pro + XREAL Eye + XREAL Beam Pro
  - XREAL Poster Image Tracking / Anchor
  - GVRM Unity Runtime
  - Avatar behavior controller
  - Audio streaming player
  - Smartphone / Beam Pro controller
  - AR captions / panel guidance
  - Secure session client
```

## 3. Gaussian-VRM統合方針

### 使用するもの

- `.gvrm`をAuthoring/保存/交換用の標準形式として採用する。
- OpenSourceの`naruya/gaussian-vrm`を、`.gvrm`生成、`data.json`仕様、skinned Gaussian avatarの挙動確認に使用する。
- 人間のGaussian Splatting scanは、Scaniverse由来のPLY/SPZを主要入力として想定する。
- SPZはmobile downloadに有利なcompressed splat形式として扱い、asset processorでPLYまたはruntime binaryへ変換する。
- `.gvrm`内の基本構成を維持する。

```text
.gvrm
  - model.vrm
  - model.ply
  - data.json
```

- `data.json`を、VRM骨格とGaussian splatsを結びつけるbinding metadataとして扱う。
- Unity Runtimeでは少なくとも次の情報を読む。

```text
modelScale
boneOperations
gsPosition
gsQuaternion
splatVertexIndices
splatBoneIndices
splatRelativePoses
```

### 実装境界

Web DashboardはGaussian-VRM preview、変換確認、asset approvalを担当する。XREAL Unity Clientはruntime manifestを受け取り、Unity側でVRM骨格、Gaussian splats、motion、audio、caption、poster anchorを統合する。

## 4. Poster / XREAL前提

PosterはA0縦またはA0横を基本にする。

```text
A0 portrait:
  841 mm x 1189 mm
  physicalSizeM = [0.841, 1.189]

A0 landscape:
  1189 mm x 841 mm
  physicalSizeM = [1.189, 0.841]
```

XREAL本番機材は次で固定する。

```text
Glasses:
  XREAL One Pro

Camera / 6DoF:
  XREAL Eye

Host:
  XREAL Beam Pro

SDK:
  XREAL SDK for Unity 3.1.0

Unity:
  Unity 6000 LTS
```

開発・検証hostとしてSamsung S25を許容する。運用手順には、XREAL One Pro / XREAL Eye / Beam Proのfirmware updateを含める。

## 5. Unity GVRM Runtime方針

Unity Runtimeは、`.gvrm`または最適化済みavatar bundleを読み込み、VRM rig、Gaussian splats、binding metadataを統合して描画する責務を持つ。

推奨モジュール境界:

```text
Assets/Chimera/Runtime/Avatar/Gvrm/
  GvrmZipLoader.cs
  GvrmMetadata.cs
  VrmRigLoader.cs
  PlySplatLoader.cs
  GvrmBindingData.cs
  SkinnedGaussianRenderer.cs
  SplatBoneSorter.cs
  AvatarAnimationController.cs
  GvrmAvatarController.cs
```

Runtime責務:

- `.gvrm`またはruntime bundleをロードする。
- `model.vrm`をUniVRMで読み込む。
- `model.ply`からGaussian splatsを読む。
- `splatVertexIndices`、`splatBoneIndices`、`splatRelativePoses`でsplatsをVRM rigへbindする。
- Unity Animator / Timeline / Animation Rigging / Runtime AnimationClipでアバター動作を制御する。
- `GraphicsBuffer` / `StructuredBuffer`を使ってGPU側でskinned splatを描画する。

Gaussian-VRMの考え方は「人間のGaussian SplattingにVRM骨格を入れて動かす」設計として扱う。Unity Runtimeでは、その資産仕様とskinning logicを読み、Unity Humanoid animationでposter presentation風のmotionへ接続する。

Skinned Gaussian Splatの基本ルール:

```text
各splatについて:
  1. 紐づくVRM mesh vertexを解決する。
  2. 初期skinning transformと現在skinning transformを読む。
  3. その差分でsplat relative poseを変形する。
  4. avatar/world space上のsplat centerとして描画する。
```

## 6. Runtime Asset方針

`.gvrm`はAuthoring/保存形式とする。XREAL本番アプリには、可能な限り最適化済みruntime bundleを配布する。

```text
original.gvrm
  -> server-side asset processor
  -> avatar.bundle
       - avatar.vrm
       - splats.bin
       - splat_batches.bin
       - binding.bin
       - metadata.json
       - preview.glb / thumbnail.webp
```

これにより、モバイル端末上でのPLY parse、ZIP展開、メモリアロケーションを抑え、ロード時間と発熱を管理しやすくする。

入力経路:

```text
Scaniverse human scan
  -> PLY or SPZ
  -> Gaussian-VRM authoring / rig binding
  -> .gvrm
  -> avatar.bundle for Unity
```

モデルが存在しない場合は、標準人型VRMをdefault avatarとして使う。default avatarは、Script、TTS、Q&A、poster anchor、motion検証を同じruntime経路で進めるための標準実装である。

## 7. Avatar Motion方針

Poster presentationらしい動きは、Unityの既存animation機能を活用して実装する。

採用package:

```text
Unity Animator / Mecanim Humanoid
Unity Timeline
Unity Animation Rigging
Input System
XR Interaction Toolkit
```

Motion layer:

```text
Base locomotion:
  Idle / Breathing / Listening / Thinking

Presentation gestures:
  ExplainIntro / PointToPoster / PointToFigure / EmphasisGesture /
  OpenPalm / PanelTransition / Thanks

Procedural rig:
  HeadLookAtViewer
  EyeLookAtPoster
  RightArmPointIK
  LeftHandSupportGesture
```

実装:

- VRMはHumanoid Avatarとしてimportし、Unity Animatorでanimation clipをretargetする。
- Gaussian splatsはVRM骨格の現在bone matrixに追従する。
- `Animation Rigging`のMulti-Aim Constraintで頭・視線をviewer/posterへ向ける。
- `Animation Rigging`のTwo Bone IKで腕をposter panel / figure regionへ向ける。
- `Timeline`はscript segmentごとのmotion、caption、audio、panel focusを同期する。
- Mixamo等のHumanoid FBX motionは、ライセンス確認後にpresentation gestureの初期assetとして使える。

## 8. Performance方針

XREALでは両眼描画、Android GPU、発熱、バッテリー制約を前提にする。Performance設計は後付けではなく初期設計に含める。

必須方針:

- 距離とframe timeに基づくLOD切替。
- `splatBoneIndices`に基づくbone batch culling。
- Gaussian Avatar専用render pass。
- GPU buffer reuse。
- 本番セッション中の重いPLY parseを避ける。

LOD目安:

```text
LOD0: 300k splats
LOD1: 150k splats
LOD2: 60k splats
LOD3: 20k splats / silhouette mode
```

Frame制御:

```text
frameMsが目標を超えたらLODを下げる。
frameMsが数秒安定して低ければLODを上げる。
```

## 9. XREAL Client方針

XREAL本番クライアントはUnityネイティブARアプリとする。

想定stack:

```text
Unity 6000 LTS
XREAL SDK for Unity 3.1.0
XREAL One Pro
XREAL Eye
XREAL Beam Pro
AR Foundation
XR Interaction Toolkit
Input System
UniVRM
Animation Rigging
Timeline
```

Tracking方針:

- ポスター画像そのものをImage Tracking targetとして`PosterAnchorRoot`を決定する。
- tracking targetの実寸はA0縦`[0.841, 1.189]`またはA0横`[1.189, 0.841]`でmanifestに入れる。
- Avatar、caption、guidance panelはposter anchor相対で配置する。
- ポスター全面をcm単位で完全追跡する前提にはしない。
- 認識安定性が不足する場合だけ、DashboardからQR markerを発行し、poster cornerまたは補助紙面へ配置する。

Input方針:

- 主操作はSmartphone / Beam Pro controller。
- Hand trackingは副操作。
- Dashboardからのremote commandで発表者が説明モード、停止、直接回答を制御できる。

標準操作:

```text
Tap / Trigger: 再生、一時停止、次へ
Swipe right: 次のパネル
Swipe left: 前のパネル
Swipe up: もっと専門的に
Swipe down: もっと簡単に
Long press: 質問録音開始 / 終了
App Button: メニュー
```

## 10. Knowledge / Script Agent方針

説明スクリプト生成とLive Q&Aは、単純なRAGではなく、CHI研究で求められる情報管理、根拠管理、発表者承認、対象者別説明、ログ分析を含むEvidence-Governed Knowledge Systemとして設計する。

Pipeline:

```text
1. Knowledge Ingestion Agent
2. Claim Extraction Agent
3. Audience / Language Modeling Agent
4. Script Planner Agent
5. Script Writer Agent
6. Evidence Checker Agent
7. Style / Voice Agent
8. Safety / Boundary Agent
9. TTS Chunking Agent
10. Human Approval
```

入力:

- Paper: PDF / LaTeX / abstract / sections / figures / captions
- Poster: image / OCR / panel regions / figure positions / marker info
- Author Notes: 強調点、限界、言ってはいけない推測、想定質問、謝辞
- Audience Profiles: `beginner`、`master`、`professional`
- Languages: `ja`、`en`

公開されるscript segmentは、evidence idと紐づき、人間の承認を経たものだけにする。

Profile定義:

```text
beginner:
  HighSchool相当。前提知識を少なくし、専門用語を避ける。

master:
  Bachelor / Master相当。研究背景、方法、結果、限界を標準的に説明する。

professional:
  PhD相当。貢献、関連研究との差分、評価可能性、限界を明示する。
```

## 11. Live Q&A方針

Live Q&AはScript Agentとは分離する。

```text
Q&A Agent
  - STT transcript cleanup
  - active panel context
  - evidence retrieval
  - answerability判定
  - streaming LLM answer
  - citation / evidence保存
  - TTS streaming
  - escalation
```

応答flow:

```text
LLM token stream
  -> sentence buffer
  -> TTS chunk
  -> audio stream to Unity
  -> subtitle stream to Unity
```

根拠が弱い、session外、未承認、危険、または推測が必要な質問は、回答を捏造せずDashboardへescalationする。

## 12. Voice / TTS方針

事前説明はpre-rendered TTS cacheを使う。Live Q&AはWebSocket streaming TTSを使う。

Voice cloningを使う場合は、voice consentをDBで明示的に管理する。

```sql
voice_consents (
  id uuid primary key,
  user_id uuid,
  voice_provider text,
  voice_id text,
  consent_type text,
  consent_version text,
  source_audio_asset_id uuid,
  created_at timestamptz
);
```

音声種別は少なくとも次を区別する。

- 本人の声。
- 本人が共有した第三者利用可能voice。
- default synthetic voice。

## 13. Security / Mobile Download方針

本番では、session_codeだけで管理機能にアクセスできる設計を禁止する。

Auth / Access Control:

- OAuth、Magic link、Passkeyのいずれかを使う。
- 研究室運用では`admin`と`member`の2 roleにする。
- `admin`だけが契約DB内の全session、全asset、全logへアクセスできる。
- `member`は自分が作成したsessionだけを管理できる。
- Dashboard frontendはDBへ直接read/writeせず、FastAPI経由で操作する。

Asset security:

- `.gvrm`、`.vrm`、poster、audio、bundleはprivate bucketに置く。
- Unity clientには短期限のsigned URLだけを渡す。
- 公開manifestには必要最小限の情報だけを含める。
- Unity mobile clientはモバイルデータ通信上で認証情報を入力し、APIからruntime manifestとsigned asset URLを取得する。
- Unity clientはDBへ直接接続せず、Auth/API/Storage経由でdownloadする。

Knowledge security:

- retrievalは必ず`session_id`でfilterする。
- uploaded knowledgeとsystem instructionを混ぜない。
- user questionとuploaded documentはuntrusted inputとして扱う。
- answerはevidence IDsと一緒に保存する。
- 根拠がない場合はescalationする。

Logging:

- 保存する: question transcript、answer、evidence IDs、latency、audience mode、escalation reason。
- 最小化する: raw audio、camera frame、face image、不要な個人情報。

## 14. Runtime Manifest方針

Unity clientは、runtime manifestとsigned assetsだけでpublished sessionを再生できるようにする。

Manifestが持つ責務:

- tracking設定。
- avatar runtime asset URL。
- avatar placement。
- behaviorとanimationの対応。
- profile別script segments。
- audio URL。
- Q&A WebSocket URL。
- schema versionとfeature flags。

基本形:

```json
{
  "schemaVersion": "1.0",
  "sessionCode": "K7M3PQ",
  "tracking": {
    "type": "xreal-image-tracking",
    "referenceImageName": "poster_a0_v1",
    "posterFormat": "A0",
    "orientation": "portrait",
    "physicalSizeM": [0.841, 1.189],
    "qrFallback": {
      "enabled": true,
      "markerName": "poster_qr_v1",
      "placementHint": "poster_corner"
    },
    "anchorOffset": {
      "position": [0, 0, 0],
      "rotation": [0, 0, 0]
    }
  },
  "avatar": {
    "runtimeType": "unity-gvrm",
    "assetBundleUrl": "signed-url",
    "defaultAnimation": "Idle",
    "placement": {
      "relativeTo": "poster_anchor",
      "position": [0.65, -0.2, -0.25],
      "rotation": [0, -20, 0],
      "scale": 1.0
    }
  },
  "qa": {
    "websocketUrl": "wss://api.example.com/runtime/K7M3PQ/questions",
    "defaultProfile": "master",
    "defaultLanguage": "ja",
    "supportedProfiles": ["beginner", "master", "professional"],
    "supportedLanguages": ["ja", "en"]
  }
}
```

## 15. Repository構成方針

本番repositoryはmonorepoを基本とする。

```text
chimera-presenter/
  apps/
    dashboard/
    api/
    realtime/
    workers/
    xr-client/
  packages/
    schema/
    prompt-templates/
    db/
    security/
    gvrm-tools/
  docs/
    production-design-policy.md
```

Unity側は次の構成を基本にする。

```text
apps/xr-client/Assets/Chimera/
  Runtime/
    Session/
    Tracking/
    Avatar/
    Audio/
    QA/
    UI/
  Shaders/
  Prefabs/
```

## 16. 実装順序

依存関係に沿って次の順序で進める。

```text
1. Production monorepo構成
2. Auth / DB / asset bucket / signed URL
3. Runtime session manifest schema
4. Dashboard knowledge ingestion
5. Script Agent
6. TTS generation cache
7. XREAL Unity client shell
8. Image Tracking + PosterAnchor
9. Unity GVRM Runtime
10. Live Q&A streaming
11. Dashboard escalation
12. Analytics / evaluation export
13. Security hardening
```

最大の技術リスクはUnity GVRM Runtimeである。したがって、初期段階ではdefault VRMでもDashboard、Manifest、Script、TTS、Q&Aを先に検証できる構造にする。

## 17. 設計決定

本番設計の判断は次で固定する。

```text
Gaussian-VRM:
  .gvrm形式は採用する。
  OpenSourceのnaruya/gaussian-vrmを資産生成・仕様参照に使う。
  XREALではUnity 6000 LTS上でSkinned Gaussian Splat Runtimeを動かす。

Avatar:
  Scaniverse由来の人間Gaussian Splatting scanを主要入力にする。
  Gaussian-VRMで骨格bindingしてmotion可能な.gvrmにする。
  モデル未登録時は標準人型VRMをdefault avatarにする。

TTS:
  事前説明はpre-render。
  Live Q&AはWebSocket streaming。

Knowledge:
  Dashboardで論文、ポスター、発表者メモ、対象聴衆、使用言語を登録する。
  Audience profileはbeginner/master/professionalに固定する。
  使用言語は日本語/英語に固定する。
  Script Agentがprofile/language別に説明を生成する。
  Evidence Checkerで根拠照合する。
  承認後にTTSを生成する。

Operation:
  Smartphone / Beam Pro controllerを主操作にする。
  Hand trackingは副操作にする。
  Dashboard remote controlを許可する。

Security:
  private assets、signed URLs、session隔離Knowledge、admin-gated DB access、
  voice consent管理、minimal loggingを必須にする。
```

一番重要な技術判断は、OpenSourceのGaussian-VRMで作った`.gvrm`を標準中間形式として扱い、Unity 6000 LTS / XREAL用のSkinned Gaussian Splat Runtime、Humanoid motion、Animation Rigging、poster anchorを統合することである。
