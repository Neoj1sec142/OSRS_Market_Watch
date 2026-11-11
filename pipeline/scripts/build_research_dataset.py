
"""
Phase 6A - Single-source research dataset extraction.

Input must be a frozen, standalone SQLite backup/archive.
Never point this script at the live collector database.

Outputs:
    snapshots.csv
    aggregates_5m.csv
    aggregates_1h.csv
    manifest.json

No database writes. No source mutation. No overwrites.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import sqlite3
from pathlib import Path


DATASET_VERSION = "research-raw-v1"

SNAPSHOT_COLUMNS = (
    "Id", "MarketItemId", "CollectionBatchId",
    "InstantBuyPrice", "InstantSellPrice",
    "InstantBuyTimeUtc", "InstantSellTimeUtc",
    "CollectedAtUtc",
)

AGGREGATE_COLUMNS = (
    "Id", "MarketItemId", "Interval", "BucketStartUtc",
    "AverageInstantBuyPrice",
    "AverageInstantSellPrice",
    "InstantBuyVolume", "InstantSellVolume",
    "CollectedAtUtc",
)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()

    with path.open("rb") as stream:
        for chunk in iter(
            lambda: stream.read(1024 * 1024),
            b"",
        ):
            digest.update(chunk)

    return digest.hexdigest()


def open_frozen_database(path: Path) -> sqlite3.Connection:
    if not path.is_file():
        raise FileNotFoundError(path)

    # Only safe for a completed, immutable artifact.
    uri = path.resolve().as_uri() + "?mode=ro&immutable=1"

    connection = sqlite3.connect(uri, uri=True)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA query_only = ON")

    return connection


def verify_database(connection: sqlite3.Connection) -> None:
    result = connection.execute(
        "PRAGMA integrity_check"
    ).fetchall()

    if len(result) != 1 or result[0][0] != "ok":
        raise RuntimeError(
            f"SQLite integrity check failed: {result}"
        )

    foreign_keys = connection.execute(
        "PRAGMA foreign_key_check"
    ).fetchall()

    if foreign_keys:
        raise RuntimeError(
            f"Foreign key violations: {foreign_keys[:5]}"
        )


def require_columns(
    connection: sqlite3.Connection,
    table: str,
    required: tuple[str, ...],
) -> None:
    existing = {
        row["name"]
        for row in connection.execute(
            f'PRAGMA table_info("{table}")'
        )
    }

    missing = set(required) - existing

    if missing:
        raise RuntimeError(
            f"{table} missing columns: {sorted(missing)}"
        )


def export_query(
    connection: sqlite3.Connection,
    query: str,
    parameters: tuple,
    destination: Path,
) -> int:
    count = 0

    with destination.open(
        "w",
        newline="",
        encoding="utf-8",
    ) as stream:

        cursor = connection.execute(query, parameters)

        writer = csv.writer(
            stream,
            lineterminator="\n",
        )

        writer.writerow(
            [column[0] for column in cursor.description]
        )

        for row in cursor:
            writer.writerow(tuple(row))
            count += 1

    return count


def build_dataset(
    database: Path,
    output: Path,
    item_id: int,
    expected_sha256: str,
    source_label: str,
) -> None:

    database = database.resolve()

    if output.exists():
        raise FileExistsError(
            f"Output already exists: {output}"
        )

    actual_sha256 = sha256_file(database)

    if actual_sha256.lower() != expected_sha256.lower():
        raise RuntimeError(
            "Source SHA-256 mismatch.\n"
            f"Expected: {expected_sha256}\n"
            f"Actual:   {actual_sha256}"
        )

    # Create a fresh output directory.
    # Never modify or overwrite existing datasets.
    output.mkdir(parents=True, exist_ok=False)

    try:
        with open_frozen_database(database) as connection:
            verify_database(connection)

            require_columns(
                connection,
                "MarketPriceSnapshots",
                SNAPSHOT_COLUMNS,
            )

            require_columns(
                connection,
                "MarketPriceAggregates",
                AGGREGATE_COLUMNS,
            )

            require_columns(
                connection,
                "MarketCollectionBatches",
                ("Id", "Status", "CompletedAtUtc"),
            )

            snapshots = export_query(
                connection,
                """
                SELECT
                    s.Id,
                    s.MarketItemId,
                    s.CollectionBatchId,
                    s.InstantBuyPrice,
                    s.InstantSellPrice,
                    s.InstantBuyTimeUtc,
                    s.InstantSellTimeUtc,
                    s.CollectedAtUtc,
                    b.CompletedAtUtc AS BatchCompletedAtUtc
                FROM MarketPriceSnapshots AS s
                JOIN MarketCollectionBatches AS b
                    ON b.Id = s.CollectionBatchId
                WHERE s.MarketItemId = ?
                  AND b.Status = 1
                  AND b.CompletedAtUtc IS NOT NULL
                ORDER BY s.CollectedAtUtc, s.Id
                """,
                (item_id,),
                output / "snapshots.csv",
            )

            aggregate_counts = {}

            for interval, filename in (
                (5, "aggregates_5m.csv"),
                (60, "aggregates_1h.csv"),
            ):
                count = export_query(
                    connection,
                    """
                    SELECT
                        Id,
                        MarketItemId,
                        Interval,
                        BucketStartUtc,
                        AverageInstantBuyPrice,
                        AverageInstantSellPrice,
                        InstantBuyVolume,
                        InstantSellVolume,
                        CollectedAtUtc
                    FROM MarketPriceAggregates
                    WHERE MarketItemId = ?
                      AND Interval = ?
                    ORDER BY BucketStartUtc, Id
                    """,
                    (item_id, interval),
                    output / filename,
                )

                aggregate_counts[filename] = count

        # Detect unexpected source changes during extraction.
        if sha256_file(database) != actual_sha256:
            raise RuntimeError(
                "Source changed during extraction."
            )

        output_files = (
            "snapshots.csv",
            "aggregates_5m.csv",
            "aggregates_1h.csv",
        )

        manifest = {
            "dataset_version": DATASET_VERSION,
            "item_id": item_id,
            "source_label": source_label,
            "source_sha256": actual_sha256,
            "source_size_bytes": database.stat().st_size,
            "row_counts": {
                "snapshots": snapshots,
                **aggregate_counts,
            },
            "files": {
                name: sha256_file(output / name)
                for name in output_files
            },
            "semantics": {
                "snapshots": (
                    "Completed batches only; "
                    "raw collection and quote timestamps"
                ),
                "aggregates": (
                    "Latest persisted bucket values; "
                    "not point-in-time revision history"
                ),
                "timestamp_format": (
                    "Original SQLite UTC text preserved"
                ),
                "aggregation_policy": (
                    "5m and 1h retained separately"
                ),
            },
        }

        manifest_path = output / "manifest.json"

        manifest_path.write_text(
            json.dumps(
                manifest,
                indent=2,
                sort_keys=True,
            ) + "\n",
            encoding="utf-8",
        )

        print("\nResearch dataset created")
        print(f"Item: {item_id}")
        print(f"Snapshots: {snapshots}")
        print(
            "5m aggregates:",
            aggregate_counts["aggregates_5m.csv"],
        )
        print(
            "1h aggregates:",
            aggregate_counts["aggregates_1h.csv"],
        )
        print(f"Output: {output}")

    except Exception:
        # Leave any partial outputs for inspection.
        # Never touch the input database.
        print(
            "Extraction failed. Output may be incomplete; "
            "do not use it as a verified dataset."
        )
        raise


def main() -> None:
    parser = argparse.ArgumentParser()

    parser.add_argument(
        "--database", type=Path, required=True
    )
    parser.add_argument(
        "--output", type=Path, required=True
    )
    parser.add_argument(
        "--item-id", type=int, default=4151
    )
    parser.add_argument(
        "--expected-sha256", required=True
    )
    parser.add_argument(
        "--source-label", required=True
    )

    args = parser.parse_args()

    build_dataset(
        database=args.database,
        output=args.output,
        item_id=args.item_id,
        expected_sha256=args.expected_sha256,
        source_label=args.source_label,
    )


if __name__ == "__main__":
    main()
