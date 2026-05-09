# Chimera XR Client (Unity 6000 LTS)

This Unity project ships a single executable that the Chimera Presenter session boots
on either of the two co-target devices defined in
[ADR 0003](../../docs/adr/0003-dual-target-xreal-and-meta-quest.md):

```text
Primary AR test:    Meta Quest 3                                     (Phase 1, 2)
Secondary AR test:  XREAL One Pro + XREAL Eye + XREAL Beam Pro       (Phase 3)
Editor smoke:       StubPosterAnchorRoot                             (always available)
```

Common Unity stack:

```text
Unity 6000 LTS, Android Vulkan, URP, Linear color space
Input System
XR Interaction Toolkit
AR Foundation
Animation Rigging
Timeline
UniVRM v0.131+ package line
com.unity.nuget.newtonsoft-json
Unity Test Framework + Unity Performance Testing
```

Per-target XR plug-in / SDK:

| Build profile | XR Loader | Required imports | Define symbol |
|---|---|---|---|
| `EditorPlay`     | (none, in-editor)              | —                                                                  | — |
| `QuestAndroid`   | OpenXR + Meta OpenXR feature   | `com.meta.xr.sdk.core` (for Marker Tracking / Passthrough Camera)  | `CHIMERA_META_XR` |
| `XrealAndroid`   | XREAL SDK 3.1.0 native loader  | XREAL SDK 3.1.0 `.unitypackage`                                    | `CHIMERA_XREAL_SDK` |

UniVRM is shared across all profiles; once imported, define `CHIMERA_UNIVRM`.

## What is already wired

`Assets/ChimeraRuntime/Runtime/`:

```text
ChimeraRuntime.asmdef                            single assembly, references Unity.Nuget.Newtonsoft.Json
ChimeraSession.cs                                orchestrator MonoBehaviour
RuntimeBuildProfile.cs                           ScriptableObject — picked up by AnchorBackendFactory + ChimeraSession

Manifest/RuntimeManifest.cs                      full DTO tree mirroring /api/v1/runtime/{code}/manifest
Manifest/GvrmMetadata.cs                         DTO matching gvrm-metadata.schema.json

Auth/SessionCredentials.cs                       runtimeToken / refreshToken / expiresAt DTO + skew check
Auth/SessionJoinToken.cs                         "chimera://session?code=&join=" URI parser
Auth/CredentialStore.cs                          PlayerPrefs-backed namespaced credential cache + deviceId
Auth/RuntimeAuthClient.cs                        POST /runtime/exchange + /runtime/refresh (RuntimeAuthException)
Auth/ISessionEntryProvider.cs                    contract for QR / manual entry sources
Auth/Manual/ManualSessionEntry.cs                Inspector field source — Phase 0 / 1 default
Auth/Meta/MetaPassthroughQrScanner.cs            CHIMERA_META_PASSTHROUGH_CAMERA + CHIMERA_ZXING gate (Phase 2)
Auth/Xreal/XrealQrScanner.cs                     CHIMERA_XREAL_SDK + CHIMERA_ZXING gate (Phase 3)

Net/ManifestClient.cs                            bearer-authenticated GET; throws ManifestUnauthorizedException on 401
Net/AssetDownloader.cs                           signed-URL bytes / Texture2D / mp3 AudioClip with disk cache
Net/LiveQaSocket.cs                              ClientWebSocket-based runtime QA, marshals events to main thread

Anchor/PosterAnchorRoot.cs                       abstract anchor (poster center, +Z out, PhysicalSizeM)
Anchor/AnchorBackendFactory.cs                   resolves backend by RuntimeBuildProfile.AnchorBackendOrder
Anchor/Stub/StubPosterAnchorRoot.cs              editor stand-in, IsTracking=true on Awake
Anchor/Manual/ManualControllerAnchorRoot.cs      controller / keyboard manual placement (Quest 3 + XREAL)
Anchor/Meta/MetaMarkerPosterAnchorRoot.cs        CHIMERA_META_XR gate; Phase 2 fills body
Anchor/Xreal/XrealPosterAnchorRoot.cs            CHIMERA_XREAL_SDK gate; Phase 3 fills body

Avatar/GvrmZipReader.cs                          .gvrm = ZIP { model.vrm, model.ply, data.json } reader + validator
Avatar/PlyParser.cs                              ASCII / binary_little_endian PLY header + scalar size helpers
Avatar/GaussianSplatStream.cs                    3DGS PLY binary vertex stream → typed float[] columns
Avatar/GvrmBoneBatches.cs                        splatBoneIndices → contiguous per-bone splat ranges
Avatar/DefaultVrmLoader.cs                       UniVRM-gated loader (CHIMERA_UNIVRM); placeholder primitive otherwise

Playback/ScriptPlayback.cs                       AudioSource + cue scheduling
Playback/PresentationCueDispatcher.cs            routes presentationCue.type → typed UnityEvents

UI/CaptionPresenter.cs                           segment + Q&A captions with citation footnotes (renderer-agnostic)
```

