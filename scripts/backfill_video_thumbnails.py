#!/usr/bin/env python3
"""Backfill video thumbnails for MyPhotoWebApi.

Context:
- The ingest endpoint (FileIngestionService) creates thumbnails only for image files.
- Video docs (mp4/avi/3gp) are stored with mediaType="video" but no thumbnail.
- Generating thumbnails can be slow; do it offline via this script.

What it does:
- Scan MongoDB `photos` collection for video docs missing `thumbnail`.
- For each doc, locate the file on disk and extract a JPEG frame thumbnail via ffmpeg.
- Update the doc's `thumbnail` field (binary bytes).

Safety defaults:
- Default mode is DRY RUN (no DB writes).
- Use --execute to apply updates.

Typical usage:
  # Auto-read defaults from ../MyPhotoWebApi/appsettings.json
  python3 scripts/backfill_video_thumbnails.py --execute

  # Or override:
  python3 scripts/backfill_video_thumbnails.py --uri mongodb://localhost:27017 --db MyPhotos --root /home/yww/FtpRoot/PicRootFolder --execute

Notes:
- Requires ffmpeg installed.
- Requires pymongo installed (can use repo venv or system pip).
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import subprocess
import sys
import time
from pathlib import Path
from typing import Any, Dict, Optional, Tuple


def _now_ts() -> str:
    return dt.datetime.now().strftime("%Y%m%d")


def _setup_logger(log_dir: Path, name: str) -> Tuple[Any, Path]:
    import logging

    log_dir.mkdir(parents=True, exist_ok=True)
    log_path = log_dir / f"{name}-{_now_ts()}.log"

    logger = logging.getLogger(name)
    logger.setLevel(logging.INFO)
    logger.handlers.clear()

    fmt = logging.Formatter("%(asctime)s [%(levelname)s] %(message)s")

    fh = logging.FileHandler(log_path, encoding="utf-8")
    fh.setFormatter(fmt)
    fh.setLevel(logging.INFO)

    sh = logging.StreamHandler(sys.stdout)
    sh.setFormatter(fmt)
    sh.setLevel(logging.INFO)

    logger.addHandler(fh)
    logger.addHandler(sh)

    return logger, log_path


def _read_defaults_from_appsettings(appsettings_path: Path) -> Tuple[Optional[str], Optional[str], Optional[str]]:
    """Return (rootFolder, connectionString, databaseName) if present."""
    try:
        with appsettings_path.open("r", encoding="utf-8") as f:
            cfg = json.load(f)
        s = (cfg or {}).get("MyPhotoSettings") or {}
        root = s.get("RootFolder")
        uri = s.get("ConnectionString")
        db = s.get("DatabaseName")
        return root, uri, db
    except Exception:
        return None, None, None


def _run_ffmpeg_extract(
    video_path: Path,
    out_path: Path,
    timestamp_sec: float,
    long_side: int,
    jpeg_quality: int,
    timeout_sec: int,
) -> Tuple[bool, str]:
    # Scale while keeping aspect ratio; cap the long side to long_side.
    # If input is wider than tall: scale=-2:LONG else LONG:-2
    scale_expr = (
        f"scale='if(gte(iw,ih),{long_side},-2)':'if(gte(iw,ih),-2,{long_side})'"
    )

    cmd = [
        "ffmpeg",
        "-hide_banner",
        "-loglevel",
        "error",
        "-ss",
        str(timestamp_sec),
        "-i",
        str(video_path),
        "-frames:v",
        "1",
        "-vf",
        f"{scale_expr}",
        "-q:v",
        str(jpeg_quality),
        "-y",
        str(out_path),
    ]

    try:
        p = subprocess.run(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=timeout_sec,
            check=False,
        )
        if p.returncode != 0:
            return False, (p.stderr.decode("utf-8", errors="replace") or "ffmpeg failed")
        return True, "ok"
    except subprocess.TimeoutExpired:
        return False, f"ffmpeg timeout after {timeout_sec}s"
    except FileNotFoundError:
        return False, "ffmpeg not found in PATH"


def _resolve_video_abs_path(root: Path, doc: Dict[str, Any]) -> Optional[Path]:
    rel_path = doc.get("path")
    file_name = doc.get("fileName")
    if not isinstance(rel_path, str) or not isinstance(file_name, str):
        return None

    # In C# ingest, path is normalized with '/' and trimmed leading '/'.
    rel_path = rel_path.strip("/")
    parts = [p for p in rel_path.split("/") if p]
    return root.joinpath(*parts, file_name)


def main() -> int:
    script_dir = Path(__file__).resolve().parent
    repo_root = script_dir.parent
    default_appsettings = repo_root / "MyPhotoWebApi" / "appsettings.json"

    ap = argparse.ArgumentParser()
    ap.add_argument(
        "--appsettings",
        default=str(default_appsettings),
        help="Path to appsettings.json (default: ../MyPhotoWebApi/appsettings.json)",
    )

    ap.add_argument("--uri", default=None, help="MongoDB URI (default: from appsettings)")
    ap.add_argument("--db", default=None, help="Database name (default: from appsettings)")
    ap.add_argument("--collection", default="photos", help="Collection name")
    ap.add_argument(
        "--root",
        default=None,
        help="Root folder for media files (default: from appsettings MyPhotoSettings:RootFolder)",
    )

    ap.add_argument(
        "--extensions",
        default=".mp4,.avi,.3gp",
        help="Comma-separated video extensions to treat as video files",
    )
    ap.add_argument("--limit", type=int, default=0, help="Process at most N docs (0 = no limit)")
    ap.add_argument("--batch-size", type=int, default=200, help="Mongo cursor batch size")
    ap.add_argument("--since-id", default=None, help="Resume: only process _id > this (ObjectId string)")

    ap.add_argument("--timestamp", type=float, default=1.0, help="Seek position in seconds")
    ap.add_argument("--long-side", type=int, default=240, help="Thumbnail long side (px)")
    ap.add_argument("--jpeg-quality", type=int, default=4, help="ffmpeg -q:v (2 best .. 31 worst)")
    ap.add_argument("--ffmpeg-timeout", type=int, default=30, help="ffmpeg timeout seconds")

    ap.add_argument("--execute", action="store_true", help="Actually update DB (default: dry-run)")

    ap.add_argument(
        "--log-dir",
        default=str(Path("scripts") / "logs"),
        help="Directory for log files (default: scripts/logs)",
    )
    ap.add_argument(
        "--progress-every",
        type=int,
        default=20,
        help="Print progress every N processed docs",
    )
    args = ap.parse_args()

    logger, log_path = _setup_logger(Path(args.log_dir), "backfill_video_thumbnails")

    appsettings_path = Path(args.appsettings)
    cfg_root, cfg_uri, cfg_db = (None, None, None)
    if appsettings_path.exists():
        cfg_root, cfg_uri, cfg_db = _read_defaults_from_appsettings(appsettings_path)

    # Merge defaults: CLI overrides config.
    uri = args.uri or cfg_uri or "mongodb://localhost:27017"
    db = args.db or cfg_db or "MyPhotos"
    root = Path(args.root or cfg_root or "/home/yww/FtpRoot/PicRootFolder")

    try:
        from bson import ObjectId
        from pymongo import MongoClient
    except Exception as e:
        logger.error("pymongo (and bson) required. Install with: pip install pymongo")
        logger.error("Details: %s", e)
        return 2

    mode = "EXECUTE" if args.execute else "DRY RUN"
    exts = {e.strip().lower() for e in args.extensions.split(",") if e.strip()}

    logger.info("[%s] start backfill video thumbnails", mode)
    logger.info("appsettings=%s (exists=%s)", str(appsettings_path), appsettings_path.exists())
    logger.info("mongo=%s db=%s col=%s", uri, db, args.collection)
    logger.info("root=%s exts=%s", str(root), sorted(exts))
    logger.info("log_file=%s", str(log_path))

    client = MongoClient(uri)
    col = client[db][args.collection]

    name_regex = "(" + "|".join([e.replace(".", "\\.") for e in sorted(exts)]) + ")$"

    q: Dict[str, Any] = {
        "$and": [
            {"$or": [{"mediaType": "video"}, {"fileName": {"$regex": name_regex, "$options": "i"}}]},
            {"$or": [{"thumbnail": {"$exists": False}}, {"thumbnail": None}]},
        ]
    }

    if args.since_id:
        q["$and"].append({"_id": {"$gt": ObjectId(args.since_id)}})

    cursor = (
        col.find(q, {"_id": 1, "path": 1, "fileName": 1, "mediaType": 1})
        .sort("_id", 1)
        .batch_size(args.batch_size)
    )

    processed = 0
    updated = 0
    skipped_missing_file = 0
    failed_ffmpeg = 0

    t0 = time.time()

    for doc in cursor:
        processed += 1
        doc_id = doc.get("_id")

        abs_path = _resolve_video_abs_path(root, doc)
        if abs_path is None:
            logger.warning("skip _id=%s: missing path/fileName", str(doc_id))
            continue

        if not abs_path.exists():
            skipped_missing_file += 1
            logger.warning("missing file _id=%s file=%s", str(doc_id), str(abs_path))
            continue

        suffix = abs_path.suffix.lower()
        if suffix not in exts:
            logger.info("skip _id=%s: ext=%s not in %s", str(doc_id), suffix, sorted(exts))
            continue

        tmp_dir = Path(args.log_dir) / "tmp"
        tmp_dir.mkdir(parents=True, exist_ok=True)
        tmp_out = tmp_dir / f"{str(doc_id)}.jpg"

        ok, msg = _run_ffmpeg_extract(
            video_path=abs_path,
            out_path=tmp_out,
            timestamp_sec=args.timestamp,
            long_side=args.long_side,
            jpeg_quality=args.jpeg_quality,
            timeout_sec=args.ffmpeg_timeout,
        )
        if not ok:
            failed_ffmpeg += 1
            logger.error("ffmpeg failed _id=%s file=%s err=%s", str(doc_id), str(abs_path), msg)
            if tmp_out.exists():
                tmp_out.unlink(missing_ok=True)
            continue

        if not tmp_out.exists():
            failed_ffmpeg += 1
            logger.error(
                "ffmpeg produced no output _id=%s file=%s out=%s",
                str(doc_id),
                str(abs_path),
                str(tmp_out),
            )
            continue

        thumb_bytes = tmp_out.read_bytes()
        tmp_out.unlink(missing_ok=True)

        if args.execute:
            res = col.update_one({"_id": doc_id}, {"$set": {"thumbnail": thumb_bytes}})
            if res.modified_count == 1:
                updated += 1
            else:
                logger.warning("update no-op _id=%s (maybe already updated)", str(doc_id))
        else:
            updated += 1

        if args.progress_every > 0 and processed % args.progress_every == 0:
            elapsed = time.time() - t0
            rate = processed / elapsed if elapsed > 0 else 0
            logger.info(
                "progress processed=%d updated=%d missingFile=%d ffmpegFail=%d rate=%.2f/s last_id=%s",
                processed,
                updated,
                skipped_missing_file,
                failed_ffmpeg,
                rate,
                str(doc_id),
            )

        if args.limit and processed >= args.limit:
            logger.info("limit reached: %d", args.limit)
            break

    elapsed = time.time() - t0
    logger.info("done mode=%s", mode)
    logger.info(
        "summary processed=%d updated=%d missingFile=%d ffmpegFail=%d elapsed=%.1fs",
        processed,
        updated,
        skipped_missing_file,
        failed_ffmpeg,
        elapsed,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
