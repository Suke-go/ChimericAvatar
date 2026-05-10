import io
import json
import base64
import hashlib
import hmac
import math
import secrets
import time
import zipfile
from datetime import UTC, datetime
from pathlib import Path
from uuid import uuid4

import httpx
from fastapi import HTTPException, UploadFile, status
from sqlalchemy import select, text as sql_text
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from app.core.config import get_settings
from app.auth import CurrentUser
from app.models import (
    Asset,
    AvatarBuildJob,
    AvatarConfig,
    KnowledgeChunk,
    KnowledgeDocument,
    PosterConfig,
    PosterPanel,
    PosterTextBlock,
    PresentationCue,
    ScriptSegment,
    SessionModel,
    SimulatedQa,
    TtsAsset,
    VoiceConsent,
    VoiceSample,
)
from app.storage import get_storage, storage_for_asset, storage_for_kind


POSTER_UPLOAD_MIME_TYPES = {
    "image/png": ".png",
    "image/jpeg": ".jpg",
    "image/webp": ".webp",
    "application/pdf": ".pdf",
}

KNOWLEDGE_UPLOAD_MIME_TYPES = {
    "application/pdf": ".pdf",
    "text/plain": ".txt",
    "text/markdown": ".md",
    "image/png": ".png",
    "image/jpeg": ".jpg",
    "image/webp": ".webp",
}


def _script_style_guide(profile: str, language: str) -> dict:
    """Profile + language specific writing style for the script LLM agent.

    Distilled from research-scientist elevator-pitch literature
    (PMC11590272 / Harvard Catalyst / Wayne State / U of Toronto poster guides):
      - Open with a problem the listener recognizes, not with jargon.
      - Self-introduce with full name + affiliation in 1 short sentence.
      - 60-second pitch arc: Problem(15s) → Solution(20s) → Proof(15s) → Hook(10s).
      - Vary energy and pace. Pause at key beats.
      - Match jargon to audience expertise.
      - Complement the poster, do not read it aloud verbatim.
      - End with an open invitation / question, not a hard stop.
    """
    if language == "ja":
        common = [
            "自然な話し言葉で書く。書き言葉の論文調(『〜である』『本研究では』)は使わない。",
            "TTSが読み上げることを意識し、1文を短く保つ(目安: 30〜50文字、最大80文字)。",
            "不必要なカタカナ語は和語・漢語に置き換える(例: 'アプローチ'→'方法・取り組み', 'パフォーマンス'→'性能', 'フレームワーク'→'枠組み', 'メソッド'→'手法', 'コントリビューション'→'貢献', 'リミテーション'→'限界')。",
            "固有名詞・モデル名・データセット名・人名は原語のまま。ただし初出時に1回だけ和訳または短い注釈を添える。",
            "数値や統計量は読み下す。例: 'p<0.01' → 'p値は0.01未満で、統計的に有意です'。'22%' → 'およそ22パーセント'。",
            "敬体(です・ます)で統一する。終助詞の『〜ね』『〜よ』は使わない。",
            "ポスター本文の文字をそのまま読み上げない。ポスターを補完する話を加える。",
            "見出しごとに息継ぎが入るよう、自然な区切りを意識する。",
        ]
        structure = [
            "openingセグメントの構成: ①発表者名と所属を1文で名乗る ②聴衆が共感できる問題を1文で提示 ③本ポスターで何を伝えるかを1文で予告。合計3文以内。",
            "panel_explainの構成: ①このパネルの問い・主張を1文で提示 ②根拠を1〜2文で支える(数値や図への言及含む) ③次のパネルへ繋がる短い橋渡し。",
            "limitationセグメント: 取り組みの限界1点と、現状の対処または今後の方針を1文ずつ。控えめに、しかし誠実に。",
            "closingセグメント: ①核心の貢献を1文で再提示 ②聴衆に投げかける質問または対話の誘い('ご質問お待ちしています' のような開かれた終わり方)。",
        ]
        delivery = [
            "重要な数値・固有名詞の前後で短い間('、')を置き、TTSが自然に間を取れるようにする。",
            "強調したい語の前に『特に』『ここで重要なのは』のような印象づけを最大1度だけ使う。",
            "1パネルあたり 60〜90秒で読み切れる長さに収める(およそ150〜220文字)。",
        ]
        if profile == "beginner":
            return {
                "common": common, "structure": structure, "delivery": delivery,
                "level": [
                    "対象は高校生〜大学1-2年生。専門語は最小限に。",
                    "やむなく専門語を出すときは『いわゆる〜』『〜と呼ばれる仕組み』で噛み砕く。",
                    "身近な例えを1か所だけ入れて理解の足場にする(濫用しない)。",
                ],
            }
        if profile == "master":
            return {
                "common": common, "structure": structure, "delivery": delivery,
                "level": [
                    "対象は学部生〜修士。基本的な専門語(回帰、注意機構、再構成誤差 等)は注釈なしで使う。",
                    "最先端の用語は1文以内で要約する。",
                    "主張と根拠を一対一で対応させ、図のどこを見るかを言葉で示す。",
                ],
            }
        return {
            "common": common, "structure": structure, "delivery": delivery,
            "level": [
                "対象は博士課程以上の専門家。専門語は注釈なしで使ってよい。",
                "貢献と限界を明確に区別する。",
                "数値・条件・比較対象を簡潔に列挙する。冗長な前置きは省く。",
            ],
        }

    # ===== English =====
    common = [
        "Write spoken English for TTS — short sentences, contractions OK, no bullet points.",
        "Prefer plain words when meaning is preserved (e.g. 'method' over 'approach', 'tested' over 'evaluated').",
        "Keep proper nouns and dataset/model names as-is. Gloss obscure terms once.",
        "Speak in first person ('we found', 'I'll show you') for the team's results.",
        "Don't read the poster verbatim — your job is to add the spoken context that the static poster cannot.",
        "Vary cadence: longer descriptive sentences for context, short punchy ones at the headline.",
    ]
    structure = [
        "opening: (1) presenter introduces themselves with full name + affiliation in one sentence, (2) frames the problem the audience already cares about, (3) previews what this poster delivers. ~3 sentences total.",
        "panel_explain: (1) state this panel's claim, (2) back it with 1-2 evidence sentences referring to figures/numbers, (3) bridge to the next panel.",
        "limitation: state one honest limitation and what you're doing about it. Confident, not apologetic.",
        "closing: restate the headline contribution and invite questions with an open prompt.",
    ]
    delivery = [
        "Insert natural pauses (commas) before key numbers and proper nouns so TTS breathes.",
        "Mark exactly one phrase with emphasis per panel ('What's important here is…').",
        "Aim for 60-90 seconds per panel — roughly 130-180 spoken English words.",
    ]
    if profile == "beginner":
        return {"common": common, "structure": structure, "delivery": delivery, "level": [
            "Target: high-school / first-year undergrads. Avoid jargon.",
            "Add one everyday analogy where it earns its keep.",
        ]}
    if profile == "master":
        return {"common": common, "structure": structure, "delivery": delivery, "level": [
            "Target: undergrad to master's. Standard ML/CS terms are fine.",
            "Briefly gloss bleeding-edge terms.",
            "Tie each claim to its evidence in the same breath.",
        ]}
    return {"common": common, "structure": structure, "delivery": delivery, "level": [
        "Target: PhD / domain experts. Use precise technical language.",
        "Distinguish contribution vs limitation cleanly.",
        "List numbers, conditions, and comparators concisely. Skip background.",
    ]}


PROFILE_LABELS = {
    "ja": {
        "beginner": "Beginner（高校生程度）",
        "master": "Master（学部・修士程度）",
        "professional": "Professional（博士・専門家程度）",
    },
    "en": {
        "beginner": "Beginner / high-school level",
        "master": "Master / undergraduate to master's level",
        "professional": "Professional / PhD level",
    },
}

SEGMENT_TYPES = {"opening", "panel_explain", "limitation", "closing"}
TRUST_LEVELS = {"primary", "author_note", "reference", "weak"}
VISIBILITY_LEVELS = {"admin_only", "presenter", "runtime"}

AVATAR_RUNTIME_TYPES = {"default-vrm", "gvrm", "scaniverse-source"}
GVRM_REQUIRED_ENTRIES = {"model.vrm", "data.json"}
# Splat data must be one of these (PLY or SPZ). naruya's reference uses
# model.ply; our build pipeline auto-converts to model.spz when the spz
# binding is available. Either is acceptable for upload.
GVRM_SPLAT_ENTRY_CANDIDATES = ("model.ply", "model.spz")
GVRM_REQUIRED_METADATA_KEYS = {
    "modelScale",
    "boneOperations",
    "gsPosition",
    "gsQuaternion",
    "splatVertexIndices",
    "splatBoneIndices",
    "splatRelativePoses",
}


def sniff_supported_poster_mime(upload: UploadFile) -> str:
    head = upload.file.read(16)
    upload.file.seek(0)
    if head.startswith(b"\x89PNG\r\n\x1a\n"):
        return "image/png"
    if head.startswith(b"\xff\xd8\xff"):
        return "image/jpeg"
    if head.startswith(b"%PDF-"):
        return "application/pdf"
    if len(head) >= 12 and head[:4] == b"RIFF" and head[8:12] == b"WEBP":
        return "image/webp"
    raise HTTPException(status_code=status.HTTP_415_UNSUPPORTED_MEDIA_TYPE, detail="Unsupported poster file type")


def sniff_supported_knowledge_mime(upload: UploadFile) -> str:
    head = upload.file.read(16)
    upload.file.seek(0)
    if head.startswith(b"\x89PNG\r\n\x1a\n"):
        return "image/png"
    if head.startswith(b"\xff\xd8\xff"):
        return "image/jpeg"
    if head.startswith(b"%PDF-"):
        return "application/pdf"
    if len(head) >= 12 and head[:4] == b"RIFF" and head[8:12] == b"WEBP":
        return "image/webp"
    if upload.content_type in {"text/plain", "text/markdown"}:
        return upload.content_type
    raise HTTPException(status_code=status.HTTP_415_UNSUPPORTED_MEDIA_TYPE, detail="Unsupported knowledge file type")


def ensure_session_access(db: Session, session_id: str, current_user: CurrentUser) -> SessionModel:
    session = db.get(SessionModel, session_id)
    if session is None:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Session not found")
    if current_user.role != "admin" and session.owner_id != current_user.user_id:
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Forbidden")
    return session


def generate_session_code(length: int = 6) -> str:
    alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
    return "".join(secrets.choice(alphabet) for _ in range(length))


def default_poster_size(orientation: str) -> tuple[float, float]:
    if orientation == "landscape":
        return 1.189, 0.841
    return 0.841, 1.189


def create_session(
    db: Session,
    current_user: CurrentUser,
    title: str,
    abstract: str | None,
    event_name: str | None,
    presenter_name: str | None = None,
    presenter_name_kana: str | None = None,
    presenter_affiliation: str | None = None,
) -> SessionModel:
    for _ in range(5):
        session = SessionModel(
            id=str(uuid4()),
            owner_id=current_user.user_id,
            title=title,
            abstract=abstract,
            event_name=event_name,
            presenter_name=presenter_name,
            presenter_name_kana=presenter_name_kana,
            presenter_affiliation=presenter_affiliation,
            session_code=generate_session_code(),
        )
        try:
            db.add(session)
            db.flush()
            width, height = default_poster_size("portrait")
            db.add(
                PosterConfig(
                    session_id=session.id,
                    poster_format="A0",
                    orientation="portrait",
                    physical_width_m=width,
                    physical_height_m=height,
                    tracking_reference_name=f"poster_{session.session_code.lower()}_v1",
                    qr_fallback_enabled=True,
                )
            )
            db.add(AvatarConfig(session_id=session.id))
            db.commit()
        except IntegrityError:
            db.rollback()
            continue
        db.refresh(session)
        return session
    raise HTTPException(status_code=status.HTTP_500_INTERNAL_SERVER_ERROR, detail="Failed to allocate session code")


def update_poster_config(db: Session, session_id: str, orientation: str, qr_fallback_enabled: bool) -> PosterConfig:
    poster = db.get(PosterConfig, session_id)
    if poster is None:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Poster config not found")
    width, height = default_poster_size(orientation)
    poster.orientation = orientation
    poster.physical_width_m = width
    poster.physical_height_m = height
    poster.qr_fallback_enabled = qr_fallback_enabled
    db.commit()
    db.refresh(poster)
    return poster


CANONICAL_POSTER_MIME = "image/jpeg"
CANONICAL_POSTER_EXT = ".jpg"
POSTER_JPEG_QUALITY = 85
POSTER_MAX_SIDE_PX = 2000


def _pdf_first_page_to_jpeg(content: bytes, max_side_px: int = POSTER_MAX_SIDE_PX) -> bytes:
    """Rasterize page 1 to JPEG (q=85), longer side capped to max_side_px.
    JPEG keeps Supabase Storage uploads under ~1-2MB on free tier — PNG was
    timing out even after we capped at 3000px because A0 PNGs are 5-30MB."""
    try:
        import fitz
    except ImportError as exc:
        raise HTTPException(status_code=503, detail="PyMuPDF is required to rasterize PDF posters") from exc
    try:
        with fitz.open(stream=content, filetype="pdf") as document:
            if document.page_count == 0:
                raise HTTPException(status_code=422, detail="PDF has no pages")
            page = document.load_page(0)
            longer = max(page.rect.width, page.rect.height) or 1.0
            zoom = max(1.0, min(4.0, max_side_px / longer))
            matrix = fitz.Matrix(zoom, zoom)
            pixmap = page.get_pixmap(matrix=matrix, alpha=False)
            return pixmap.tobytes("jpeg", jpg_quality=POSTER_JPEG_QUALITY)
    except HTTPException:
        raise
    except Exception as exc:
        raise HTTPException(status_code=422, detail=f"PDF rasterize failed: {exc}") from exc


def extract_pdf_text_blocks(content: bytes) -> list[dict]:
    """Return a list of {x, y, width, height, text, page_number, order_index}
    where x/y/width/height are normalized [0,1] relative to the page that
    contains the block. PyMuPDF's `get_text("blocks")` already gives us
    paragraph-ish chunks with bboxes."""
    try:
        import fitz
    except ImportError as exc:
        raise HTTPException(status_code=503, detail="PyMuPDF is required") from exc
    out: list[dict] = []
    order_counter = 0
    try:
        with fitz.open(stream=content, filetype="pdf") as document:
            for page_index, page in enumerate(document):
                pw, ph = page.rect.width, page.rect.height
                if pw <= 0 or ph <= 0:
                    continue
                for x0, y0, x1, y1, text, block_no, block_type in page.get_text("blocks"):
                    if block_type != 0:  # 0 = text, 1 = image
                        continue
                    text_clean = text.strip()
                    if not text_clean:
                        continue
                    out.append({
                        "page_number": page_index + 1,
                        "order_index": order_counter,
                        "x": max(0.0, x0 / pw),
                        "y": max(0.0, y0 / ph),
                        "width": max(0.0, (x1 - x0) / pw),
                        "height": max(0.0, (y1 - y0) / ph),
                        "text": text_clean,
                    })
                    order_counter += 1
    except Exception as exc:
        raise HTTPException(status_code=422, detail=f"PDF text-block extraction failed: {exc}") from exc
    return out


