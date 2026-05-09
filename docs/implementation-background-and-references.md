# 実装背景と参照文献

## 1. 研究・実装背景

Chimera Presenterは、研究ポスター発表の場で、ポスター横にARアバターを配置し、来場者の知識レベルと言語に応じて説明と質疑応答を行うシステムとして設計する。

実装判断は、次の背景に基づく。

- ポスター発表は、短時間・立ち話・異分野来場者・反復説明・質問対応が中心になる。
- CHI系の発表では、専門外の参加者にも主張と貢献が伝わる説明設計が必要になる。
- CHI 2026のposter instructionではon-site posterはA0 sizeとされているため、本設計でもA0縦/横を基本にする。
- ARアバターは、説明の代理、視線誘導、図表指差し、質問受付、発表者へのescalationを担当する。
- Knowledge機構は普通のRAGを直接公開するのではなく、根拠・承認・profile/language別説明・ログ分析を管理する。
- Avatar assetは、Scaniverse等から得た人間Gaussian Splatting scanを、OpenSourceのGaussian-VRMでVRM骨格にbindingして動かす。

## 2. Poster前提

標準poster size:

```text
Format: A0
Portrait:  841 mm x 1189 mm = 0.841 m x 1.189 m
Landscape: 1189 mm x 841 mm = 1.189 m x 0.841 m
```

Runtime manifestでは、XREAL Image Trackingの`physicalSizeM`を実寸で指定する。

```json
{
  "posterFormat": "A0",
  "orientation": "portrait",
  "physicalSizeM": [0.841, 1.189]
}
```

ポスター画像そのものをtracking targetにする。認識が不安定な場合だけ、DashboardがQR markerを発行し、poster cornerまたは補助紙面へ配置する。

## 3. XREAL対象機材

本番target:

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

理由:

- XREAL SDK 3.1.0は、XREAL One series + XREAL Eyeで6DoF trackingを追加している。
- XREAL SDK 3.1.0のtested compatible host devicesにはBeam ProとSamsung S25が挙げられている。
- XREAL One ProはXREAL Eye併用で6DoFに対応する。
- 本番運用ではBeam Proを標準hostにし、Samsung S25は開発・検証用hostとして扱う。

注意:

- 6DoF利用にはglasses firmware updateが必要。
- Beam Pro側のMyGlasses updateも検証項目に含める。
- Unity clientはDBへ直接接続せず、認証後にAPIからruntime manifestとsigned asset URLを取得する。

## 4. Avatar / Gaussian Splatting背景

Gaussian Splatting:

- Kerbl et al.の3D Gaussian Splattingは、3D Gaussiansによるscene表現とreal-time novel-view renderingの基盤になる。
- 本設計では、photorealistic human capture assetのruntime rendering基盤として使う。

Rigged Gaussian avatar:

- GaussianAvatarsは、Gaussian splatsをparametric modelへriggingし、pose/expression/viewpointを制御する方向性を示している。
- Gaussian-VRMは、Web/VRM ecosystem上で、Gaussian Splatting avatarをVRM骨格で動かすOpenSource実装として本設計の資産仕様の中心に置く。

Scaniverse:

- Scaniverseは3D Gaussian Splatting captureとSPZ形式を提供している。
- 本設計では、Scaniverse由来の人間scanをPLY/SPZとして受け取り、Gaussian-VRM authoringへ渡す。

## 5. Knowledge / CHI設計背景

普通のRAGだけでは、研究ポスター発表の情報管理として弱い。

必要な管理:

- どの説明が論文・ポスター・発表者メモに基づくか。
- どの説明が発表者に承認済みか。
- beginner / master / professionalで説明内容がどう変わるか。
- 日本語/英語で意味がずれていないか。
- 回答不能質問をどのように発表者へ戻すか。
- 評価用にquestion、answer、evidence ids、latency、escalation reasonを残せるか。

このため、retrievalは使うが、全体はEvidence-Governed Knowledge Systemとして設計する。

## 6. 参照文献・資料

Gaussian Splatting / Avatar:

- Kerbl, B., Kopanas, G., Leimkuehler, T., & Drettakis, G. (2023). 3D Gaussian Splatting for Real-Time Radiance Field Rendering. ACM Transactions on Graphics, 42(4). DOI: 10.1145/3592433.
- Qian, S., Kirschstein, T., Schoneveld, L., Davoli, D., Giebenhain, S., & Nießner, M. (2024). GaussianAvatars: Photorealistic Head Avatars with Rigged 3D Gaussians. CVPR 2024. DOI: 10.1109/CVPR52733.2024.01919.
- Kondo, N., Asano, Y., & Ochiai, Y. (2025). Instant Skinned Gaussian Avatars for Web, Mobile and VR Applications. SUI '25. DOI: 10.1145/3694907.3765954. arXiv:2510.13978. https://arxiv.org/abs/2510.13978
- Gaussian-VRM OpenSource repository: https://github.com/naruya/gaussian-vrm
- Scaniverse Gaussian Splatting: https://scaniverse.com/news/scaniverse-introduces-support-for-3d-gaussian-splatting
- Scaniverse SPZ: https://scaniverse.com/spz

Poster / CHI:

- ACM CHI 2026 Posters: https://chi2026.acm.org/authors/posters/
- ACM CHI 2019 Guide to a Successful Presentation: https://chi2019.acm.org/authors/papers/guide-to-a-successful-presentation/
- ISO A0 paper size reference: https://www.engineeringtoolbox.com/drawings-paper-sheets-sizes-d_349.html

XREAL / Unity:

- XREAL SDK Overview: https://docs.xreal.com/
- XREAL SDK 3.1.0 Download / Release Notes: https://developer.xreal.com/download/
- XREAL One Pro official product page: https://us.shop.xreal.com/products/xreal-one-pro
- Unity Animation Rigging: https://docs.unity3d.com/Packages/com.unity.animation.rigging@latest
- Unity Timeline: https://docs.unity3d.com/Packages/com.unity.timeline@latest

Knowledge / Retrieval:

- Lewis, P. et al. (2020). Retrieval-Augmented Generation for Knowledge-Intensive NLP Tasks. NeurIPS 2020.
- Kopp, S., Gesellensetter, L., Krämer, N. C., & Wachsmuth, I. (2005). A Conversational Agent as Museum Guide: Design and Evaluation of a Real-World Application. IVA 2005. DOI: 10.1007/11550617_28.
