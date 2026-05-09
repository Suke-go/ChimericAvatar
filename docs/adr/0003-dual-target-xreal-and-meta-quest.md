# ADR 0003: XREAL One Pro と Meta Quest 3 を co-target にする

Status: Accepted

## Context

[ADR 0001](0001-use-unity-native-gvrm-runtime.md)はUnity 6000 LTS Native GVRM Runtimeを採用すると決めたが、検証device targetをXREAL One Pro + XREAL Eye + XREAL Beam Pro 1系統に固定していた。

実装/検証段階で、もう1系統の標準的なAR/MR HMDで動作するかを早期に押さえたい。Meta Quest 3はAndroid + Vulkan + URPのstackがXREAL系と同じで、passthrough品質、image marker tracking、controller、hand tracking、developer tooling、市販性が揃っており、Quest 3単機種でMeta系の検証レーンを成立させられる。

## Decision

XREAL One Pro + XREAL Eye + XREAL Beam Pro と Meta Quest 3 を co-target とする。

両device向けに **単一Unityプロジェクト** `apps/xr-client/`（旧 `apps/xreal-unity/`）を維持し、device固有の差分は次の3層で吸収する:

```text
Anchor backend:
  PosterAnchorRoot (abstract)
    Anchor/Stub/      StubPosterAnchorRoot           Editor smoke
    Anchor/Manual/    ManualControllerAnchorRoot     SDK非依存、両device共通
    Anchor/Meta/      MetaMarkerPosterAnchorRoot     CHIMERA_META_XR gate
    Anchor/Xreal/     XrealPosterAnchorRoot          CHIMERA_XREAL_SDK gate

Build wiring:
  RuntimeBuildProfile (ScriptableObject) per target
  AnchorBackendFactory が AnchorBackendOrder に従ってfallback resolve

Build profile (Unity 6 BuildProfiles):
  QuestAndroid.asset   OpenXR + Meta OpenXR feature group + Meta XR Core SDK
  XrealAndroid.asset   XREAL SDK 3.1.0 native XR Loader
  EditorPlay.asset     StubPosterAnchorRoot
```

XR Loaderは:

- **Meta Quest 3**: OpenXR + Meta OpenXR feature group を主とし、Marker Tracking / Passthrough Camera 等のMeta固有機能だけ Meta XR Core SDK (`com.meta.xr.sdk.core`) を drop-in。
- **XREAL One Pro**: XREAL SDK 3.1.0 純正XR Loader（OpenXR非対応のため）。

実機検証の優先順:

1. **Phase 1**: Quest 3 で `ManualControllerAnchorRoot` を使い、SDK固有依存を最小にしてend-to-end (manifest取得 → 手動配置 → placeholder avatar → cached TTS再生 → caption表示) を最速で通す。
2. **Phase 2**: Quest 3 で `MetaMarkerPosterAnchorRoot` を結線し、image marker trackingで自動anchor化する。
3. **Phase 3**: XREAL One Pro + XREAL Eye + XREAL Beam Pro で同じシナリオを `XrealPosterAnchorRoot` 経由で再現する。
4. **Phase 4以降**: UniVRM結線、Animation Rigging poster pointing、GVRM skinned splat、Live Q&A polish。これらはanchor backendに依存しないので両deviceで等しく適用する。

Quest 2 / 3S / Pro / Vision Pro / Pico / Lynx などへの拡張はscope外。将来必要になった時点で `RuntimeBuildProfile` の追加と `AnchorBackendKind` の追加で吸収する設計にしてある。

## Consequences

良い点:

- Meta Quest 3 は手元でのiteration速度が高く、image markerやhand trackingの検証コストが低い。
- XREAL固有のhost制約 (XREAL Beam Pro mobile networking、XREAL EyeのUSB認識) と切り分けて、anchor/avatar/audio/caption層のbugを早期に潰せる。
- `PosterAnchorRoot`抽象とdefine gateにより、XREAL SDK / Meta XR SDKの片方しか持たないCI環境でもコンパイルが通る。
- Anchor backend選択はmanifestではなくclient build profileが決めるため、dashboardのmanifest schemaにdevice固有値を追加する必要がない。

技術リスク:

- Meta Quest 3でのmarker tracking APIは2026年5月時点でまだ仕上げ段階で、Meta XR SDKのバージョン更新で変わる可能性がある。`MetaMarkerPosterAnchorRoot`はSDK更新時に追従が要る。
- 同一Unityプロジェクトで2系統のXR Loaderを抱えるため、PlayerSettings / XR Plug-in Management切り替えはBuild Profile単位で厳密に管理する必要がある。誤ってXREAL Loader有効のままQuest向けbuildを焼くと起動時に黒画面になり得るので、`RuntimeBuildProfile`にPlatform列を持たせて起動時にチェックする。
- OpenXR + XREAL の互換性は将来移行を見越して継続watchする。XREALがOpenXR非対応のうちはbuild profile分離は妥当だが、OpenXR対応でXREAL loaderが収斂したらprofile統合を再検討する。
- Quest固有のpassthrough projection / camera / depth APIはXREALに存在しないため、依存箇所はAnchor backend内に閉じるか `CHIMERA_META_XR` gateで囲む規律が必要。

## Validation

- `apps/xr-client/`がEditor / QuestAndroid / XrealAndroid 3 build profile すべてでcompile errorなくbuildできる。
- Editor Play で `RuntimeBuildProfile (Editor)` + `Bootstrap.Editor.unity` がmanifest取得→Stub anchor→placeholder avatar→cached TTS→caption表示まで通る。
- Quest 3 build で `RuntimeBuildProfile (QuestAndroid)` + `Bootstrap.Quest.unity` が `ManualControllerAnchorRoot` で同シナリオを通す。
- Phase 2以降で `MetaMarkerPosterAnchorRoot` が image marker からposter anchor poseを取得できる。
- Phase 3で XREAL One Pro + Eye + Beam Pro が `XrealPosterAnchorRoot` で同シナリオを通す。
- EditMode unit test群 (`PlyParserTests`, `GaussianSplatStreamTests`, `GvrmBoneBatchesTests`, `GvrmZipReaderTests`) はSDKに依存せず、すべてのbuild profile設定でgreen。