EditMode tests live under `Assets/ChimeraRuntime/Tests/Editor/`:

```text
ChimeraRuntime.Tests.Editor.asmdef       Editor-only, references nunit.framework.dll
PlyParserTests.cs                        ASCII header parse, stride, scalar size
GvrmZipReaderTests.cs                    synthesises a minimal .gvrm in-memory + magic / metadata rejects
GvrmBoneBatchesTests.cs                  contiguous-by-bone permutation, sparse bone ids
GaussianSplatStreamTests.cs              binary_little_endian 3DGS PLY round-trip
SessionJoinTokenTests.cs                 chimera:// URI parse / scheme / host / required-param checks
SessionCredentialsTests.cs               expiry skew / unparseable expiry / local→UTC conversion
```

Run from Unity Editor: Window → General → Test Runner → EditMode → Run All.

## First-time setup

1. Open `apps/xr-client/` in Unity Hub as a new 6000 LTS project (URP). Unity fills in the missing `Packages/`, `ProjectSettings/`, and `Library/` folders around the existing `Assets/`.
2. Window → Package Manager → install:
   - `com.unity.nuget.newtonsoft-json` (required by `ChimeraRuntime.asmdef`)
   - `com.unity.xr.arfoundation`
   - `com.unity.xr.interaction.toolkit`
   - `com.unity.inputsystem`
   - `com.unity.animation.rigging`
   - `com.unity.timeline`
3. Import UniVRM (v0.131+) via .unitypackage from <https://github.com/vrm-c/UniVRM/releases>. Add `CHIMERA_UNIVRM` to **PlayerSettings → Other → Scripting Define Symbols**.
4. **Quest 3 lane** (Phase 1+):
   - Edit → Project Settings → XR Plug-in Management → Android tab → enable **OpenXR**.
   - In OpenXR settings (Android), enable the **Meta OpenXR** feature group + **Hand Tracking**, **Anchor**, **Passthrough**, **Eye Gaze** as needed.
   - Install `com.meta.xr.sdk.core` from Asset Store / Package Manager when you reach Phase 2 (Marker Tracking). Then add `CHIMERA_META_XR` to Scripting Define Symbols.
5. **XREAL lane** (Phase 3):
   - Import XREAL SDK 3.1.0 `.unitypackage`.
   - Edit → Project Settings → XR Plug-in Management → Android tab → enable the XREAL XR Loader.
   - Add `CHIMERA_XREAL_SDK` to Scripting Define Symbols.
6. (Optional) `aras-p/UnityGaussianSplatting` for static splat rendering reference.

## Build profiles (Unity 6 BuildProfiles)

`Assets/ChimeraRuntime/BuildProfiles/` (Phase 0 still creates these by hand in editor):

