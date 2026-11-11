
"""
Phase 6A - Independent research dataset verification.

Verifies an exported single-item dataset against
its original frozen SQLite source.

Does not modify either the source or the dataset.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import sqlite3
from pathlib import Path


QUERIES = {
    "snapshots.csv": """
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
    "aggregates_5m.csv": """
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
          AND Interval = 5
        ORDER BY BucketStartUtc, Id
    """,
    "aggregates_1h.csv": """
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
          AND Interval = 60
        ORDER BY BucketStartUtc, Id
    """,
}


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()

    with path.open("rb") as stream:
        for chunk in iter(
            lambda: stream.read(1024 * 1024),
            b"",
        ):
            digest.update(chunk)

    return digest.hexdigest()


def normalize_sql_value(value: object) -> str:
    # Matches the existing csv.writer exporter:
    # SQL NULL becomes an empty CSV field.
    if value is None:
        return ""

    return str(value)


def compare_export(
    connection: sqlite3.Connection,
    csv_path: Path,
    query: str,
    item_id: int,
) -> int:
    """
    Stream the SQL and CSV results together.
    This avoids loading both datasets into memory.
    """

    cursor = connection.execute(query, (item_id,))

    expected_header = [
        column[0] for column in cursor.description
    ]

    count = 0

    with csv_path.open(
        "r",
        newline="",
        encoding="utf-8",
    ) as stream:

        reader = csv.reader(stream)

        header = next(reader, None)

        if header != expected_header:
            raise ValueError(
                f"{csv_path.name}: column mismatch.\n"
                f"Expected: {expected_header}\n"
                f"Actual: {header}"
            )

        for sql_row in cursor:
            csv_row = next(reader, None)

            if csv_row is None:
                raise ValueError(
                    f"{csv_path.name}: missing row "
                    f"at position {count + 1}"
                )

            expected_row = [
                normalize_sql_value(value)
                for value in sql_row
            ]

            if csv_row != expected_row:
                raise ValueError(
                    f"{csv_path.name}: row mismatch "
                    f"at position {count + 1}\n"
                    f"Expected: {expected_row}\n"
                    f"Actual: {csv_row}"
                )

            count += 1

        if next(reader, None) is not None:
            raise ValueError(
                f"{csv_path.name}: unexpected extra rows"
            )

    return count


def verify_dataset(
    database: Path,
    dataset: Path,
) -> None:
    manifest_path = dataset / "manifest.json"

    manifest = json.loads(
        manifest_path.read_text(encoding="utf-8")
    )

    if manifest["dataset_version"] != "research-raw-v1":
        raise ValueError("Unsupported dataset version")

    item_id = int(manifest["item_id"])

    expected_hash = manifest["source_sha256"].lower()
    actual_hash = sha256_file(database).lower()

    if expected_hash != actual_hash:
        raise ValueError("Source database SHA-256 mismatch")

    if database.stat().st_size != manifest["source_size_bytes"]:
        raise ValueError("Source database size mismatch")

    print(f"Source verified: {actual_hash}")
    print(f"Item: {item_id}")

    # This connection mode is ONLY for frozen,
    # complete SQLite backup/archive files.
    uri = database.resolve().as_uri() + (
        "?mode=ro&immutable=1"
    )

    with sqlite3.connect(uri, uri=True) as connection:

        integrity = connection.execute(
            "PRAGMA integrity_check"
        ).fetchall()

        if integrity != [("ok",)]:
            raise ValueError(
                f"SQLite integrity failure: {integrity}"
            )

        fk_violations = connection.execute(
            "PRAGMA foreign_key_check"
        ).fetchall()

        if fk_violations:
            raise ValueError(
                f"Foreign key violations: {fk_violations[:5]}"
            )

        for filename, query in QUERIES.items():
            csv_path = dataset / filename

            expected_csv_hash = (
                manifest["files"][filename].lower()
            )

            actual_csv_hash = sha256_file(
                csv_path
            ).lower()

            if expected_csv_hash != actual_csv_hash:
                raise ValueError(
                    f"{filename}: SHA-256 mismatch"
                )

            count = compare_export(
                connection,
                csv_path,
                query,
                item_id,
            )

            count_key = (
                "snapshots"
                if filename == "snapshots.csv"
                else filename
            )

            if count != manifest["row_counts"][count_key]:
                raise ValueError(
                    f"{filename}: row count mismatch"
                )

            print(f"PASS {filename}: {count:,} rows")

    # Detect changes to the source during comparison.
    if sha256_file(database).lower() != expected_hash:
        raise ValueError(
            "Source changed during verification"
        )

    print("\nPHASE 6A DATASET VERIFICATION PASSED")


def main() -> None:
    parser = argparse.ArgumentParser()

    parser.add_argument(
        "--database",
        type=Path,
        required=True,
    )

    parser.add_argument(
        "--dataset",
        type=Path,
        required=True,
    )

    args = parser.parse_args()

    verify_dataset(
        args.database,
        args.dataset,
    )


if __name__ == "__main__":
    main()