def _canonicalize_poster_bytes(content: bytes, detected_mime: str) -> bytes:
    """Canonicalize all uploaded posters to JPEG (q=85, max side 2000px).
    No PDF viewer sidebar, files small enough for Supabase free-tier upload."""
    if detected_mime == "application/pdf":
        return _pdf_first_page_to_jpeg(content)
    try:
        import fitz
        pixmap = fitz.Pixmap(content)
        if pixmap.alpha or pixmap.colorspace.n not in (1, 3):
            pixmap = fitz.Pixmap(fitz.csRGB, pixmap)
        if max(pixmap.width, pixmap.height) > POSTER_MAX_SIDE_PX:
            scale = POSTER_MAX_SIDE_PX / max(pixmap.width, pixmap.height)
            new_w = int(pixmap.width * scale)
            new_h = int(pixmap.height * scale)
            pixmap = fitz.Pixmap(pixmap, new_w, new_h)
        return pixmap.tobytes("jpeg", jpg_quality=POSTER_JPEG_QUALITY)
    except Exception:
        return content


def upload_poster_asset(db: Session, session_id: str, upload: UploadFile) -> Asset:
    if upload.content_type not in POSTER_UPLOAD_MIME_TYPES:
        raise HTTPException(status_code=status.HTTP_415_UNSUPPORTED_MEDIA_TYPE, detail="Unsupported poster file type")
    detected_mime = sniff_supported_poster_mime(upload)
    if detected_mime != upload.content_type:
        raise HTTPException(status_code=status.HTTP_415_UNSUPPORTED_MEDIA_TYPE, detail="Poster file type mismatch")
    raw = _read_upload_bytes(upload, get_settings().max_poster_upload_bytes)
    canonical_bytes = _canonicalize_poster_bytes(raw, detected_mime)
    asset_id = str(uuid4())
    original_name = upload.filename or "poster"
    stored_name = f"{asset_id}{CANONICAL_POSTER_EXT}"
    storage = get_storage()
    storage_path, size_bytes = storage.save_bytes(
        session_id=session_id,
        folder="poster",
        stored_name=stored_name,
        content=canonical_bytes,
        content_type=CANONICAL_POSTER_MIME,
    )
    asset = Asset(
        id=asset_id,
        session_id=session_id,
        kind="poster-image",
        storage_path=storage_path,
        file_name=original_name,
        mime_type=CANONICAL_POSTER_MIME,
        size_bytes=size_bytes,
    )
    db.add(asset)
    db.flush()
    poster = db.get(PosterConfig, session_id)
    if poster is not None:
        poster.poster_asset_id = asset.id
    # Drop any prior text blocks for this session — we just rebuilt the poster
    db.query(PosterTextBlock).filter(PosterTextBlock.session_id == session_id).delete()
    if detected_mime == "application/pdf":
        for block in extract_pdf_text_blocks(raw):
            db.add(PosterTextBlock(
                id=str(uuid4()),
                session_id=session_id,
                asset_id=asset.id,
                **block,
            ))
    db.commit()
    db.refresh(asset)
    # Refresh panel.text_content for any panels already drawn
    _resync_all_panel_text(db, session_id)
    # Auto-register the extracted text as a kind="poster" KnowledgeDocument so
    # the script generator + retrieval pick it up without manual paste.
    sync_poster_knowledge_from_blocks(db, session_id, asset.id)
    return asset


def _bbox_overlap(panel: PosterPanel, block: PosterTextBlock) -> float:
    """Return overlap area as a fraction of the block's area. Used to decide
    whether a text block "belongs" to a panel."""
    bx, by, bw, bh = block.x, block.y, block.width, block.height
    px, py, pw, ph = panel.x, panel.y, panel.width, panel.height
    x0 = max(px, bx)
    y0 = max(py, by)
    x1 = min(px + pw, bx + bw)
    y1 = min(py + ph, by + bh)
    if x1 <= x0 or y1 <= y0:
        return 0.0
    inter = (x1 - x0) * (y1 - y0)
    block_area = max(1e-6, bw * bh)
    return inter / block_area


def recompute_panel_text(db: Session, panel: PosterPanel, threshold: float = 0.4) -> str | None:
    """Concatenate text of blocks whose overlap with the panel exceeds threshold."""
    blocks = list(
        db.scalars(
            select(PosterTextBlock)
            .where(PosterTextBlock.session_id == panel.session_id)
            .order_by(PosterTextBlock.order_index.asc())
        )
    )
    captured: list[str] = []
    for block in blocks:
        if _bbox_overlap(panel, block) >= threshold:
            captured.append(block.text)
    text = "\n\n".join(captured) if captured else None
    panel.text_content = text
    return text


def _resync_all_panel_text(db: Session, session_id: str) -> None:
    panels = list_panels(db, session_id)
    if not panels:
        return
    blocks_exist = db.query(PosterTextBlock).filter(PosterTextBlock.session_id == session_id).count() > 0
    if not blocks_exist:
        # No blocks → clear cached text on panels (a fresh poster image may have lost the PDF source)
        for panel in panels:
            panel.text_content = None
        db.commit()
        return
    for panel in panels:
        recompute_panel_text(db, panel)
    db.commit()


def sync_poster_knowledge_from_blocks(db: Session, session_id: str, asset_id: str) -> None:
    """Replace the auto-generated poster KnowledgeDocument with chunks built
    from the current poster_text_blocks. Each chunk stores its bbox in metadata
    so panel CRUD can rebind chunks to overlapping panels."""
    blocks = list_poster_text_blocks(db, session_id)
    # Find or replace the canonical poster knowledge document
    existing = db.scalars(
        select(KnowledgeDocument)
        .where(
            KnowledgeDocument.session_id == session_id,
            KnowledgeDocument.kind == "poster",
            KnowledgeDocument.asset_id == asset_id,
        )
    ).first()
    if existing is not None:
        db.query(KnowledgeChunk).filter(KnowledgeChunk.document_id == existing.id).delete()
        document = existing
        document.text = "\n\n".join(b.text for b in blocks) if blocks else None
    else:
        document = KnowledgeDocument(
            id=str(uuid4()),
            session_id=session_id,
            asset_id=asset_id,
            kind="poster",
            title="Poster (auto-extracted)",
            text="\n\n".join(b.text for b in blocks) if blocks else None,
            language="ja",
            status="ready",
            trust_level="primary",
            use_for_script=True,
            use_for_live_qa=True,
            visibility="runtime",
        )
        db.add(document)
    db.flush()
    if not blocks:
        db.commit()
        return
    panels = list_panels(db, session_id)
    for index, block in enumerate(blocks):
        # Find which panel (if any) contains this block — assigns chunk to its scope
        panel_id: str | None = None
        for panel in panels:
            if _bbox_overlap(panel, block) >= 0.4:
                panel_id = panel.id
                break
        metadata = {
            "kind": "poster",
            "title": "Poster (auto-extracted)",
            "trust_level": "primary",
            "use_for_script": True,
            "use_for_live_qa": True,
            "visibility": "runtime",
            "source_url": None,
            "citation_label": None,
            "bbox": [block.x, block.y, block.width, block.height],
            "page_number": block.page_number,
        }
        db.add(KnowledgeChunk(
            id=str(uuid4()),
            session_id=session_id,
            document_id=document.id,
            panel_id=panel_id,
            chunk_order=index,
            text=block.text,
            language="ja",
            page_number=block.page_number,
            token_count=_token_estimate(block.text),
            retrieval_enabled=True,
            script_enabled=True,
            qa_enabled=True,
            embedding_status="pending",
            quality_status="unreviewed",
            metadata_json=json.dumps(metadata, ensure_ascii=False),
        ))
    db.commit()


def rebind_chunks_to_panel(db: Session, panel: PosterPanel, threshold: float = 0.4) -> None:
    """When a panel is created or moved, re-assign the panel_id of any
    poster-kind KnowledgeChunk whose stored bbox overlaps the new geometry."""
    chunks = list(
        db.scalars(
            select(KnowledgeChunk)
            .where(KnowledgeChunk.session_id == panel.session_id)
        )
    )
    for chunk in chunks:
        try:
            metadata = json.loads(chunk.metadata_json or "{}")
        except json.JSONDecodeError:
            continue
        bbox = metadata.get("bbox")
        if not isinstance(bbox, list) or len(bbox) != 4:
            continue
        bx, by, bw, bh = (float(v) for v in bbox)
        x0 = max(panel.x, bx)
        y0 = max(panel.y, by)
        x1 = min(panel.x + panel.width, bx + bw)
        y1 = min(panel.y + panel.height, by + bh)
        if x1 <= x0 or y1 <= y0:
            overlap = 0.0
        else:
            overlap = ((x1 - x0) * (y1 - y0)) / max(1e-6, bw * bh)
        if overlap >= threshold:
            chunk.panel_id = panel.id
        elif chunk.panel_id == panel.id:
            chunk.panel_id = None


def list_poster_text_blocks(db: Session, session_id: str) -> list[PosterTextBlock]:
    return list(
        db.scalars(
            select(PosterTextBlock)
            .where(PosterTextBlock.session_id == session_id)
            .order_by(PosterTextBlock.order_index.asc())
        )
    )


def poster_text_block_to_read(block: PosterTextBlock) -> dict:
    return {
        "id": block.id,
        "page_number": block.page_number,
        "order_index": block.order_index,
        "x": block.x,
        "y": block.y,
        "width": block.width,
        "height": block.height,
        "text": block.text,
    }


def signed_asset_url(db: Session, asset_id: str, ttl_seconds: int | None = None) -> tuple[str, int]:
    asset = db.get(Asset, asset_id)
    if asset is None:
        raise HTTPException(status_code=404, detail="Asset not found")
    return storage_for_asset(asset).signed_download_url(asset.id, asset.storage_path, ttl_seconds)


def _create_scoped_token(scope: str, session_code: str, ttl_seconds: int, extra: str = "") -> tuple[str, int]:
    settings = get_settings()
    expires_at = int(time.time()) + ttl_seconds
    message = f"{scope}:{session_code}:{extra}.{expires_at}".encode("utf-8")
    signature = hmac.new(settings.asset_url_secret.encode("utf-8"), message, hashlib.sha256).hexdigest()
    return f"{expires_at}.{signature}", expires_at


def _verify_scoped_token(scope: str, session_code: str, token: str, extra: str = "") -> None:
    try:
        expires_text, signature = token.split(".", 1)
        expires_at = int(expires_text)
    except ValueError as exc:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail=f"Invalid {scope} token") from exc
    if expires_at < int(time.time()):
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail=f"{scope} token expired")
    message = f"{scope}:{session_code}:{extra}.{expires_at}".encode("utf-8")
    expected = hmac.new(get_settings().asset_url_secret.encode("utf-8"), message, hashlib.sha256).hexdigest()
    if not hmac.compare_digest(signature, expected):
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail=f"Invalid {scope} token")


def create_runtime_join_token(session_code: str, ttl_seconds: int | None = None) -> tuple[str, int]:
    settings = get_settings()
    return _create_scoped_token("join", session_code, ttl_seconds or settings.runtime_join_token_ttl_seconds)


def verify_runtime_join_token(session_code: str, token: str) -> None:
    _verify_scoped_token("join", session_code, token)


def create_runtime_token(session_code: str, ttl_seconds: int | None = None) -> tuple[str, int]:
    settings = get_settings()
    return _create_scoped_token("runtime", session_code, ttl_seconds or settings.runtime_token_ttl_seconds)


def verify_runtime_token(session_code: str, token: str) -> None:
    _verify_scoped_token("runtime", session_code, token)


def create_runtime_refresh_token(session_code: str, device_id: str, ttl_seconds: int | None = None) -> tuple[str, int]:
    settings = get_settings()
    expires_at = int(time.time()) + (ttl_seconds or settings.runtime_refresh_token_ttl_seconds)
    message = f"refresh:{session_code}:{device_id}.{expires_at}".encode("utf-8")
    signature = hmac.new(settings.asset_url_secret.encode("utf-8"), message, hashlib.sha256).hexdigest()
    return f"{session_code}.{device_id}.{expires_at}.{signature}", expires_at


def verify_runtime_refresh_token(refresh_token: str) -> tuple[str, str]:
    try:
        payload, signature = refresh_token.rsplit(".", 1)
        session_code, device_id, expires_text = payload.split(".", 2)
        expires_at = int(expires_text)
    except ValueError as exc:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid refresh token") from exc
    if expires_at < int(time.time()):
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Refresh token expired")
    message = f"refresh:{session_code}:{device_id}.{expires_at}".encode("utf-8")
    expected = hmac.new(get_settings().asset_url_secret.encode("utf-8"), message, hashlib.sha256).hexdigest()
    if not hmac.compare_digest(signature, expected):
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid refresh token")
    return session_code, device_id


def regenerate_cues_from_script(
    db: Session,
    session_id: str,
    profile: str,
    language: str,
) -> list[PresentationCue]:
    """Auto-derive presentation cues from approved/draft script segments.
    Per panel_explain segment we emit:
      - panel_focus  : camera/UI focuses the panel
      - avatar_point : avatar arm IK toward the panel
      - caption_show : optional bottom caption with the spoken line
    Replaces all existing cues for this session."""
    segments = list_script_segments(db, session_id, profile, language)
    panels = {p.id: p for p in list_panels(db, session_id)}
    db.query(PresentationCue).filter(PresentationCue.session_id == session_id).delete()
    created: list[PresentationCue] = []
    cursor_ms = 0
    pad_ms = 200
    for segment in segments:
        duration = max(2000, (segment.duration_estimate_sec or 6) * 1000)
        if segment.segment_type == "panel_explain" and segment.panel_id and segment.panel_id in panels:
            panel = panels[segment.panel_id]
            # Choose hand by panel center: left half → right hand crosses over feels weird, prefer same-side hand
            hand = "right" if (panel.x + panel.width / 2) >= 0.5 else "left"
            for cue_type, payload in [
                (
                    "panel_focus",
                    {"panelId": panel.id, "label": panel.label},
                ),
                (
                    "avatar_point",
                    {"panelId": panel.id, "hand": hand, "intensity": 0.7},
                ),
                (
                    "caption_show",
                    {"text": segment.text, "segmentId": segment.id},
                ),
            ]:
                cue = PresentationCue(
                    id=str(uuid4()),
                    session_id=session_id,
                    segment_id=segment.id,
                    cue_type=cue_type,
                    start_ms=cursor_ms,
                    duration_ms=duration,
                    payload_json=json.dumps(payload, ensure_ascii=False),
                )
                db.add(cue)
                created.append(cue)
        elif segment.segment_type in {"opening", "closing", "limitation"}:
            gesture_style = {
                "opening": "open_palms",
                "limitation": "thinking",
                "closing": "nod",
            }[segment.segment_type]
            for cue_type, payload in [
                (
                    "avatar_gesture",
                    {"style": gesture_style},
                ),
                (
                    "caption_show",
                    {"text": segment.text, "segmentId": segment.id},
                ),
            ]:
                cue = PresentationCue(
                    id=str(uuid4()),
                    session_id=session_id,
                    segment_id=segment.id,
                    cue_type=cue_type,
                    start_ms=cursor_ms,
                    duration_ms=duration,
                    payload_json=json.dumps(payload, ensure_ascii=False),
                )
                db.add(cue)
                created.append(cue)
        cursor_ms += duration + pad_ms
    db.commit()
    return created