```text
EditorPlay.asset
  Platform = Editor
  AnchorBackendOrder = [ Stub ]

QuestAndroid.asset
  Platform = MetaQuest
  AnchorBackendOrder = [ MetaMarker, ManualController, Stub ]
  DefaultApiBaseUrl = https://chimera-api.fly.dev

XrealAndroid.asset
  Platform = Xreal
  AnchorBackendOrder = [ XrealImage, ManualController, Stub ]
  DefaultApiBaseUrl = https://chimera-api.fly.dev
```

`AnchorBackendFactory` evaluates `AnchorBackendOrder` and instantiates the first
backend whose define gate is satisfied, so `QuestAndroid` falls through to
`ManualController` until you import the Meta XR SDK + define `CHIMERA_META_XR`.

## Minimum viable scene

```text
- ChimeraSession (GameObject)
    ChimeraSession.cs
        ApiBaseUrl         = (filled from BuildProfile.DefaultApiBaseUrl when left as default)
        SessionCode        = (left empty — populated from QR / manual entry)
        SessionEntrySource = (drag ManualSessionEntry child, OR leave null and rely on auto-resolve)
        DebugRuntimeToken  = "" (set non-empty to bypass auth in editor)
        DeviceLabel        = "Quest 3 / Lab C"
        BuildProfile       = (drag the matching profile asset)
        Anchor             = (leave empty — AnchorBackendFactory creates one)
        ScriptPlayback     = (drag ScriptPlayback child)
        AutoBootOnStart    = true
- ManualSessionEntry (child)                ← Phase 0/1 entry source
    SessionUri = "chimera://session?code=ABC123&join=eyJ..."
    (or fill SessionCode + JoinToken individually)
- ScriptPlayback (child)
    AudioSource (auto)
    ScriptPlayback.cs
- CaptionPresenter (child)
    CaptionPresenter.cs
        Source = (drag ScriptPlayback)
        Session = (drag ChimeraSession)
        OnCaptionChanged → wired to TMP_Text or UnityEngine.UI.Text
- PresentationCueDispatcher (child)
    Source = (drag ScriptPlayback)
    OnFocusPanel / OnPointPanel / ... wired in scene as needed
```

Press Play. Logs should show:

```text
status: authorizing -> waiting-for-session-entry -> exchanging-credentials
       -> fetching-manifest -> downloading-gvrm -> ready
[Chimera] Anchor backend resolved: Stub for profile 'EditorPlay'
```

## Auth + load + scan flow

See [ADR 0004](../../docs/adr/0004-runtime-credential-exchange.md) for the full design.

```text
1. ISessionEntryProvider returns SessionJoinToken
   (ManualSessionEntry: Inspector fields; future: QR scanner via passthrough camera)
2. RuntimeAuthClient.ExchangeAsync(joinToken) → SessionCredentials
   POST /api/v1/runtime/exchange
3. CredentialStore.Save (PlayerPrefs, key="chimera.creds.<sessionCode>")
4. ManifestClient.FetchAsync(sessionCode, bearer)
   On 401 → RuntimeAuthClient.RefreshAsync → retry once → fail = OnAuthRequired
5. AssetDownloader pulls signed URLs (no header — auth-in-URL)
6. PosterAnchorRoot scans poster (image marker / manual placement)
7. ScriptPlayback + LiveQaSocket run as before
```

## Phase plan (lock from ADR 0003)

```text
Phase 0  refactor + Backends抽象 + ADR 0003 + Editor Stub bootstrap         DONE
Phase 1  Quest 3:  OpenXR + ManualControllerAnchor + manifest → end-to-end  TODO
Phase 2  Quest 3:  Meta XR SDK Marker Tracking で自動anchor                  TODO
Phase 3  XREAL One Pro + Eye:  XREAL SDK 3.1.0 + XrealPosterAnchorRoot       TODO
Phase 4  Avatar強化:  UniVRM結線 + Animation Rigging poster pointing         TODO
Phase 5  GVRM proof:  SkinnedGaussianRenderer + compute shader               TODO
Phase 6  Live Q&A polish:  WebSocket UI + escalation + citation              TODO
```

