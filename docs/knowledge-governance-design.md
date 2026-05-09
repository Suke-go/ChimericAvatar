# Evidence-Governed Knowledge設計

## 1. 結論

Chimera Presenterでは、普通のRAGをそのまま本番回答に使わない。

Retrievalは使うが、設計の中心はRAGではなく、CHI研究・発表支援・情報管理に必要なEvidence-Governed Knowledge Systemにする。

```text
Documents
  -> structured knowledge
  -> claims
  -> evidence
  -> approved scripts
  -> live answerability
  -> dashboard escalation
  -> analytics / evaluation
```

この構成により、来場者への説明、発表者の承認、根拠管理、対象者別説明、言語別説明、質疑ログ分析を同じデータモデルで扱う。

## 2. RAGを部品として扱う理由

普通のRAGは、質問に対して近いchunkを検索し、LLMが回答を生成する構成である。研究ポスター発表では、それだけだと次の情報管理が弱い。

- どの主張が論文・ポスター・発表者メモに基づくか。
- どの説明が発表者に承認済みか。
- どの対象者向けにどの語彙で話すか。
- 日本語/英語で意味がずれていないか。
- 回答不能だった質問をどう発表者へ戻すか。
- CHI投稿・評価用に、何をログとして残すか。

そのため、retrievalはLive Q&Aとevidence lookupの部品として使い、公開される説明・回答はclaim/evidence/approvalを通す。

## 3. Knowledge Object

```text
KnowledgeDocument
  paper / poster / notes / approved presenter statement

KnowledgeChunk
  text / page / panel / figure / bbox / language / embedding

EvidenceClaim
  claim text / support level / evidence chunk ids / limitations

ScriptSegment
  profile / language / text / evidence ids / approval status / tts asset

Question
  transcript / active panel / profile / language / timestamp

Answer
  text / evidence ids / answerability / escalation reason / latency
```

## 4. Audience / Language

Profileは3つに固定する。

```text
beginner:
  HighSchool相当。直感的説明、短文、専門用語を避ける。

master:
  Bachelor / Master相当。背景、方法、結果、限界を標準的に説明する。

professional:
  PhD相当。貢献、関連研究との差分、評価可能性、限界を明示する。
```

Languageは2つに固定する。

```text
ja
en
```

Script Agentは、`profile x language`ごとにscript planとscript segmentを生成する。

## 5. Script生成

```text
1. Ingest paper / poster / notes
2. Extract claims
3. Link claims to evidence
4. Build profile/language-specific script plan
5. Write script segments
6. Check support status
7. Optimize for TTS
8. Human approval
9. Publish to runtime manifest
```

公開条件:

```text
script_segment.status = approved
evidence_chunk_ids is not empty
support_status in [SUPPORTED, PARTIALLY_SUPPORTED_WITH_LIMITATION]
language in [ja, en]
profile in [beginner, master, professional]
```

## 6. Live Q&A

Live Q&Aではretrievalを使う。ただし回答生成前にanswerabilityを判定する。

```text
question
  -> transcript cleanup
  -> active poster panel context
  -> evidence retrieval
  -> answerability judgment
  -> answer draft
  -> evidence check
  -> TTS / subtitle stream
```

Answerability:

```text
ANSWERABLE:
  session内evidenceで回答できる。

PARTIAL:
  限界や不確実性を明示すれば回答できる。

ESCALATE:
  evidence不足、session外、危険、未承認、推測が強い。
```

ESCALATEの場合はDashboardへ送る。

## 7. 実装ライブラリ

```text
DB:
  PostgreSQL
  pgvector
  PostgreSQL full-text search

Backend:
  FastAPI
  Pydantic v2
  SQLAlchemy
  Alembic

LLM:
  OpenAI Responses API
  structured outputs

Validation:
  jsonschema
  pytest
  fixed fixture evaluation
```

## 8. 検証

| Test | 合格条件 |
|---|---|
| session isolation | 他sessionのchunkがretrievalに混ざらない |
| claim support | unsupported claimが公開scriptに入らない |
| profile generation | beginner/master/professional全てでschema-valid |
| language generation | ja/en両方でschema-valid |
| answerability | evidence不足の質問がescalationされる |
| approval boundary | approved segmentだけruntime manifestへ出る |
| audit log | question、answer、evidence ids、latency、escalation reasonが残る |