def replace_cues(db: Session, session_id: str, cues: list[dict]) -> list[PresentationCue]:
    db.query(PresentationCue).filter(PresentationCue.session_id == session_id).delete()
    created: list[PresentationCue] = []
    for cue in cues:
        item = PresentationCue(
            id=str(uuid4()),
            session_id=session_id,
            cue_type=cue["type"],
            start_ms=cue["start_ms"],
            duration_ms=cue.get("duration_ms"),
            payload_json=json.dumps(cue.get("payload", {})),
        )
        db.add(item)
        created.append(item)
    db.commit()
    return created


def list_accessible_sessions(db: Session, current_user: CurrentUser) -> list[SessionModel]:
    stmt = select(SessionModel).order_by(SessionModel.created_at.desc())
    if current_user.role != "admin":
        stmt = stmt.where(SessionModel.owner_id == current_user.user_id)
    return list(db.scalars(stmt))


def list_panels(db: Session, session_id: str) -> list[PosterPanel]:
    stmt = (
        select(PosterPanel)
        .where(PosterPanel.session_id == session_id)
        .order_by(PosterPanel.order_index.asc(), PosterPanel.label.asc())
    )
    return list(db.scalars(stmt))


def ensure_panel_access(db: Session, session_id: str, panel_id: str | None) -> PosterPanel | None:
    if panel_id is None:
        return None
    panel = db.get(PosterPanel, panel_id)
    if panel is None or panel.session_id != session_id:
        raise HTTPException(status_code=404, detail="Panel not found")
    return panel


def _json_list(value: str | None) -> list[str]:
    if not value:
        return []
    data = json.loads(value)
    if not isinstance(data, list):
        return []
    return [str(item) for item in data]


def _chunk_normalized(normalized: str, size: int, overlap: int) -> list[str]:
    chunks: list[str] = []
    cursor = 0
    while cursor < len(normalized):
        chunk = normalized[cursor : cursor + size].strip()
        if chunk:
            chunks.append(chunk)
        if cursor + size >= len(normalized):
            break
        cursor += max(1, size - overlap)
    return chunks


def chunk_text(text: str) -> list[str]:
    settings = get_settings()
    normalized = "\n".join(line.strip() for line in text.splitlines() if line.strip())
    if len(normalized) > settings.max_knowledge_text_chars:
        raise HTTPException(status_code=413, detail="Knowledge text is too large")
    return _chunk_normalized(normalized, settings.knowledge_chunk_chars, settings.knowledge_chunk_overlap_chars)


def chunk_pages(pages: list[tuple[int, str]]) -> list[tuple[int, str]]:
    settings = get_settings()
    total_chars = sum(len(text) for _, text in pages)
    if total_chars > settings.max_knowledge_text_chars:
        raise HTTPException(status_code=413, detail="Knowledge text is too large")
    out: list[tuple[int, str]] = []
    for page_no, text in pages:
        normalized = "\n".join(line.strip() for line in text.splitlines() if line.strip())
        if not normalized:
            continue
        for chunk in _chunk_normalized(normalized, settings.knowledge_chunk_chars, settings.knowledge_chunk_overlap_chars):
            out.append((page_no, chunk))
    return out


def _token_estimate(text: str) -> int:
    return max(1, math.ceil(len(text) / 4))


def _validate_rag_metadata(trust_level: str, visibility: str) -> None:
    if trust_level not in TRUST_LEVELS:
        raise HTTPException(status_code=422, detail="Invalid trust_level")
    if visibility not in VISIBILITY_LEVELS:
        raise HTTPException(status_code=422, detail="Invalid visibility")


def _chunk_metadata(
    kind: str,
    title: str | None,
    trust_level: str,
    use_for_script: bool,
    use_for_live_qa: bool,
    visibility: str,
    source_url: str | None,
    citation_label: str | None,
    extra: dict | None = None,
) -> dict:
    payload = {
        "kind": kind,
        "title": title,
        "trust_level": trust_level,
        "visibility": visibility,
        "use_for_script": use_for_script,
        "use_for_live_qa": use_for_live_qa,
        "source_url": source_url,
        "citation_label": citation_label,
    }
    if extra:
        payload.update(extra)
    return payload


def _new_knowledge_chunk(
    session_id: str,
    document_id: str,
    panel_id: str | None,
    chunk_order: int,
    chunk: str,
    language: str,
    metadata: dict,
    use_for_script: bool,
    use_for_live_qa: bool,
    page_number: int | None = None,
) -> KnowledgeChunk:
    return KnowledgeChunk(
        id=str(uuid4()),
        session_id=session_id,
        document_id=document_id,
        panel_id=panel_id,
        chunk_order=chunk_order,
        text=chunk,
        language=language,
        page_number=page_number,
        token_count=_token_estimate(chunk),
        retrieval_enabled=use_for_script or use_for_live_qa,
        script_enabled=use_for_script,
        qa_enabled=use_for_live_qa,
        embedding_status="pending",
        quality_status="unreviewed",
        metadata_json=json.dumps(metadata, ensure_ascii=False),
    )


def create_knowledge_text(
    db: Session,
    session_id: str,
    kind: str,
    title: str | None,
    text: str,
    language: str,
    panel_id: str | None,
    trust_level: str = "primary",
    use_for_script: bool = True,
    use_for_live_qa: bool = True,
    visibility: str = "runtime",
    source_url: str | None = None,
    citation_label: str | None = None,
) -> KnowledgeDocument:
    ensure_panel_access(db, session_id, panel_id)
    _validate_rag_metadata(trust_level, visibility)
    document = KnowledgeDocument(
        id=str(uuid4()),
        session_id=session_id,
        kind=kind,
        title=title,
        text=text,
        language=language,
        status="ready",
        trust_level=trust_level,
        use_for_script=use_for_script,
        use_for_live_qa=use_for_live_qa,
        visibility=visibility,
        source_url=source_url,
        citation_label=citation_label,
    )
    db.add(document)
    db.flush()
    metadata = _chunk_metadata(
        kind=kind,
        title=title,
        trust_level=trust_level,
        use_for_script=use_for_script,
        use_for_live_qa=use_for_live_qa,
        visibility=visibility,
        source_url=source_url,
        citation_label=citation_label,
    )
    for index, chunk in enumerate(chunk_text(text)):
        db.add(_new_knowledge_chunk(session_id, document.id, panel_id, index, chunk, language, metadata, use_for_script, use_for_live_qa))
    db.commit()
    db.refresh(document)
    return document


def _read_upload_bytes(upload: UploadFile, max_bytes: int) -> bytes:
    total = 0
    data = bytearray()
    while True:
        chunk = upload.file.read(1024 * 1024)
        if not chunk:
            break
        total += len(chunk)
        if total > max_bytes:
            mb = max_bytes // (1024 * 1024)
            raise HTTPException(
                status_code=413,
                detail=f"Upload exceeds the {mb} MB limit for this endpoint",
            )
        data.extend(chunk)
    upload.file.seek(0)
    return bytes(data)


def extract_pdf_pages(content: bytes) -> list[tuple[int, str]]:
    try:
        import fitz
    except ImportError as exc:
        raise HTTPException(status_code=503, detail="PyMuPDF is required for PDF extraction") from exc
    try:
        with fitz.open(stream=content, filetype="pdf") as document:
            pages = [
                (index + 1, page.get_text("text", sort=True).strip())
                for index, page in enumerate(document)
            ]
    except Exception as exc:
        raise HTTPException(status_code=422, detail=f"PDF extraction failed: {exc}") from exc
    pages = [(num, text) for num, text in pages if text]
    if not pages:
        raise HTTPException(status_code=422, detail="No selectable text was found in the PDF")
    return pages


def extract_pdf_text(content: bytes) -> str:
    return "\n\n".join(text for _, text in extract_pdf_pages(content))


async def extract_image_ocr_with_openai(content: bytes, mime_type: str, language: str) -> str:
    settings = get_settings()
    if not settings.openai_api_key:
        raise HTTPException(status_code=503, detail="OPENAI_API_KEY is required for image OCR")
    data_url = f"data:{mime_type};base64,{base64.b64encode(content).decode('ascii')}"
    prompt = (
        "Extract all readable poster text. Preserve headings, figure labels, equations as readable plain text, "
        "and reading order. Return only JSON with keys title and text."
    )
    if language == "ja":
        prompt = (
            "ポスター画像から読める文字をすべて抽出してください。見出し、図番号、数式、注記を読み順に保ち、"
            "JSONだけで title と text を返してください。"
        )
    payload = {
        "model": settings.openai_ocr_model,
        "input": [
            {
                "role": "user",
                "content": [
                    {"type": "input_text", "text": prompt},
                    {"type": "input_image", "image_url": data_url},
                ],
            }
        ],
        "text": {
            "format": {
                "type": "json_schema",
                "name": "poster_ocr_result",
                "strict": True,
                "schema": {
                    "type": "object",
                    "additionalProperties": False,
                    "properties": {
                        "title": {"type": "string"},
                        "text": {"type": "string"},
                    },
                    "required": ["title", "text"],
                },
            }
        },
    }
    data = await _call_openai_responses(payload)
    result = _extract_openai_json(data)
    text = str(result.get("text", "")).strip()
    if not text:
        raise HTTPException(status_code=502, detail="OpenAI OCR returned no text")
    return text


async def upload_knowledge_asset(
    db: Session,
    session_id: str,
    upload: UploadFile,
    kind: str,
    title: str | None,
    language: str,
    panel_id: str | None,
    auto_extract: bool,
    trust_level: str = "primary",
    use_for_script: bool = True,
    use_for_live_qa: bool = True,
    visibility: str = "runtime",
    source_url: str | None = None,
    citation_label: str | None = None,
) -> dict:
    ensure_panel_access(db, session_id, panel_id)
    _validate_rag_metadata(trust_level, visibility)
    if upload.content_type not in KNOWLEDGE_UPLOAD_MIME_TYPES:
        raise HTTPException(status_code=415, detail="Unsupported knowledge file type")
    detected_mime = sniff_supported_knowledge_mime(upload)
    if detected_mime != upload.content_type:
        raise HTTPException(status_code=415, detail="Knowledge file type mismatch")
    content = _read_upload_bytes(upload, get_settings().max_knowledge_upload_bytes)
    asset_id = str(uuid4())
    original_name = upload.filename or "knowledge"
    suffix = Path(original_name).suffix.lower()
    extension = suffix if suffix in KNOWLEDGE_UPLOAD_MIME_TYPES.values() else KNOWLEDGE_UPLOAD_MIME_TYPES[detected_mime]
    storage = get_storage()
    storage_path, size_bytes = storage.save_bytes(
        session_id=session_id,
        folder="knowledge",
        stored_name=f"{asset_id}{extension}",
        content=content,
    )
    try:
        asset = Asset(
            id=asset_id,
            session_id=session_id,
            kind=f"knowledge-{kind}",
            storage_path=storage_path,
            file_name=original_name,
            mime_type=detected_mime,
            size_bytes=size_bytes,
            status="uploaded",
        )
        db.add(asset)
        db.flush()
        extraction_method = "uploaded_only"
        extracted_text = ""
        page_chunks: list[tuple[int | None, str]] | None = None
        if auto_extract:
            if detected_mime == "application/pdf":
                pdf_pages = extract_pdf_pages(content)
                extracted_text = "\n\n".join(text for _, text in pdf_pages)
                extraction_method = "pymupdf"
                page_chunks = [
                    (page_no, chunk_text_value)
                    for page_no, chunk_text_value in chunk_pages(pdf_pages)
                ]
            elif detected_mime.startswith("text/"):
                extracted_text = content.decode("utf-8", errors="replace")
                extraction_method = "plain_text"
            elif detected_mime.startswith("image/"):
                extracted_text = await extract_image_ocr_with_openai(content, detected_mime, language)
                extraction_method = "openai_vision_ocr"
        document = KnowledgeDocument(
            id=str(uuid4()),
            session_id=session_id,
            asset_id=asset.id,
            kind=kind,
            title=title or original_name,
            text=extracted_text or None,
            language=language,
            status="ready" if extracted_text else "uploaded",
            trust_level=trust_level,
            use_for_script=use_for_script,
            use_for_live_qa=use_for_live_qa,
            visibility=visibility,
            source_url=source_url,
            citation_label=citation_label,
        )
        db.add(document)
        db.flush()
        chunks_created = 0
        if extracted_text:
            metadata = _chunk_metadata(
                kind=kind,
                title=document.title,
                trust_level=trust_level,
                use_for_script=use_for_script,
                use_for_live_qa=use_for_live_qa,
                visibility=visibility,
                source_url=source_url,
                citation_label=citation_label,
                extra={"method": extraction_method},
            )
            iterator: list[tuple[int | None, str]]
            if page_chunks is not None:
                iterator = page_chunks
            else:
                iterator = [(None, chunk) for chunk in chunk_text(extracted_text)]
            for index, (page_no, chunk) in enumerate(iterator):
                db.add(
                    _new_knowledge_chunk(
                        session_id,
                        document.id,
                        panel_id,
                        index,
                        chunk,
                        language,
                        metadata,
                        use_for_script,
                        use_for_live_qa,
                        page_number=page_no,
                    )
                )
                chunks_created += 1
        db.commit()
        db.refresh(document)
        return {
            "document": document,
            "chunks_created": chunks_created,
            "extracted_chars": len(extracted_text),
            "extraction_method": extraction_method,
        }
    except Exception:
        db.rollback()
        storage.delete_path(storage_path)
        raise


def list_knowledge_documents(db: Session, session_id: str) -> list[KnowledgeDocument]:
    stmt = (
        select(KnowledgeDocument)
        .where(KnowledgeDocument.session_id == session_id)
        .order_by(KnowledgeDocument.created_at.desc())
    )
    return list(db.scalars(stmt))


def delete_knowledge_document(db: Session, session_id: str, document_id: str) -> None:
    document = db.get(KnowledgeDocument, document_id)
    if document is None or document.session_id != session_id:
        raise HTTPException(status_code=404, detail="Knowledge document not found")
    storage = get_storage()
    if document.asset_id:
        asset = db.get(Asset, document.asset_id)
        if asset is not None:
            try:
                storage.delete_path(asset.storage_path)
            except Exception:
                # cleanup is best-effort; the DB row removal is the source of truth
                pass
            db.delete(asset)
    # KnowledgeChunk has ON DELETE CASCADE on document_id, so the chunks
    # disappear automatically when the document row is removed.
    db.delete(document)
    db.commit()