## Offline development

`Assets/StreamingAssets/manifest.example.json` lets you exercise the Manifest DTOs without a running API. Replace its contents with the output of:

```bash
curl http://localhost:8000/api/v1/runtime/<your-session-code>/manifest | jq > manifest.example.json
```

Bypass the network with:

```csharp
string raw = System.IO.File.ReadAllText(System.IO.Path.Combine(
    UnityEngine.Application.streamingAssetsPath, "manifest.example.json"));
var manifest = Newtonsoft.Json.JsonConvert.DeserializeObject<Chimera.Runtime.RuntimeManifest>(raw);
```

## GVRM Runtime validation order

```text
1.  runtime-manifest.example.json deserialize     (DONE — DTOs ship with this folder)
2.  default VRM placement                         (PARTIAL — DefaultVrmLoader; UniVRM gate via CHIMERA_UNIVRM)
3.  authenticated manifest + signed asset download (DONE — ChimeraSession.BootAsync)
4.  .gvrm zip loader                              (DONE — GvrmZipReader)
5.  data.json validation                          (DONE — embedded in GvrmZipReader)
6.  PLY vertex stream extractor                   (DONE — GaussianSplatStream)
7.  static splat render                           (TODO — build on top of UnityGaussianSplatting)
8.  bone batch grouping                           (DONE — GvrmBoneBatches)
9.  1 bone deformation                            (TODO — compute shader skinning)
10. full humanoid deformation                     (TODO — UniVRM bones × splat skinning)
11. Timeline + Animation Rigging poster pointing  (TODO — Phase 4)
12. Per-device poster anchor + stereo render      (Phase 1 Quest 3 / Phase 3 XREAL)
```

## Live QA usage

```csharp
session.OnQaAnswer += answer =>
{
    Debug.Log($"answerability={answer.Answerability} text={answer.DraftAnswer}");
    if (answer.QaMatch != null)
    {
        Debug.Log($"matched rehearsed Q: {answer.QaMatch.Question}");
    }
};
await session.AskAsync("この研究の主な貢献は何ですか？", activePanelId: someId, topK: 6);
```

LiveQaSocket reads on a background thread; `LiveQaSocket.PumpMainThread()` is called every frame from `ChimeraSession.Update()` to dispatch events back to Unity's main thread.

## Things still TODO outside the scripts

- Author the three `RuntimeBuildProfile` ScriptableObject assets in `Assets/ChimeraRuntime/BuildProfiles/`
- Author the three Bootstrap scenes (`Bootstrap.Editor.unity`, `Bootstrap.Quest.unity`, `Bootstrap.XREAL.unity`)
- Author the three Unity 6 Build Profile assets and pin them to the matching scene + scripting defines
- Author `Assets/ChimeraRuntime/Input/ChimeraInput.inputactions` (Phase 1 — `Confirm`, `Cancel`, `MenuOpen`, `Pinch` action map)
- Define `CHIMERA_UNIVRM` once UniVRM is imported, so `DefaultVrmLoader` switches off its placeholder primitives
- Define `CHIMERA_META_XR` once `com.meta.xr.sdk.core` is in the project (Phase 2)
- Define `CHIMERA_XREAL_SDK` once XREAL SDK 3.1.0 is imported (Phase 3)
- Skinned Gaussian Splat compute-shader pipeline (the technical risk noted in ADR 0001):
  consumes `GaussianSplatStream` + `GvrmBoneBatches` + UniVRM bone transforms each frame
- Caption surface concrete UI: hook `CaptionPresenter.OnCaptionChanged` to a TMP_Text or UnityEngine.UI.Text in the scene
