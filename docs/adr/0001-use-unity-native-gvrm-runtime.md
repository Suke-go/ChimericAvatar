# ADR 0001: Unity 6000 LTS Native GVRM Runtimeを採用する

Status: Accepted

## Context

Gaussian-VRMは、Gaussian Splatting人間scanをVRM骨格にbindingし、bone animationで動かすOpenSource実装である。XREAL本番アプリでは、poster image anchor、stereo rendering、Android GPU、audio、captions、controller input、secure sessionをUnity 6000 LTS側で一貫管理する。

## Decision

`.gvrm`は共通資産形式として採用する。OpenSourceの`naruya/gaussian-vrm`を資産生成・仕様参照に使用し、XREAL本番ではUnity 6000 LTS Native GVRM Runtimeを実装する。

人間scanはScaniverse由来のGaussian Splatting PLY/SPZを主要入力にする。モデルが存在しないsessionでは標準人型VRMをdefault avatarとして使う。

## Consequences

良い点:

- XREAL SDK、AR Foundation、XR Interaction Toolkit、Unity AudioSourceと自然に統合できる。
- Stereo rendering、poster image anchor、QR fallbackの整合性をUnityで管理できる。
- Android Vulkan/URP向けに性能検証できる。
- Asset security、manifest、controller inputが単一runtimeに閉じる。
- Animator、Timeline、Animation Riggingでposter presentation風のmotionを作れる。

技術リスク:

- Unity専用のSkinned Gaussian Splat rendererを作る必要がある。
- Gaussian-VRM shader/skinning logicをUnity 6000 LTS向けに検証する必要がある。

## Validation

- `.gvrm` unzip + `data.json` parse。
- UniVRM runtime load。
- static splat render。
- 1 bone deformation。
- humanoid animation追従。
- Animation Riggingによるposter pointing。
- XREAL poster image anchor上でstereo描画。