def get_knowledge_document(db: Session, session_id: str, document_id: str) -> KnowledgeDocument:
    document = db.get(KnowledgeDocument, document_id)
    if document is None or document.session_id != session_id:
        raise HTTPException(status_code=404, detail="Knowledge document not found")
    return document


def list_knowledge_chunks(db: Session, session_id: str) -> list[KnowledgeChunk]:
    stmt = (
        select(KnowledgeChunk)
        .where(KnowledgeChunk.session_id == session_id)
        .order_by(KnowledgeChunk.created_at.asc(), KnowledgeChunk.chunk_order.asc())
    )
    return list(db.scalars(stmt))


def list_document_chunks(db: Session, session_id: str, document_id: str) -> list[KnowledgeChunk]:
    stmt = (
        select(KnowledgeChunk)
        .where(
            KnowledgeChunk.session_id == session_id,
            KnowledgeChunk.document_id == document_id,
        )
        .order_by(KnowledgeChunk.chunk_order.asc())
    )
    return list(db.scalars(stmt))


def list_rag_chunks(db: Session, session_id: str, language: str, for_live_qa: bool = True) -> list[KnowledgeChunk]:
    stmt = select(KnowledgeChunk).where(
        KnowledgeChunk.session_id == session_id,
        KnowledgeChunk.language == language,
        KnowledgeChunk.retrieval_enabled.is_(True),
    )
    if for_live_qa:
        stmt = stmt.where(KnowledgeChunk.qa_enabled.is_(True))
    else:
        stmt = stmt.where(KnowledgeChunk.script_enabled.is_(True))
    stmt = stmt.order_by(KnowledgeChunk.created_at.asc(), KnowledgeChunk.chunk_order.asc())
    return list(db.scalars(stmt))


async def _call_openai_responses(payload: dict) -> dict:
    settings = get_settings()
    if not settings.openai_api_key:
        raise HTTPException(status_code=503, detail="OPENAI_API_KEY is required")
    headers = {
        "authorization": f"Bearer {settings.openai_api_key}",
        "content-type": "application/json",
    }
    try:
        async with httpx.AsyncClient(timeout=90.0) as client:
            response = await client.post("https://api.openai.com/v1/responses", headers=headers, json=payload)
    except httpx.HTTPError as exc:
        raise HTTPException(status_code=502, detail=f"OpenAI request failed: {exc}") from exc
    if response.status_code >= 400:
        raise HTTPException(status_code=502, detail=f"OpenAI request failed with status {response.status_code}")
    return response.json()


async def _call_openai_embeddings(texts: list[str]) -> list[list[float]]:
    settings = get_settings()
    if not settings.openai_api_key:
        raise HTTPException(status_code=503, detail="OPENAI_API_KEY is required")
    headers = {
        "authorization": f"Bearer {settings.openai_api_key}",
        "content-type": "application/json",
    }
    body = {
        "model": settings.openai_embedding_model,
        "input": texts,
        "dimensions": settings.openai_embedding_dimensions,
    }
    try:
        async with httpx.AsyncClient(timeout=90.0) as client:
            response = await client.post("https://api.openai.com/v1/embeddings", headers=headers, json=body)
    except httpx.HTTPError as exc:
        raise HTTPException(status_code=502, detail=f"OpenAI embedding request failed: {exc}") from exc
    if response.status_code >= 400:
        raise HTTPException(status_code=502, detail=f"OpenAI embedding request failed with status {response.status_code}")
    data = response.json().get("data", [])
    vectors = [item.get("embedding") for item in sorted(data, key=lambda item: item.get("index", 0))]
    if len(vectors) != len(texts) or not all(isinstance(vector, list) for vector in vectors):
        raise HTTPException(status_code=502, detail="OpenAI embedding response was incomplete")
    return vectors


async def embed_document_chunks(db: Session, session_id: str, document_id: str) -> None:
    chunks = list_document_chunks(db, session_id, document_id)
    if not chunks:
        return
    settings = get_settings()
    if not settings.openai_api_key:
        for chunk in chunks:
            chunk.embedding_status = "not_configured"
        db.commit()
        return
    try:
        vectors = await _call_openai_embeddings([chunk.text for chunk in chunks])
    except HTTPException:
        for chunk in chunks:
            chunk.embedding_status = "failed"
        db.commit()
        return
    for chunk, vector in zip(chunks, vectors, strict=True):
        chunk.embedding_json = json.dumps(vector)
        chunk.embedding_model = settings.openai_embedding_model
        chunk.embedding_dimensions = settings.openai_embedding_dimensions
        chunk.embedding_status = "ready"
    db.commit()
    if not db.bind or db.bind.dialect.name == "sqlite":
        return
    for chunk, vector in zip(chunks, vectors, strict=True):
        db.execute(
            sql_text("update knowledge_chunks set embedding = cast(:embedding as vector) where id = :chunk_id"),
            {"embedding": "[" + ",".join(str(value) for value in vector) + "]", "chunk_id": chunk.id},
        )
    db.commit()


def _cosine_similarity(left: list[float], right: list[float]) -> float:
    if not left or not right or len(left) != len(right):
        return 0.0
    dot = sum(a * b for a, b in zip(left, right, strict=True))
    left_norm = math.sqrt(sum(a * a for a in left))
    right_norm = math.sqrt(sum(b * b for b in right))
    if left_norm == 0 or right_norm == 0:
        return 0.0
    return dot / (left_norm * right_norm)


def _lexical_score(question: str, chunk_text_value: str) -> float:
    question_terms = {term.lower() for term in question.replace("\n", " ").split() if len(term) > 1}
    chunk_terms = {term.lower() for term in chunk_text_value.replace("\n", " ").split() if len(term) > 1}
    if not question_terms or not chunk_terms:
        return 0.0
    return len(question_terms & chunk_terms) / max(1, len(question_terms))


TRUST_WEIGHTS = {"primary": 1.0, "author_note": 0.85, "reference": 0.7, "weak": 0.5}


def _trust_weight(metadata: dict) -> float:
    return TRUST_WEIGHTS.get(metadata.get("trust_level", "primary"), 1.0)


async def embed_simulated_qa_questions(db: Session, session_id: str) -> None:
    settings = get_settings()
    qas = list(
        db.scalars(
            select(SimulatedQa).where(
                SimulatedQa.session_id == session_id,
                SimulatedQa.embedding_status.in_(["pending", "stale"]),
            )
        )
    )
    if not qas:
        return
    if not settings.openai_api_key:
        for qa in qas:
            qa.embedding_status = "not_configured"
        db.commit()
        return
    try:
        vectors = await _call_openai_embeddings([qa.question for qa in qas])
    except HTTPException:
        for qa in qas:
            qa.embedding_status = "failed"
        db.commit()
        return
    for qa, vector in zip(qas, vectors, strict=True):
        qa.question_embedding_json = json.dumps(vector)
        qa.embedding_model = settings.openai_embedding_model
        qa.embedding_status = "ready"
    db.commit()


def _list_approved_qas(db: Session, session_id: str, language: str) -> list[SimulatedQa]:
    return list(
        db.scalars(
            select(SimulatedQa).where(
                SimulatedQa.session_id == session_id,
                SimulatedQa.language == language,
                SimulatedQa.status == "approved",
            )
        )
    )


async def retrieval_preview(
    db: Session,
    session_id: str,
    question: str,
    profile: str,
    language: str,
    active_panel_id: str | None,
    top_k: int,
) -> dict:
    ensure_panel_access(db, session_id, active_panel_id)
    chunks = list_rag_chunks(db, session_id, language, for_live_qa=True)
    approved_qas = _list_approved_qas(db, session_id, language)
    if not chunks and not approved_qas:
        return {
            "answerability": "escalate",
            "reason": "No QA-enabled knowledge chunks or approved simulated questions are registered.",
            "draft_answer": "",
            "chunks": [],
            "qa_match": None,
        }
    query_vector: list[float] | None = None
    if get_settings().openai_api_key:
        try:
            query_vector = (await _call_openai_embeddings([question]))[0]
        except HTTPException:
            query_vector = None

    scored_qas: list[tuple[float, SimulatedQa]] = []
    for qa in approved_qas:
        score = 0.0
        if query_vector and qa.question_embedding_json:
            try:
                score = _cosine_similarity(query_vector, json.loads(qa.question_embedding_json))
            except (json.JSONDecodeError, TypeError):
                score = 0.0
        if score <= 0.0:
            score = _lexical_score(question, qa.question)
        if active_panel_id and qa.panel_id == active_panel_id:
            score = min(1.0, score + 0.08)
        scored_qas.append((score, qa))
    scored_qas.sort(key=lambda item: item[0], reverse=True)
    qa_match: dict | None = None
    qa_threshold = 0.85 if query_vector is not None else 0.55
    if scored_qas and scored_qas[0][0] >= qa_threshold:
        score, top_qa = scored_qas[0]
        qa_match = {
            "qa_id": top_qa.id,
            "question": top_qa.question,
            "answer": top_qa.answer,
            "score": round(score, 4),
            "panel_id": top_qa.panel_id,
            "evidence_chunk_ids": _json_list(top_qa.evidence_chunk_ids_json),
        }

    scored: list[tuple[float, KnowledgeChunk]] = []
    for chunk in chunks:
        score = 0.0
        if query_vector and chunk.embedding_json:
            try:
                score = _cosine_similarity(query_vector, json.loads(chunk.embedding_json))
            except (json.JSONDecodeError, TypeError):
                score = 0.0
        if score <= 0.0:
            score = _lexical_score(question, chunk.text)
        chunk_metadata = json.loads(chunk.metadata_json or "{}")
        score *= _trust_weight(chunk_metadata)
        if active_panel_id and chunk.panel_id == active_panel_id:
            score = min(1.0, score + 0.08)
        scored.append((score, chunk))
    scored.sort(key=lambda item: item[0], reverse=True)
    selected = [(score, chunk) for score, chunk in scored[:top_k] if score > 0]
    best = selected[0][0] if selected else 0.0

    if qa_match is not None:
        answerability = "answerable"
        reason = "Matched an approved rehearsed question."
        draft_answer = qa_match["answer"]
    else:
        if best >= 0.72 or (query_vector is None and best >= 0.35):
            answerability = "answerable"
            reason = "Registered evidence has a close match."
        elif best >= 0.55 or (query_vector is None and best >= 0.18):
            answerability = "weak"
            reason = "Some related evidence was found, but presenter review is recommended."
        else:
            answerability = "escalate"
            reason = "No sufficiently close registered evidence was found."
        evidence_text = " ".join(chunk.text for _, chunk in selected[:2])
        draft_answer = _shorten_for_profile(evidence_text, profile, language) if selected else ""
        if language == "ja" and draft_answer:
            draft_answer = f"登録済みの根拠では、{draft_answer}"
        elif draft_answer:
            draft_answer = f"Based on registered evidence: {draft_answer}"
    return {
        "answerability": answerability,
        "reason": reason,
        "draft_answer": draft_answer,
        "qa_match": qa_match,
        "chunks": [
            {
                "chunk_id": chunk.id,
                "document_id": chunk.document_id,
                "panel_id": chunk.panel_id,
                "score": round(score, 4),
                "text": chunk.text,
                "page_number": chunk.page_number,
                "metadata": json.loads(chunk.metadata_json or "{}"),
            }
            for score, chunk in selected
        ],
    }


def _extract_openai_text(data: dict) -> str:
    if isinstance(data.get("output_text"), str):
        return data["output_text"]
    texts: list[str] = []
    for item in data.get("output", []):
        for content in item.get("content", []):
            if content.get("type") in {"output_text", "text"} and isinstance(content.get("text"), str):
                texts.append(content["text"])
    return "\n".join(texts)


def _extract_openai_json(data: dict) -> dict:
    text = _extract_openai_text(data).strip()
    if not text:
        raise HTTPException(status_code=502, detail="OpenAI returned no text")
    try:
        parsed = json.loads(text)
    except json.JSONDecodeError as exc:
        raise HTTPException(status_code=502, detail=f"OpenAI returned invalid JSON: {text[:300]}") from exc
    if not isinstance(parsed, dict):
        raise HTTPException(status_code=502, detail="OpenAI returned non-object JSON")
    return parsed


def _shorten_for_profile(text: str, profile: str, language: str) -> str:
    cleaned = " ".join(text.split())
    if profile == "beginner":
        limit = 170 if language == "ja" else 210
    elif profile == "master":
        limit = 260 if language == "ja" else 330
    else:
        limit = 380 if language == "ja" else 480
    if len(cleaned) <= limit:
        return cleaned
    return cleaned[:limit].rstrip() + "..."


def _compose_panel_script(panel: PosterPanel, chunks: list[KnowledgeChunk], profile: str, language: str) -> str:
    evidence = _shorten_for_profile(chunks[0].text if chunks else panel.label, profile, language)
    if language == "en":
        if profile == "beginner":
            return f"Here we look at {panel.label}. The main point is this: {evidence}"
        if profile == "master":
            return f"For panel {panel.label}, focus on the claim and the evidence. {evidence}"
        return f"Panel {panel.label} should be read as an evidence block. The relevant technical point is: {evidence}"
    if profile == "beginner":
        return f"{panel.label}では、まず何を示しているかに注目します。大事な点は、{evidence}"
    if profile == "master":
        return f"{panel.label}では、主張と根拠の対応を確認します。ここで説明すべき内容は、{evidence}"
    return f"{panel.label}は専門的な根拠を示すパネルです。評価すべき技術的ポイントは、{evidence}"


