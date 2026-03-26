#!/usr/bin/env python3
"""De-duplicate MyPhotos MongoDB `photos` collection and (optionally) create a unique index.

Why this exists:
- Historical ingest races could create duplicate documents with the same (path, fileName) but different _id.
- This script removes duplicates safely (dry-run by default) and can then enforce uniqueness via a MongoDB unique index.

Safety:
- Default mode is DRY RUN.
- Deletions require --execute.

Typical usage:
  python3 scripts/dedupe_photos_mongo.py --uri mongodb://localhost:27017 --db MyPhotos --execute --create-index

Note:
- If your dataset is huge, consider running with --path-prefix to scope the work.
"""

from __future__ import annotations

import argparse
import sys
from typing import Any, Dict, List, Optional, Tuple


def _pick_keep_doc(docs: List[Dict[str, Any]], keep: str) -> Dict[str, Any]:
    # Prefer a deterministic keep rule to avoid churn.
    if keep == "latest":
        # dateTaken can be missing; fall back to _id ordering.
        def key(d: Dict[str, Any]) -> Tuple[int, str]:
            dt = d.get("dateTaken")
            # Mongo returns datetime for BSON Date; if it isn't, treat as missing.
            ts = int(dt.timestamp()) if hasattr(dt, "timestamp") else -1
            oid = str(d.get("_id", ""))
            return (ts, oid)

        return max(docs, key=key)

    if keep == "oldest":
        def key(d: Dict[str, Any]) -> Tuple[int, str]:
            dt = d.get("dateTaken")
            ts = int(dt.timestamp()) if hasattr(dt, "timestamp") else 2**31 - 1
            oid = str(d.get("_id", ""))
            return (ts, oid)

        return min(docs, key=key)

    # Fallback: keep first (should not happen).
    return docs[0]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--uri", required=True, help="MongoDB URI, e.g. mongodb://localhost:27017")
    ap.add_argument("--db", required=True, help="Database name, e.g. MyPhotos")
    ap.add_argument("--collection", default="photos", help="Collection name (default: photos)")
    ap.add_argument("--path-prefix", default=None, help="Only process docs where path startswith this prefix")
    ap.add_argument("--keep", choices=["latest", "oldest"], default="latest", help="Which doc to keep per (path,fileName)")
    ap.add_argument("--batch-size", type=int, default=2000, help="Server cursor batch size")
    ap.add_argument("--execute", action="store_true", help="Actually delete duplicates (default: dry-run)")
    ap.add_argument("--create-index", action="store_true", help="Create unique index on (path,fileName) after dedupe")
    ap.add_argument("--index-name", default="uniq_path_fileName", help="Index name for unique index")
    args = ap.parse_args()

    try:
        from pymongo import MongoClient
        from pymongo.errors import BulkWriteError
    except Exception as e:
        print("ERROR: pymongo not installed. Install it with: pip install pymongo", file=sys.stderr)
        print(f"Details: {e}", file=sys.stderr)
        return 2

    client = MongoClient(args.uri)
    col = client[args.db][args.collection]

    match: Dict[str, Any] = {}
    if args.path_prefix is not None:
        match["path"] = {"$regex": f"^{args.path_prefix}"}

    # Group by (path, fileName), collect ids and basic fields used for keep selection.
    pipeline: List[Dict[str, Any]] = []
    if match:
        pipeline.append({"$match": match})

    pipeline.extend(
        [
            {
                "$group": {
                    "_id": {"path": "$path", "fileName": "$fileName"},
                    "count": {"$sum": 1},
                    "ids": {"$push": "$_id"},
                }
            },
            {"$match": {"count": {"$gt": 1}}},
        ]
    )

    dup_groups = list(col.aggregate(pipeline, allowDiskUse=True))
    total_groups = len(dup_groups)
    total_dups = sum(int(g.get("count", 0)) - 1 for g in dup_groups)

    mode = "EXECUTE" if args.execute else "DRY RUN"
    scope = f"path-prefix={args.path_prefix!r}" if args.path_prefix is not None else "all-paths"
    print(f"[{mode}] scanning {args.db}.{args.collection} ({scope})")
    print(f"duplicate groups: {total_groups}; duplicate docs to remove: {total_dups}")

    if total_groups == 0:
        if args.create_index:
            print("no duplicates found; creating unique index...")
            col.create_index([("path", 1), ("fileName", 1)], unique=True, name=args.index_name)
            print("unique index created.")
        return 0

    # Resolve keep/delete ids per group.
    delete_ids: List[Any] = []

    for i, g in enumerate(dup_groups, start=1):
        key = g.get("_id") or {}
        path = key.get("path")
        file_name = key.get("fileName")
        ids = g.get("ids") or []

        # Fetch full docs for this group to decide which to keep.
        docs = list(col.find({"_id": {"$in": ids}}, {"_id": 1, "path": 1, "fileName": 1, "dateTaken": 1}))
        if not docs:
            continue

        keep_doc = _pick_keep_doc(docs, args.keep)
        keep_id = keep_doc.get("_id")
        for d in docs:
            if d.get("_id") != keep_id:
                delete_ids.append(d.get("_id"))

        if i <= 5:
            print(f"example[{i}] ({path!r}, {file_name!r}) count={len(docs)} keep={str(keep_id)}")

    print(f"planned deletions: {len(delete_ids)}")

    if not args.execute:
        print("dry-run only; re-run with --execute to apply deletions")
        if args.create_index:
            print("NOTE: unique index creation skipped in dry-run.")
        return 0

    # Apply deletions in chunks to avoid oversized commands.
    chunk = 5000
    deleted = 0
    for off in range(0, len(delete_ids), chunk):
        part = delete_ids[off : off + chunk]
        res = col.delete_many({"_id": {"$in": part}})
        deleted += int(res.deleted_count)
        print(f"deleted {deleted}/{len(delete_ids)}")

    if args.create_index:
        print("creating unique index on (path,fileName)...")
        col.create_index([("path", 1), ("fileName", 1)], unique=True, name=args.index_name)
        print("unique index created.")

    print("done")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