def generate_script_from_knowledge(
    db: Session,
    session: SessionModel,
    profile: str,
    language: str,
    replace_existing: bool,
) -> list[ScriptSegment]:
    panels = list_panels(db, session.id)
    chunks = list_rag_chunks(db, session.id, language, for_live_qa=False)
    if not chunks:
        raise HTTPException(status_code=409, detail="Knowledge chunks are required before script generation")
    if replace_existing:
        db.query(ScriptSegment).filter(
            ScriptSegment.session_id == session.id,
            ScriptSegment.profile == profile,
            ScriptSegment.language == language,
        ).delete()
        db.flush()
    segments: list[ScriptSegment] = []
    profile_label = PROFILE_LABELS[language][profile]
    presenter_name = session.presenter_name
    presenter_aff = session.presenter_affiliation
    if language == "ja":
        if presenter_name:
            intro = f"はじめまして、{presenter_aff + 'の' if presenter_aff else ''}{presenter_name}と申します。"
        else:
            intro = "こんにちは、ご来場ありがとうございます。"
        opening_text = f"{intro}この説明は{profile_label}向けです。ポスターをパネル順にご紹介します。"
    else:
        if presenter_name:
            intro = f"Hi, I'm {presenter_name}{', ' + presenter_aff if presenter_aff else ''}."
        else:
            intro = "Hi, thanks for stopping by."
        opening_text = f"{intro} This walkthrough is tuned for {profile_label}. I'll go through the panels in order."
    opening = ScriptSegment(
        id=str(uuid4()),
        session_id=session.id,
        profile=profile,
        language=language,
        panel_id=None,
        segment_order=0,
        segment_type="opening",
        text=opening_text,
        evidence_chunk_ids_json="[]",
        status="draft",
        duration_estimate_sec=max(4, len(opening_text) // (18 if language == "ja" else 14)),
    )
    db.add(opening)
    segments.append(opening)
    by_panel: dict[str | None, list[KnowledgeChunk]] = {}
    for chunk in chunks:
        by_panel.setdefault(chunk.panel_id, []).append(chunk)
    fallback_chunks = chunks[:2]
    order = 1
    for panel in panels:
        panel_chunks = by_panel.get(panel.id) or fallback_chunks
        text = _compose_panel_script(panel, panel_chunks, profile, language)
        evidence_ids = [chunk.id for chunk in panel_chunks[:3]]
        segment = ScriptSegment(
            id=str(uuid4()),
            session_id=session.id,
            profile=profile,
            language=language,
            panel_id=panel.id,
            segment_order=order,
            segment_type="panel_explain",
            text=text,
            evidence_chunk_ids_json=json.dumps(evidence_ids),
            status="draft",
            duration_estimate_sec=max(8, len(text) // (18 if language == "ja" else 14)),
        )
        db.add(segment)
        segments.append(segment)
        order += 1
    limitation_chunks = [
        chunk for chunk in chunks
        if json.loads(chunk.metadata_json or "{}").get("kind") in {"limitation", "author_notes"}
    ]
    if limitation_chunks:
        limit_text = _shorten_for_profile(limitation_chunks[0].text, profile, language)
        limitation_text = (
            f"As a limitation: {limit_text}"
            if language == "en"
            else f"研究上の限界として、{limit_text}"
        )
        limitation = ScriptSegment(
            id=str(uuid4()),
            session_id=session.id,
            profile=profile,
            language=language,
            panel_id=None,
            segment_order=order,
            segment_type="limitation",
            text=limitation_text,
            evidence_chunk_ids_json=json.dumps([limitation_chunks[0].id]),
            status="draft",
            duration_estimate_sec=max(6, len(limitation_text) // (18 if language == "ja" else 14)),
        )
        db.add(limitation)
        segments.append(limitation)
        order += 1

    closing_text = (
        "If a question goes beyond the registered evidence, I will route it to the presenter instead of guessing."
        if language == "en"
        else "登録された根拠を超える質問は、推測で答えず発表者へ確認します。"
    )
    closing = ScriptSegment(
        id=str(uuid4()),
        session_id=session.id,
        profile=profile,
        language=language,
        panel_id=None,
        segment_order=order,
        segment_type="closing",
        text=closing_text,
        evidence_chunk_ids_json="[]",
        status="draft",
        duration_estimate_sec=max(5, len(closing_text) // (18 if language == "ja" else 14)),
    )
    db.add(closing)
    segments.append(closing)
    db.commit()
    regenerate_cues_from_script(db, session.id, profile, language)
    return segments


async def generate_script_agent(
    db: Session,
    session: SessionModel,
    profile: str,
    language: str,
    replace_existing: bool,
    use_openai: bool,
) -> list[ScriptSegment]:
    settings = get_settings()
    if not use_openai or not settings.openai_api_key:
        return generate_script_from_knowledge(db, session, profile, language, replace_existing)
    panels = list_panels(db, session.id)
    chunks = list_rag_chunks(db, session.id, language, for_live_qa=False)
    if not chunks:
        raise HTTPException(status_code=409, detail="Knowledge chunks are required before script generation")
    panel_payload = [{"id": panel.id, "label": panel.label, "order": panel.order_index} for panel in panels]
    chunk_payload = [
        {
            "id": chunk.id,
            "panel_id": chunk.panel_id,
            "text": _shorten_for_profile(chunk.text, "professional", language),
        }
        for chunk in chunks[:40]
    ]
    presenter = {
        "name": session.presenter_name,
        "name_kana": session.presenter_name_kana,
        "affiliation": session.presenter_affiliation,
    }
    prompt = {
        "task": "Generate AR poster presentation script segments. Use only registered knowledge. Do not invent claims.",
        "session": {"title": session.title, "abstract": session.abstract, "event_name": session.event_name},
        "presenter": presenter,
        "audience_profile": profile,
        "language": language,
        "panels": panel_payload,
        "knowledge_chunks": chunk_payload,
        "rules": [
            "Each panel segment must cite 1-3 evidence chunk ids.",
            "If knowledge is weak, phrase conservatively.",
            "Output concise spoken text for TTS — short sentences, no bullet points.",
            "If presenter.name is provided, the opening segment MUST include 'はじめまして、〜と申します' (ja) or 'Hi, I'm <name>' (en) followed by the affiliation. If presenter.name is null, open with 'こんにちは、ご来場ありがとうございます' (ja) or 'Hi, thanks for stopping by' (en).",
            "Use presenter.name_kana only as pronunciation guidance — do not say the kana out loud unless the field equals the name.",
            "Return JSON only.",
        ],
        "style_guide": _script_style_guide(profile, language),
    }
    schema = {
        "type": "object",
        "additionalProperties": False,
        "properties": {
            "segments": {
                "type": "array",
                "items": {
                    "type": "object",
                    "additionalProperties": False,
                    "properties": {
                        "panel_id": {"type": ["string", "null"]},
                        "segment_order": {"type": "integer"},
                        "segment_type": {"type": "string", "enum": ["opening", "panel_explain", "limitation", "closing"]},
                        "text": {"type": "string"},
                        "evidence_chunk_ids": {"type": "array", "items": {"type": "string"}},
                    },
                    "required": ["panel_id", "segment_order", "segment_type", "text", "evidence_chunk_ids"],
                },
            }
        },
        "required": ["segments"],
    }
    system_prompt_ja = (
        "あなたはAR上で研究ポスターを発表するアバター用の台本ライターです。"
        "登録された根拠だけを使い、根拠を超える主張はしません。"
        "出力は実際にアバターが話す自然な日本語で、論文調や直訳調を避けます。"
        "不必要なカタカナ用語は和語・漢語に言い換え、必要な専門語は短く言い添えます。"
    )
    system_prompt_en = (
        "You write the spoken script for an AR avatar presenting a research poster. "
        "Stay strictly grounded in registered evidence. Use natural spoken English; "
        "avoid jargon when plain words suffice."
    )
    payload = {
        "model": settings.openai_model,
        "input": [
            {
                "role": "system",
                "content": system_prompt_ja if language == "ja" else system_prompt_en,
            },
            {"role": "user", "content": json.dumps(prompt, ensure_ascii=False)},
        ],
        "reasoning": {"effort": settings.openai_reasoning_effort},
        "text": {
            "format": {
                "type": "json_schema",
                "name": "poster_script_segments",
                "strict": True,
                "schema": schema,
            }
        },
    }
    result = _extract_openai_json(await _call_openai_responses(payload))
    raw_segments = result.get("segments", [])
    if not isinstance(raw_segments, list) or not raw_segments:
        raise HTTPException(status_code=502, detail="OpenAI script agent returned no segments")
    if replace_existing:
        db.query(ScriptSegment).filter(
            ScriptSegment.session_id == session.id,
            ScriptSegment.profile == profile,
            ScriptSegment.language == language,
        ).delete()
        db.flush()
    valid_panel_ids = {panel.id for panel in panels}
    valid_chunk_ids = {chunk.id for chunk in chunks}
    created: list[ScriptSegment] = []
    for item in raw_segments:
        panel_id = item.get("panel_id")
        if panel_id is not None and panel_id not in valid_panel_ids:
            panel_id = None
        evidence_ids = [chunk_id for chunk_id in item.get("evidence_chunk_ids", []) if chunk_id in valid_chunk_ids][:3]
        text = str(item.get("text", "")).strip()
        if not text:
            continue
        segment = ScriptSegment(
            id=str(uuid4()),
            session_id=session.id,
            profile=profile,
            language=language,
            panel_id=panel_id,
            segment_order=int(item.get("segment_order", len(created))),
            segment_type=item.get("segment_type") if item.get("segment_type") in SEGMENT_TYPES else "panel_explain",
            text=text,
            evidence_chunk_ids_json=json.dumps(evidence_ids),
            status="needs_review" if not evidence_ids and item.get("segment_type") == "panel_explain" else "draft",
            duration_estimate_sec=max(5, len(text) // (18 if language == "ja" else 14)),
        )
        db.add(segment)
        created.append(segment)
    if not created:
        raise HTTPException(status_code=502, detail="OpenAI script agent returned unusable segments")
    created.sort(key=lambda s: s.segment_order)
    for index, segment in enumerate(created):
        segment.segment_order = index
    db.commit()
    regenerate_cues_from_script(db, session.id, profile, language)
    return created


def _fallback_simulated_qas(
    db: Session,
    session: SessionModel,
    profile: str,
    language: str,
    count: int,
    replace_existing: bool,
) -> list[SimulatedQa]:
    chunks = list_rag_chunks(db, session.id, language, for_live_qa=True)
    if replace_existing:
        db.query(SimulatedQa).filter(
            SimulatedQa.session_id == session.id,
            SimulatedQa.profile == profile,
            SimulatedQa.language == language,
        ).delete()
        db.flush()
    created: list[SimulatedQa] = []
    templates = (
        [
            "What is the main contribution here?",
            "What evidence supports this claim?",
            "What is the limitation of this approach?",
            "How should I interpret this panel?",
            "What would change for a different audience?",
            "What is the next step of this research?",
        ]
        if language == "en"
        else [
            "この研究の主な貢献は何ですか？",
            "この主張を支える根拠は何ですか？",
            "この方法の限界は何ですか？",
            "このパネルはどう読めばよいですか？",
            "対象者が変わると説明はどう変わりますか？",
            "次の研究課題は何ですか？",
        ]
    )
    for index, chunk in enumerate(chunks[: max(1, count)]):
        question = templates[index % len(templates)]
        answer = _shorten_for_profile(chunk.text, profile, language)
        if language == "en":
            answer = f"Based on the registered material, {answer}"
        else:
            answer = f"登録済み資料では、{answer}"
        item = SimulatedQa(
            id=str(uuid4()),
            session_id=session.id,
            profile=profile,
            language=language,
            panel_id=chunk.panel_id,
            question=question,
            answer=answer,
            evidence_chunk_ids_json=json.dumps([chunk.id]),
            status="draft",
            source="fallback",
        )
        db.add(item)
        created.append(item)
        if len(created) >= count:
            break
    db.commit()
    return created


async def generate_simulated_qas_agent(
    db: Session,
    session: SessionModel,
    profile: str,
    language: str,
    count: int,
    replace_existing: bool,
    use_openai: bool,
) -> list[SimulatedQa]:
    if count <= 0:
        return []
    settings = get_settings()
    if not use_openai or not settings.openai_api_key:
        return _fallback_simulated_qas(db, session, profile, language, count, replace_existing)
    panels = list_panels(db, session.id)
    chunks = list_rag_chunks(db, session.id, language, for_live_qa=True)
    if not chunks:
        return []
    chunk_payload = [
        {
            "id": chunk.id,
            "panel_id": chunk.panel_id,
            "text": _shorten_for_profile(chunk.text, "professional", language),
            "metadata": json.loads(chunk.metadata_json or "{}"),
        }
        for chunk in chunks[:50]
    ]
    prompt = {
        "task": "Generate simulated audience questions and grounded answers for author approval.",
        "session": {"title": session.title, "abstract": session.abstract, "event_name": session.event_name},
        "audience_profile": profile,
        "language": language,
        "question_count": count,
        "panels": [{"id": panel.id, "label": panel.label, "order": panel.order_index} for panel in panels],
        "qa_enabled_knowledge_chunks": chunk_payload,
        "rules": [
            "Use only registered knowledge chunks.",
            "Mix easy, expected, critical, and limitation-oriented questions.",
            "Each answer must cite 1-3 evidence chunk ids.",
            "If evidence is weak, make the answer conservative.",
            "Return JSON only.",
        ],
        "style_guide": _script_style_guide(profile, language),
    }
    schema = {
        "type": "object",
        "additionalProperties": False,
        "properties": {
            "qas": {
                "type": "array",
                "items": {
                    "type": "object",
                    "additionalProperties": False,
                    "properties": {
                        "panel_id": {"type": ["string", "null"]},
                        "question": {"type": "string"},
                        "answer": {"type": "string"},
                        "evidence_chunk_ids": {"type": "array", "items": {"type": "string"}},
                    },
                    "required": ["panel_id", "question", "answer", "evidence_chunk_ids"],
                },
            }
        },
        "required": ["qas"],
    }
    qa_system_ja = (
        "あなたはAR上で研究ポスターを発表するアバターのリハーサル用想定質問を作ります。"
        "登録された根拠だけを使い、自然な日本語の話し言葉で出力します。"
        "質問者の口調(来場者の気軽な質問口調)と、回答者(発表者)の丁寧な口調を区別します。"
        "不必要なカタカナ用語は避け、必要な専門語は短く言い添えます。"
    )
    qa_system_en = (
        "You write rehearsal Q&A for a research poster, grounded only in registered evidence. "
        "Questions read as a curious visitor; answers are polite and concise."
    )
    payload = {
        "model": settings.openai_model,
        "input": [
            {
                "role": "system",
                "content": qa_system_ja if language == "ja" else qa_system_en,
            },
            {"role": "user", "content": json.dumps(prompt, ensure_ascii=False)},
        ],
        "reasoning": {"effort": settings.openai_reasoning_effort},
        "text": {
            "format": {
                "type": "json_schema",
                "name": "simulated_poster_qas",
                "strict": True,
                "schema": schema,
            }
        },
    }
    result = _extract_openai_json(await _call_openai_responses(payload))
    raw_qas = result.get("qas", [])
    if not isinstance(raw_qas, list) or not raw_qas:
        return _fallback_simulated_qas(db, session, profile, language, count, replace_existing)
    if replace_existing:
        db.query(SimulatedQa).filter(
            SimulatedQa.session_id == session.id,
            SimulatedQa.profile == profile,
            SimulatedQa.language == language,
        ).delete()
        db.flush()
    valid_panel_ids = {panel.id for panel in panels}
    valid_chunk_ids = {chunk.id for chunk in chunks}
    created: list[SimulatedQa] = []
    for item in raw_qas:
        question = str(item.get("question", "")).strip()
        answer = str(item.get("answer", "")).strip()
        if not question or not answer:
            continue
        panel_id = item.get("panel_id")
        if panel_id is not None and panel_id not in valid_panel_ids:
            panel_id = None
        evidence_ids = [chunk_id for chunk_id in item.get("evidence_chunk_ids", []) if chunk_id in valid_chunk_ids][:3]
        if not evidence_ids:
            continue
        qa = SimulatedQa(
            id=str(uuid4()),
            session_id=session.id,
            profile=profile,
            language=language,
            panel_id=panel_id,
            question=question,
            answer=answer,
            evidence_chunk_ids_json=json.dumps(evidence_ids),
            status="draft",
            source="openai",
        )
        db.add(qa)
        created.append(qa)
        if len(created) >= count:
            break
    if not created:
        return _fallback_simulated_qas(db, session, profile, language, count, replace_existing)
    db.commit()
    await embed_simulated_qa_questions(db, session.id)
    return created


def list_simulated_qas(
    db: Session,
    session_id: str,
    profile: str | None = None,
    language: str | None = None,
) -> list[SimulatedQa]:
    stmt = select(SimulatedQa).where(SimulatedQa.session_id == session_id)
    if profile:
        stmt = stmt.where(SimulatedQa.profile == profile)
    if language:
        stmt = stmt.where(SimulatedQa.language == language)
    stmt = stmt.order_by(SimulatedQa.profile.asc(), SimulatedQa.language.asc(), SimulatedQa.created_at.asc())
    return list(db.scalars(stmt))


def update_simulated_qa(
    db: Session,
    session_id: str,
    qa_id: str,
    status_value: str | None,
    question: str | None,
    answer: str | None,
) -> SimulatedQa:
    qa = db.get(SimulatedQa, qa_id)
    if qa is None or qa.session_id != session_id:
        raise HTTPException(status_code=404, detail="Simulated Q&A not found")
    if status_value is not None:
        qa.status = status_value
    if question is not None:
        qa.question = question
        qa.embedding_status = "stale"
        qa.question_embedding_json = None
        if status_value is None:
            qa.status = "draft"
    if answer is not None:
        qa.answer = answer
        if status_value is None:
            qa.status = "draft"
    db.commit()
    db.refresh(qa)
    return qa


def simulated_qa_to_read(qa: SimulatedQa) -> dict:
    return {
        "id": qa.id,
        "session_id": qa.session_id,
        "profile": qa.profile,
        "language": qa.language,
        "panel_id": qa.panel_id,
        "question": qa.question,
        "answer": qa.answer,
        "evidence_chunk_ids": _json_list(qa.evidence_chunk_ids_json),
        "status": qa.status,
        "source": qa.source,
    }


def list_script_segments(
    db: Session,
    session_id: str,
    profile: str | None = None,
    language: str | None = None,
) -> list[ScriptSegment]:
    stmt = select(ScriptSegment).where(ScriptSegment.session_id == session_id)
    if profile:
        stmt = stmt.where(ScriptSegment.profile == profile)
    if language:
        stmt = stmt.where(ScriptSegment.language == language)
    stmt = stmt.order_by(ScriptSegment.profile.asc(), ScriptSegment.language.asc(), ScriptSegment.segment_order.asc())
    return list(db.scalars(stmt))


def update_script_segment(db: Session, session_id: str, segment_id: str, text: str | None, status_value: str | None) -> ScriptSegment:
    segment = db.get(ScriptSegment, segment_id)
    if segment is None or segment.session_id != session_id:
        raise HTTPException(status_code=404, detail="Script segment not found")
    if text is not None:
        segment.text = text
        segment.duration_estimate_sec = max(5, len(text) // (18 if segment.language == "ja" else 14))
        segment.tts_asset_id = None
        if status_value is None:
            segment.status = "draft"
    if status_value is not None:
        segment.status = status_value
    db.commit()
    db.refresh(segment)
    return segment


def script_segment_to_read(segment: ScriptSegment) -> dict:
    return {
        "id": segment.id,
        "session_id": segment.session_id,
        "profile": segment.profile,
        "language": segment.language,
        "panel_id": segment.panel_id,
        "segment_order": segment.segment_order,
        "segment_type": segment.segment_type,
        "text": segment.text,
        "evidence_chunk_ids": _json_list(segment.evidence_chunk_ids_json),
        "status": segment.status,
        "tts_asset_id": segment.tts_asset_id,
        "duration_estimate_sec": segment.duration_estimate_sec,
    }


def knowledge_chunk_to_read(chunk: KnowledgeChunk) -> dict:
    return {
        "id": chunk.id,
        "document_id": chunk.document_id,
        "panel_id": chunk.panel_id,
        "chunk_order": chunk.chunk_order,
        "text": chunk.text,
        "language": chunk.language,
        "section_title": chunk.section_title,
        "page_number": chunk.page_number,
        "token_count": chunk.token_count,
        "embedding_model": chunk.embedding_model,
        "embedding_dimensions": chunk.embedding_dimensions,
        "embedding_status": chunk.embedding_status,
        "retrieval_enabled": chunk.retrieval_enabled,
        "script_enabled": chunk.script_enabled,
        "qa_enabled": chunk.qa_enabled,
        "quality_status": chunk.quality_status,
        "metadata": json.loads(chunk.metadata_json or "{}"),
    }


def _tts_cache_key(text: str, voice_id: str, model_id: str, output_format: str, voice_settings: dict | None) -> str:
    payload = json.dumps(
        {
            "text": text,
            "voice_id": voice_id,
            "model_id": model_id,
            "output_format": output_format,
            "voice_settings": voice_settings or {},
        },
        ensure_ascii=False,
        sort_keys=True,
    )
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


_MP3_BITRATE_BYTES_PER_SEC = {
    "mp3_44100_128": 16000.0,
    "mp3_22050_64": 8000.0,
    "mp3_44100_192": 24000.0,
    "mp3_44100_96": 12000.0,
}


def _estimate_audio_duration(byte_count: int, output_format: str) -> float | None:
    rate = _MP3_BITRATE_BYTES_PER_SEC.get(output_format)
    if rate is None or byte_count <= 0:
        return None
    return round(byte_count / rate, 3)


def _normalize_voice_settings(values: dict | None) -> dict | None:
    if not values:
        return None
    bounded: dict = {}
    for key in ("stability", "similarity_boost", "style"):
        if key in values and values[key] is not None:
            num = float(values[key])
            if not 0.0 <= num <= 1.0:
                raise HTTPException(status_code=422, detail=f"voice_settings.{key} must be between 0 and 1")
            bounded[key] = num
    if "use_speaker_boost" in values and values["use_speaker_boost"] is not None:
        bounded["use_speaker_boost"] = bool(values["use_speaker_boost"])
    return bounded or None


def record_voice_consent(
    db: Session,
    session_id: str,
    current_user: CurrentUser,
    voice_id: str,
    consent_label: str,
    notes: str | None = None,
) -> VoiceConsent:
    existing = db.scalars(
        select(VoiceConsent).where(
            VoiceConsent.session_id == session_id,
            VoiceConsent.voice_id == voice_id,
        )
    ).first()
    if existing is not None:
        return existing
    consent = VoiceConsent(
        id=str(uuid4()),
        session_id=session_id,
        voice_id=voice_id,
        provider="elevenlabs",
        confirmed_by_user_id=current_user.user_id,
        consent_label=consent_label,
        notes=notes,
    )
    db.add(consent)
    db.commit()
    db.refresh(consent)
    return consent


def list_voice_consents(db: Session, session_id: str) -> list[VoiceConsent]:
    stmt = select(VoiceConsent).where(VoiceConsent.session_id == session_id).order_by(VoiceConsent.created_at.desc())
    return list(db.scalars(stmt))


# ElevenLabs Instant Voice Cloning. Audio sample size limits:
#   * Each sample: <= 10MB raw bytes (Starter+ plan limit)
#   * Total request: <= ~25MB combined (transport / Fly proxy headroom)
# These caps are intentionally narrower than ElevenLabs' nominal 11MB/sample
# so a stray giant WAV doesn't fail at the 502 layer. We accept the common
# browser-MediaRecorder formats plus what ElevenLabs documents as supported.
VOICE_CLONE_MAX_SAMPLE_BYTES = 10 * 1024 * 1024
VOICE_CLONE_MAX_TOTAL_BYTES = 25 * 1024 * 1024
VOICE_CLONE_ACCEPTED_MIME_PREFIXES = ("audio/",)
VOICE_CLONE_ACCEPTED_MIME_TYPES = {
    # Mostly browser MediaRecorder + common upload formats. ElevenLabs
    # accepts more than this, but we whitelist to avoid forwarding random
    # binaries (e.g. a video file the user grabbed by mistake) and only
    # discovering the rejection 5+ seconds into the upload.
    "audio/mpeg",         # .mp3
    "audio/mp3",          # .mp3 (some browsers report this)
    "audio/wav",          # .wav
    "audio/wave",
    "audio/x-wav",
    "audio/webm",         # MediaRecorder default on Chromium
    "audio/ogg",
    "audio/ogg; codecs=opus",
    "audio/m4a",
    "audio/mp4",          # .m4a / aac in mp4 container
    "audio/x-m4a",
    "audio/flac",
    "audio/aac",
}


async def clone_voice_for_session(
    db: Session,
    session_id: str,
    current_user: CurrentUser,
    *,
    name: str,
    description: str | None,
    consent_label: str,
    files: list[UploadFile],
    set_as_session_voice: bool = True,
    remove_background_noise: bool = False,
) -> tuple[str, VoiceConsent, AvatarConfig | None]:
    """Forward a multipart audio bundle to ElevenLabs Instant Voice Cloning,
    record consent, and (by default) wire the resulting voice ID into the
    session's AvatarConfig so subsequent TTS preview / synthesis uses it.

    Returns ``(voice_id, consent_row, updated_avatar_config)``. The
    avatar_config is ``None`` when ``set_as_session_voice=False`` so the
    caller can audition the cloned voice before committing it.

    Failure modes we deliberately surface up the stack:

    - ElevenLabs API key absent → 503 (operator config issue)
    - Plan doesn't allow cloning → forward ElevenLabs' own 4xx body so the
      operator sees "Subscription does not allow voice cloning" verbatim
    - Sample too large / wrong type → 422 with the offending file name so
      the user knows which take to re-record

    Auto-heal contract (matches Tier 1/2/3): if the operator passes
    ``set_as_session_voice=False`` and ElevenLabs succeeds but our DB
    write fails, the voice ID still exists upstream; we don't try to
    delete it because that would silently nuke a manual rebind path.
    """
    settings = get_settings()
    if not settings.elevenlabs_api_key:
        raise HTTPException(
            status_code=503,
            detail="ElevenLabs API key not configured on this server",
        )
    cleaned_name = (name or "").strip()
    if not cleaned_name:
        raise HTTPException(status_code=422, detail="Voice name is required")
    if len(cleaned_name) > 80:
        raise HTTPException(status_code=422, detail="Voice name must be 80 characters or fewer")
    cleaned_consent = (consent_label or "").strip()
    if not cleaned_consent:
        raise HTTPException(
            status_code=422,
            detail="Consent label is required (operator must explicitly affirm clone consent)",
        )
    if not files:
        raise HTTPException(status_code=422, detail="At least one audio sample is required")
    if len(files) > 10:
        raise HTTPException(status_code=422, detail="Up to 10 audio samples per clone request")

    # Read all samples eagerly so we can validate sizes / MIME up-front
    # and produce a helpful error before invoking ElevenLabs. Memory cost
    # is bounded by VOICE_CLONE_MAX_TOTAL_BYTES.
    sample_payloads: list[tuple[str, bytes, str]] = []
    total_bytes = 0
    for upload in files:
        mime = (upload.content_type or "").lower()
        accepted = mime in VOICE_CLONE_ACCEPTED_MIME_TYPES or any(
            mime.startswith(prefix) for prefix in VOICE_CLONE_ACCEPTED_MIME_PREFIXES
        )
        if not accepted:
            raise HTTPException(
                status_code=422,
                detail=(
                    f"Unsupported audio MIME type for {upload.filename!r}: {mime!r}. "
                    "Supported: mp3, wav, m4a, ogg, webm, flac."
                ),
            )
        body = await upload.read()
        size = len(body)
        if size == 0:
            raise HTTPException(
                status_code=422,
                detail=f"Audio sample {upload.filename!r} is empty",
            )
        if size > VOICE_CLONE_MAX_SAMPLE_BYTES:
            raise HTTPException(
                status_code=413,
                detail=(
                    f"Audio sample {upload.filename!r} is too large "
                    f"({size // 1024}KB > {VOICE_CLONE_MAX_SAMPLE_BYTES // 1024}KB)."
                ),
            )
        total_bytes += size
        if total_bytes > VOICE_CLONE_MAX_TOTAL_BYTES:
            raise HTTPException(
                status_code=413,
                detail=(
                    f"Combined audio samples exceed {VOICE_CLONE_MAX_TOTAL_BYTES // 1024}KB. "
                    "Trim the longest take and retry."
                ),
            )
        sample_payloads.append(
            (upload.filename or f"sample-{len(sample_payloads) + 1}.bin", body, mime)
        )

    # Build the multipart payload for ElevenLabs. Their docs name the file
    # field `files` (not `files[]`); httpx serializes a list of (name, ...)
    # tuples as repeated fields with the same name.
    multipart_files: list[tuple[str, tuple[str, bytes, str]]] = [
        ("files", (filename, body, mime)) for (filename, body, mime) in sample_payloads
    ]
    form_data = {
        "name": cleaned_name,
        "remove_background_noise": "true" if remove_background_noise else "false",
    }
    if description:
        # ElevenLabs caps description at ~500 chars; trim defensively.
        form_data["description"] = description.strip()[:500]

    url = "https://api.elevenlabs.io/v1/voices/add"
    headers = {"xi-api-key": settings.elevenlabs_api_key}
    try:
        # 120s ceiling: large samples + ElevenLabs' background processing
        # can stretch past the default 5s connect / 30s read.
        async with httpx.AsyncClient(timeout=httpx.Timeout(120.0, connect=15.0)) as client:
            response = await client.post(url, headers=headers, data=form_data, files=multipart_files)
    except httpx.HTTPError as exc:
        raise HTTPException(status_code=502, detail=f"ElevenLabs voice clone request failed: {exc}") from exc

    if response.status_code >= 400:
        # Surface ElevenLabs' error body to the operator. Common cases:
        #   401 — API key invalid
        #   403 / 422 — plan does not allow cloning, or quota exceeded
        #   429 — rate limit
        body_text = (response.text or "")[:400]
        raise HTTPException(
            status_code=502 if response.status_code >= 500 else response.status_code,
            detail=f"ElevenLabs voice clone failed: {response.status_code} {body_text}",
        )

    try:
        body = response.json()
    except Exception:
        body = {}
    new_voice_id = (body or {}).get("voice_id")
    if not new_voice_id:
        raise HTTPException(
            status_code=502,
            detail="ElevenLabs voice clone returned no voice_id",
        )

    consent_row = record_voice_consent(
        db,
        session_id=session_id,
        current_user=current_user,
        voice_id=new_voice_id,
        consent_label=cleaned_consent,
        notes=f"Cloned via dashboard: name={cleaned_name!r}, samples={len(sample_payloads)}",
    )

    updated_config: AvatarConfig | None = None
    if set_as_session_voice:
        config = db.get(AvatarConfig, session_id)
        if config is None:
            # Session has no avatar row yet — create one with default values
            # so the freshly-cloned voice doesn't get orphaned.
            config = AvatarConfig(session_id=session_id, voice_id=new_voice_id)
            db.add(config)
        else:
            config.voice_id = new_voice_id
        db.commit()
        db.refresh(config)
        updated_config = config

    return new_voice_id, consent_row, updated_config


_VOICE_SAMPLE_DEFAULT_TEXT_JA = (
    "こんにちは。これは音声合成のプレビューです。声の確認に使ってください。"
)
_VOICE_SAMPLE_DEFAULT_TEXT_EN = (
    "Hello. This is a voice preview sample for verification."
)


async def get_or_create_voice_sample(
    db: Session,
    voice_id: str,
    text: str | None,
    language: str,
) -> tuple[VoiceSample, bool]:
    """Return a cached preview audio for (voice_id, text, language, model).

    Cache hits return the existing row + asset without calling ElevenLabs.
    Misses make exactly one API call. Designed so the dashboard can let
    operators audition voice IDs they pasted from the ElevenLabs Dashboard
    without burning credits on every click.

    Returns (sample, cached): `cached` is True when no API call was made.
    """
    settings = get_settings()
    if not settings.elevenlabs_api_key:
        raise HTTPException(status_code=503, detail="ElevenLabs API key not configured")

    actual_text = (text or "").strip() or (
        _VOICE_SAMPLE_DEFAULT_TEXT_JA if language == "ja" else _VOICE_SAMPLE_DEFAULT_TEXT_EN
    )
    actual_model = settings.elevenlabs_model_id
    voice_settings = {
        "stability": 0.45,
        "similarity_boost": 0.75,
        "style": 0.0,
        "use_speaker_boost": True,
    }
    cache_key = _tts_cache_key(
        actual_text,
        voice_id,
        actual_model,
        settings.elevenlabs_output_format,
        voice_settings,
    )

    existing = db.scalars(
        select(VoiceSample).where(VoiceSample.cache_key == cache_key)
    ).first()
    if existing is not None:
        return existing, True

    # Cache miss → call ElevenLabs.
    url = f"https://api.elevenlabs.io/v1/text-to-speech/{voice_id}/stream"
    headers = {
        "xi-api-key": settings.elevenlabs_api_key,
        "accept": "audio/mpeg",
        "content-type": "application/json",
    }
    body = {
        "text": actual_text,
        "model_id": actual_model,
        "language_code": language,
        "apply_language_text_normalization": language == "ja",
        "voice_settings": voice_settings,
    }
    audio = bytearray()
    try:
        async with httpx.AsyncClient(timeout=60.0) as client:
            async with client.stream(
                "POST", url,
                headers=headers,
                params={"output_format": settings.elevenlabs_output_format},
                json=body,
            ) as response:
                if response.status_code >= 400:
                    detail = await response.aread()
                    raise HTTPException(
                        status_code=502,
                        detail=(
                            f"ElevenLabs voice sample failed: {response.status_code} "
                            f"{detail[:240].decode('utf-8', errors='ignore')}"
                        ),
                    )
                async for chunk in response.aiter_bytes():
                    if chunk:
                        audio.extend(chunk)
    except httpx.HTTPError as exc:
        raise HTTPException(status_code=502, detail=f"ElevenLabs request failed: {exc}") from exc
    if not audio:
        raise HTTPException(status_code=502, detail="ElevenLabs returned empty audio")

    sample_id = str(uuid4())
    storage = get_storage()
    storage_path, size_bytes = storage.save_bytes(
        session_id="voice-samples",  # pseudo-session: keeps storage path layout consistent
        folder="elevenlabs",
        stored_name=f"{sample_id}.mp3",
        content=bytes(audio),
        content_type="audio/mpeg",
    )
    sample = VoiceSample(
        id=sample_id,
        cache_key=cache_key,
        voice_id=voice_id,
        text=actual_text,
        language=language,
        model_id=actual_model,
        storage_path=storage_path,
        size_bytes=size_bytes,
    )
    db.add(sample)
    db.commit()
    db.refresh(sample)
    return sample, False


def signed_voice_sample_url(sample: VoiceSample, ttl_seconds: int | None = None) -> tuple[str, int]:
    """Sign a download URL for a voice sample's audio. Mirrors how
    `signed_asset_url` routes by storage backend, but voice samples don't
    live in the `assets` table (no session_id), so the helper takes the
    path + a synthetic asset_id (the sample id) directly."""
    storage = get_storage()
    return storage.signed_download_url(sample.id, sample.storage_path, ttl_seconds)


async def synthesize_segment_tts(
    db: Session,
    session_id: str,
    segment_id: str,
    voice_id: str | None,
    model_id: str | None,
    consent_confirmed: bool,
    consent_label: str | None,
    current_user: CurrentUser,
    voice_settings: dict | None = None,
) -> TtsAsset:
    settings = get_settings()
    segment = db.get(ScriptSegment, segment_id)
    if segment is None or segment.session_id != session_id:
        raise HTTPException(status_code=404, detail="Script segment not found")
    if not segment.text or not segment.text.strip():
        raise HTTPException(status_code=422, detail="Script segment text is empty")
    if len(segment.text) > 4000:
        raise HTTPException(status_code=422, detail="Script segment exceeds ElevenLabs single-call limit (4000 chars)")
    actual_voice_id = voice_id or settings.elevenlabs_default_voice_id
    if not settings.elevenlabs_api_key or not actual_voice_id:
        raise HTTPException(status_code=503, detail="ElevenLabs API key and voice_id are required")
    if not consent_confirmed:
        consent = db.scalars(
            select(VoiceConsent).where(
                VoiceConsent.session_id == session_id,
                VoiceConsent.voice_id == actual_voice_id,
            )
        ).first()
        if consent is None:
            raise HTTPException(status_code=409, detail="Voice consent confirmation is required before TTS")
    else:
        record_voice_consent(
            db,
            session_id=session_id,
            current_user=current_user,
            voice_id=actual_voice_id,
            consent_label=consent_label or "Presenter confirmed voice usage consent",
        )
    actual_model_id = model_id or settings.elevenlabs_model_id
    normalized_voice = _normalize_voice_settings(voice_settings)
    effective_voice = {
        "stability": 0.45,
        "similarity_boost": 0.75,
        "style": 0.0,
        "use_speaker_boost": True,
    }
    if normalized_voice:
        effective_voice.update(normalized_voice)
    cache_key = _tts_cache_key(
        segment.text,
        actual_voice_id,
        actual_model_id,
        settings.elevenlabs_output_format,
        effective_voice,
    )
    existing = db.scalars(
        select(TtsAsset).where(
            TtsAsset.segment_id == segment.id,
            TtsAsset.cache_key == cache_key,
        )
    ).first()
    if existing is not None:
        segment.tts_asset_id = existing.id
        db.commit()
        db.refresh(existing)
        return existing

    url = f"https://api.elevenlabs.io/v1/text-to-speech/{actual_voice_id}/stream"
    headers = {
        "xi-api-key": settings.elevenlabs_api_key,
        "accept": "audio/mpeg",
        "content-type": "application/json",
    }
    # If the session has a presenter name + kana reading, swap the kanji name
    # for kana so ElevenLabs reads it correctly (Japanese TTS misreads many
    # rare given names). English doesn't need this.
    spoken_text = segment.text
    session = db.get(SessionModel, segment.session_id)
    if (
        session
        and segment.language == "ja"
        and session.presenter_name
        and session.presenter_name_kana
        and session.presenter_name in spoken_text
        and session.presenter_name_kana != session.presenter_name
    ):
        spoken_text = spoken_text.replace(session.presenter_name, session.presenter_name_kana)
    body = {
        "text": spoken_text,
        "model_id": actual_model_id,
        "language_code": segment.language,
        "apply_language_text_normalization": segment.language == "ja",
        "voice_settings": effective_voice,
    }
    audio = bytearray()
    try:
        async with httpx.AsyncClient(timeout=60.0) as client:
            async with client.stream(
                "POST",
                url,
                headers=headers,
                params={"output_format": settings.elevenlabs_output_format},
                json=body,
            ) as response:
                if response.status_code >= 400:
                    detail = await response.aread()
                    raise HTTPException(
                        status_code=502,
                        detail=f"ElevenLabs TTS failed: {response.status_code} {detail[:240].decode('utf-8', errors='ignore')}",
                    )
                async for chunk in response.aiter_bytes():
                    if chunk:
                        audio.extend(chunk)
    except httpx.HTTPError as exc:
        raise HTTPException(status_code=502, detail=f"ElevenLabs TTS request failed: {exc}") from exc
    if not audio:
        raise HTTPException(status_code=502, detail="ElevenLabs TTS returned empty audio")

    tts_id = str(uuid4())
    asset_id = str(uuid4())
    storage = get_storage()
    storage_path, size_bytes = storage.save_bytes(
        session_id=session_id,
        folder="tts",
        stored_name=f"{tts_id}.mp3",
        content=bytes(audio),
    )
    try:
        asset = Asset(
            id=asset_id,
            session_id=session_id,
            kind="tts-audio",
            storage_path=storage_path,
            file_name=f"{segment.profile}-{segment.language}-{segment.segment_order}.mp3",
            mime_type="audio/mpeg",
            size_bytes=size_bytes,
            status="ready",
        )
        duration_sec = _estimate_audio_duration(size_bytes, settings.elevenlabs_output_format)
        tts_asset = TtsAsset(
            id=tts_id,
            session_id=session_id,
            segment_id=segment.id,
            asset_id=asset.id,
            provider="elevenlabs",
            voice_id=actual_voice_id,
            model_id=actual_model_id,
            language=segment.language,
            duration_sec=duration_sec,
            cache_key=cache_key,
        )
        db.add(asset)
        db.add(tts_asset)
        db.flush()
        segment.tts_asset_id = tts_asset.id
        if duration_sec is not None:
            segment.duration_estimate_sec = max(1, int(round(duration_sec)))
        segment.updated_at = datetime.now(UTC)
        db.commit()
        db.refresh(tts_asset)
        return tts_asset
    except Exception:
        db.rollback()
        storage.delete_path(storage_path)
        raise


async def synthesize_all_session_tts(
    db: Session,
    session_id: str,
    profile: str | None,
    language: str | None,
    voice_id: str | None,
    model_id: str | None,
    consent_confirmed: bool,
    consent_label: str | None,
    current_user: CurrentUser,
    voice_settings: dict | None,
) -> dict:
    segments = list_script_segments(db, session_id, profile, language)
    targets = [segment for segment in segments if segment.text and segment.text.strip()]
    results: dict = {"synthesized": [], "skipped": [], "failed": []}
    for segment in targets:
        try:
            tts = await synthesize_segment_tts(
                db,
                session_id,
                segment.id,
                voice_id,
                model_id,
                consent_confirmed,
                consent_label,
                current_user,
                voice_settings,
            )
            results["synthesized"].append({"segment_id": segment.id, "tts_asset_id": tts.id})
        except HTTPException as exc:
            if exc.status_code in {409, 422}:
                results["skipped"].append({"segment_id": segment.id, "reason": str(exc.detail)})
            else:
                results["failed"].append({"segment_id": segment.id, "status": exc.status_code, "reason": str(exc.detail)})
    return results


def ensure_avatar_config(db: Session, session_id: str) -> AvatarConfig:
    config = db.get(AvatarConfig, session_id)
    if config is None:
        config = AvatarConfig(session_id=session_id)
        db.add(config)
        db.commit()
        db.refresh(config)
    return config


def _parse_vec3(value: str | None, fallback: list[float]) -> list[float]:
    if not value:
        return fallback
    try:
        parsed = json.loads(value)
    except json.JSONDecodeError:
        return fallback
    if not isinstance(parsed, list) or len(parsed) != 3:
        return fallback
    try:
        return [float(item) for item in parsed]
    except (TypeError, ValueError):
        return fallback


def avatar_config_to_read(config: AvatarConfig, db: Session | None = None) -> dict:
    metadata: dict | None = None
    if config.metadata_json:
        try:
            parsed = json.loads(config.metadata_json)
            if isinstance(parsed, dict):
                metadata = parsed
        except json.JSONDecodeError:
            metadata = None

    # When the session has a built .gvrm and we have a DB handle, mint a
    # signed URL + report the build_version (so the UI can flag stub
    # bindings). avatar_config_to_read is called from places that don't
    # always have `db` (e.g., synchronous serializers); without it we just
    # omit the URL fields and the dashboard falls back to "no preview".
    gvrm_url: str | None = None
    gvrm_expires_at: int | None = None
    gvrm_build_version: str | None = None
    if db is not None and config.gvrm_asset_id:
        try:
            gvrm_url, gvrm_expires_at = signed_asset_url(db, config.gvrm_asset_id)
        except Exception:
            # Sign failures shouldn't break the whole avatar read — degrade
            # quietly so the UI just hides the preview.
            pass
        # Resolve the .gvrm's build_version via the AvatarBuildJob row that
        # produced this asset. We could parse data.json from the .gvrm zip
        # on every request, but that's an unnecessary blob fetch — the
        # build worker stamps `build_version` into the job row at success
        # time and the value never changes thereafter.
        try:
            job = (
                db.execute(
                    select(AvatarBuildJob)
                    .where(AvatarBuildJob.output_asset_id == config.gvrm_asset_id)
                    .order_by(AvatarBuildJob.created_at.desc())
                    .limit(1)
                )
                .scalars()
                .first()
            )
            if job is not None and job.build_version:
                gvrm_build_version = job.build_version
        except Exception:
            pass
        # Legacy fallback: very early builds stamped `_buildVersion` into
        # config.metadata_json (no AvatarBuildJob.build_version column).
        if gvrm_build_version is None and metadata and isinstance(metadata, dict):
            gvrm_build_version = metadata.get("_buildVersion")

    # Always include the server's CURRENT preprocess version so the
    # dashboard can compare and flag stale builds. Imported lazily to
    # avoid circular imports between service.py and avatar_preprocess.py
    # (preprocess pulls in numpy/scipy which we don't want at module
    # import time for non-avatar code paths).
    from app.avatar_preprocess import BUILD_VERSION as _CURRENT_BUILD_VERSION

    return {
        "session_id": config.session_id,
        "runtime_type": config.runtime_type,
        "gvrm_asset_id": config.gvrm_asset_id,
        "gvrm_url": gvrm_url,
        "gvrm_url_expires_at": gvrm_expires_at,
        "gvrm_build_version": gvrm_build_version,
        "expected_build_version": _CURRENT_BUILD_VERSION,
        "source_ply_asset_id": config.source_ply_asset_id,
        "source_vrm_asset_id": config.source_vrm_asset_id,
        "display_name": config.display_name,
        "attribution": config.attribution,
        "license_note": config.license_note,
        "placement_position": _parse_vec3(config.placement_position_json, [0.65, -0.2, -0.25]),
        "placement_rotation": _parse_vec3(config.placement_rotation_json, [0.0, -20.0, 0.0]),
        "placement_scale": config.placement_scale,
        "avatar_height_scale": config.avatar_height_scale,
        "voice_id": config.voice_id,
        "base_vrm_preset": config.base_vrm_preset,
        "default_animation": config.default_animation,
        "behavior_idle": config.behavior_idle,
        "behavior_explain": config.behavior_explain,
        "behavior_listening": config.behavior_listening,
        "behavior_thinking": config.behavior_thinking,
        "metadata": metadata,
    }


def update_avatar_config(db: Session, session_id: str, payload: dict) -> AvatarConfig:
    config = ensure_avatar_config(db, session_id)
    runtime_type = payload.get("runtime_type")
    if runtime_type is not None:
        if runtime_type not in AVATAR_RUNTIME_TYPES:
            raise HTTPException(status_code=422, detail="Invalid avatar runtime_type")
        if runtime_type == "gvrm" and config.gvrm_asset_id is None:
            raise HTTPException(status_code=409, detail="A .gvrm asset must be uploaded before selecting runtime_type='gvrm'")
        if runtime_type == "scaniverse-source" and config.source_ply_asset_id is None:
            raise HTTPException(status_code=409, detail="A Scaniverse PLY/SPZ source must be uploaded before selecting runtime_type='scaniverse-source'")
        config.runtime_type = runtime_type
    for field in ("display_name", "attribution", "license_note", "default_animation",
                  "behavior_idle", "behavior_explain", "behavior_listening", "behavior_thinking"):
        if field in payload and payload[field] is not None:
            setattr(config, field, payload[field])
    if payload.get("placement_position") is not None:
        config.placement_position_json = json.dumps([float(v) for v in payload["placement_position"]])
    if payload.get("placement_rotation") is not None:
        config.placement_rotation_json = json.dumps([float(v) for v in payload["placement_rotation"]])
    if payload.get("placement_scale") is not None:
        config.placement_scale = float(payload["placement_scale"])
    if payload.get("avatar_height_scale") is not None:
        scale = float(payload["avatar_height_scale"])
        if not 0.5 <= scale <= 2.0:
            raise HTTPException(
                status_code=422,
                detail="avatar_height_scale must be between 0.5 and 2.0",
            )
        config.avatar_height_scale = scale
    # Empty string or pure whitespace means "clear" — set to None so the
    # session falls back to ELEVENLABS_DEFAULT_VOICE_ID. This matches the
    # dashboard's UX where blanking the input restores the default.
    if "voice_id" in payload:
        v = payload["voice_id"]
        config.voice_id = v.strip() if isinstance(v, str) and v.strip() else None
    if "base_vrm_preset" in payload:
        v = payload["base_vrm_preset"]
        if v in (None, "", "fem_vroid", "masc_vroid"):
            config.base_vrm_preset = v if v else None
        else:
            raise HTTPException(
                status_code=422,
                detail="base_vrm_preset must be 'fem_vroid', 'masc_vroid', or empty",
            )
    db.commit()
    db.refresh(config)
    return config


def _validate_gvrm_metadata(metadata: dict) -> None:
    missing = GVRM_REQUIRED_METADATA_KEYS - metadata.keys()
    if missing:
        raise HTTPException(
            status_code=422,
            detail=f"GVRM data.json is missing required keys: {sorted(missing)}",
        )
    scale = metadata.get("modelScale")
    if not isinstance(scale, (int, float)) or scale <= 0:
        raise HTTPException(status_code=422, detail="GVRM modelScale must be a positive number")
    for key in ("gsPosition",):
        value = metadata.get(key)
        if not isinstance(value, list) or len(value) != 3:
            raise HTTPException(status_code=422, detail=f"GVRM {key} must be a 3-element list")
    quat = metadata.get("gsQuaternion")
    if not isinstance(quat, list) or len(quat) != 4:
        raise HTTPException(status_code=422, detail="GVRM gsQuaternion must be a 4-element list")
    for key in ("boneOperations", "splatVertexIndices", "splatBoneIndices", "splatRelativePoses"):
        if not isinstance(metadata.get(key), list):
            raise HTTPException(status_code=422, detail=f"GVRM {key} must be a list")


def validate_gvrm_archive(content: bytes) -> dict:
    if not content[:4] == b"PK\x03\x04":
        raise HTTPException(status_code=415, detail=".gvrm must be a ZIP archive")
    try:
        archive = zipfile.ZipFile(io.BytesIO(content))
    except zipfile.BadZipFile as exc:
        raise HTTPException(status_code=415, detail=f"Invalid .gvrm archive: {exc}") from exc
    names = set(archive.namelist())
    missing = GVRM_REQUIRED_ENTRIES - names
    if missing:
        raise HTTPException(
            status_code=422,
            detail=f".gvrm archive is missing required entries: {sorted(missing)}",
        )
    splat_entry = next((n for n in GVRM_SPLAT_ENTRY_CANDIDATES if n in names), None)
    if splat_entry is None:
        raise HTTPException(
            status_code=422,
            detail=(
                ".gvrm archive must contain splat data — either "
                "model.ply (Gaussian Splat PLY) or model.spz (Niantic SPZ)"
            ),
        )
    try:
        with archive.open("data.json") as handle:
            raw = handle.read().decode("utf-8")
    except (KeyError, UnicodeDecodeError) as exc:
        raise HTTPException(status_code=422, detail="Could not read .gvrm data.json") from exc
    try:
        metadata = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise HTTPException(status_code=422, detail=f"Invalid .gvrm data.json: {exc}") from exc
    if not isinstance(metadata, dict):
        raise HTTPException(status_code=422, detail=".gvrm data.json must be a JSON object")
    vrm_info = archive.getinfo("model.vrm")
    if vrm_info.file_size <= 0:
        raise HTTPException(status_code=422, detail=".gvrm model.vrm is empty")
    splat_info = archive.getinfo(splat_entry)
    if splat_info.file_size <= 0:
        raise HTTPException(status_code=422, detail=f".gvrm {splat_entry} is empty")
    with archive.open("model.vrm") as vrm_handle:
        vrm_head = vrm_handle.read(4)
    if vrm_head != b"glTF":
        raise HTTPException(status_code=422, detail=".gvrm model.vrm is not a valid GLB/VRM file")
    with archive.open(splat_entry) as splat_handle:
        splat_head = splat_handle.read(4)
    if splat_entry == "model.ply" and not splat_head.startswith(b"ply"):
        raise HTTPException(status_code=422, detail=".gvrm model.ply is not a valid PLY file")
    if splat_entry == "model.spz" and splat_head != b"NGSP":
        raise HTTPException(status_code=422, detail=".gvrm model.spz is not a valid SPZ file (missing NGSP magic)")
    _validate_gvrm_metadata(metadata)
    metadata.setdefault("_splatEntry", splat_entry)
    metadata.setdefault("_splatFormat", "spz" if splat_entry == "model.spz" else "ply")
    return metadata


def _read_avatar_upload(upload: UploadFile) -> bytes:
    return _read_upload_bytes(upload, get_settings().max_avatar_upload_bytes)


def _detach_avatar_asset(db: Session, _unused, asset_id: str | None) -> None:
    if not asset_id:
        return
    asset = db.get(Asset, asset_id)
    if asset is None:
        return
    try:
        storage_for_asset(asset).delete_path(asset.storage_path)
    except Exception:
        pass  # cleanup is best-effort
    db.delete(asset)


def upload_gvrm_archive(
    db: Session,
    session_id: str,
    upload: UploadFile,
    display_name: str | None,
    attribution: str | None,
    license_note: str | None,
) -> tuple[AvatarConfig, Asset, dict]:
    config = ensure_avatar_config(db, session_id)
    content = _read_avatar_upload(upload)
    metadata = validate_gvrm_archive(content)
    asset_id = str(uuid4())
    storage = storage_for_kind("avatar-gvrm")
    storage_path, size_bytes = storage.save_bytes(
        session_id=session_id,
        folder="avatar",
        stored_name=f"{asset_id}.gvrm",
        content=content,
    )
    try:
        asset = Asset(
            id=asset_id,
            session_id=session_id,
            kind="avatar-gvrm",
            storage_path=storage_path,
            file_name=upload.filename or f"{asset_id}.gvrm",
            mime_type="application/zip",
            size_bytes=size_bytes,
            status="ready",
        )
        db.add(asset)
        db.flush()
        previous_asset_id = config.gvrm_asset_id
        config.gvrm_asset_id = asset.id
        config.runtime_type = "gvrm"
        config.metadata_json = json.dumps(metadata, ensure_ascii=False)
        if display_name is not None:
            config.display_name = display_name
        if attribution is not None:
            config.attribution = attribution
        if license_note is not None:
            config.license_note = license_note
        db.flush()
        _detach_avatar_asset(db, storage, previous_asset_id)
        db.commit()
        db.refresh(config)
        db.refresh(asset)
        return config, asset, metadata
    except Exception:
        db.rollback()
        storage.delete_path(storage_path)
        raise


def _detect_avatar_source_role(content: bytes, filename: str | None) -> str:
    head = content[:8] if content else b""
    suffix = Path(filename or "").suffix.lower()
    if head[:4] == b"glTF":
        return "base-vrm"
    if head[:4] == b"NGSP" or (head[:2] == b"\x1f\x8b" and suffix == ".spz"):
        return "scaniverse-spz"
    if head[:3] == b"ply":
        return "scaniverse-ply"
    if suffix == ".vrm":
        return "base-vrm"
    if suffix == ".spz":
        return "scaniverse-spz"
    if suffix == ".ply":
        return "scaniverse-ply"
    raise HTTPException(status_code=415, detail="Unrecognized avatar source file. Expected .vrm, .ply, or .spz")


def upload_avatar_source(
    db: Session,
    session_id: str,
    upload: UploadFile,
) -> tuple[AvatarConfig, Asset, str]:
    config = ensure_avatar_config(db, session_id)
    content = _read_avatar_upload(upload)
    if not content:
        raise HTTPException(status_code=422, detail="Avatar source file is empty")
    role = _detect_avatar_source_role(content, upload.filename)
    asset_id = str(uuid4())
    if role == "scaniverse-ply":
        folder = "avatar/scaniverse"
        stored_name = f"{asset_id}.ply"
        kind = "avatar-scaniverse-ply"
        mime_type = "application/octet-stream"
    elif role == "scaniverse-spz":
        folder = "avatar/scaniverse"
        stored_name = f"{asset_id}.spz"
        kind = "avatar-scaniverse-spz"
        mime_type = "application/octet-stream"
    else:
        folder = "avatar"
        stored_name = f"{asset_id}.vrm"
        kind = "avatar-base-vrm"
        mime_type = "model/gltf-binary"
    storage = storage_for_kind(kind)
    storage_path, size_bytes = storage.save_bytes(
        session_id=session_id,
        folder=folder,
        stored_name=stored_name,
        content=content,
    )
    try:
        asset = Asset(
            id=asset_id,
            session_id=session_id,
            kind=kind,
            storage_path=storage_path,
            file_name=upload.filename or stored_name,
            mime_type=mime_type,
            size_bytes=size_bytes,
            status="ready",
        )
        db.add(asset)
        db.flush()
        if role in {"scaniverse-ply", "scaniverse-spz"}:
            previous = config.source_ply_asset_id
            config.source_ply_asset_id = asset.id
            if config.runtime_type == "default-vrm":
                config.runtime_type = "scaniverse-source"
        else:
            previous = config.source_vrm_asset_id
            config.source_vrm_asset_id = asset.id
        db.flush()
        _detach_avatar_asset(db, storage, previous)
        db.commit()
        db.refresh(config)
        db.refresh(asset)
        return config, asset, role
    except Exception:
        db.rollback()
        storage.delete_path(storage_path)
        raise


def list_avatar_build_jobs(db: Session, session_id: str) -> list[AvatarBuildJob]:
    return list(
        db.scalars(
            select(AvatarBuildJob)
            .where(AvatarBuildJob.session_id == session_id)
            .order_by(AvatarBuildJob.created_at.desc())
        )
    )


def get_avatar_build_job(db: Session, session_id: str, job_id: str) -> AvatarBuildJob:
    job = db.get(AvatarBuildJob, job_id)
    if job is None or job.session_id != session_id:
        raise HTTPException(status_code=404, detail="Build job not found")
    return job


def enqueue_avatar_build(db: Session, session_id: str, current_user: CurrentUser) -> AvatarBuildJob:
    """Queue a Gaussian-VRM build for the session's currently configured PLY/SPZ + base VRM.

    The actual conversion (PLY/SPZ + base VRM → .gvrm zip with skinning weights) is performed
    by an external worker; this endpoint records the request and exposes a status lifecycle so
    the dashboard can show progress. Once a worker completes the build it should call back into
    `mark_avatar_build_succeeded` (server-internal) with the produced asset ID.
    """
    config = ensure_avatar_config(db, session_id)
    if not config.source_ply_asset_id and not config.gvrm_asset_id:
        raise HTTPException(
            status_code=409,
            detail="Upload a Scaniverse PLY/SPZ scan or an existing .gvrm before requesting a build",
        )
    # source_vrm_asset_id is optional — when missing, avatar_build.py falls
    # back to the bundled CC0 fem_vroid.vrm via Settings.avatar_default_vrm_filename.
    job = AvatarBuildJob(
        id=str(uuid4()),
        session_id=session_id,
        source_ply_asset_id=config.source_ply_asset_id,
        source_vrm_asset_id=config.source_vrm_asset_id,
        status="queued",
        progress=0,
        requested_by=current_user.user_id,
        log="queued",
    )
    db.add(job)
    db.commit()
    db.refresh(job)
    return job


def mark_avatar_build_succeeded(
    db: Session, session_id: str, job_id: str, output_asset_id: str
) -> AvatarBuildJob:
    job = get_avatar_build_job(db, session_id, job_id)
    job.status = "succeeded"
    job.progress = 100
    job.output_asset_id = output_asset_id
    config = ensure_avatar_config(db, session_id)
    config.gvrm_asset_id = output_asset_id
    config.runtime_type = "gvrm"
    db.commit()
    db.refresh(job)
    return job


def mark_avatar_build_failed(db: Session, session_id: str, job_id: str, error: str) -> AvatarBuildJob:
    job = get_avatar_build_job(db, session_id, job_id)
    job.status = "failed"
    job.error = error
    db.commit()
    db.refresh(job)
    return job


def avatar_build_job_to_read(job: AvatarBuildJob) -> dict:
    return {
        "id": job.id,
        "session_id": job.session_id,
        "source_ply_asset_id": job.source_ply_asset_id,
        "source_vrm_asset_id": job.source_vrm_asset_id,
        "output_asset_id": job.output_asset_id,
        "status": job.status,
        "progress": job.progress,
        "log": job.log,
        "error": job.error,
        "requested_by": job.requested_by,
        "created_at": job.created_at.isoformat() if job.created_at else None,
        "updated_at": job.updated_at.isoformat() if job.updated_at else None,
    }
