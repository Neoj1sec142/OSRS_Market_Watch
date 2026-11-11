
"""
Phase 6A - Canonical single-source research loader.

Consumes the research-raw-v1 CSV export.

Security and integrity:
- Verifies manifest schema and CSV hashes.
- Rejects unexpected CSV columns and malformed rows.
- Checks recorded row counts.
- Preserves source IDs and original SQLite time text.
- Never opens or modifies the production database.

Full backup/archive source verification remains a separate gate.
"""

from __future__ import annotations

import csv
import hashlib
import json
import re

from dataclasses import dataclass
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation
from pathlib import Path

from .research_contracts import (
    MarketAggregateV1,
    MarketObservationV1,
    ResearchSource,
)


DATASET_VERSION = "research-raw-v1"

SNAPSHOT_COLUMNS = (
    "Id",
    "MarketItemId",
    "CollectionBatchId",
    "InstantBuyPrice",
    "InstantSellPrice",
    "InstantBuyTimeUtc",
    "InstantSellTimeUtc",
    "CollectedAtUtc",
    "BatchCompletedAtUtc",
)

AGGREGATE_COLUMNS = (
    "Id",
    "MarketItemId",
    "Interval",
    "BucketStartUtc",
    "AverageInstantBuyPrice",
    "AverageInstantSellPrice",
    "InstantBuyVolume",
    "InstantSellVolume",
    "CollectedAtUtc",
)

FILES = {
    "snapshots.csv": SNAPSHOT_COLUMNS,
    "aggregates_5m.csv": AGGREGATE_COLUMNS,
    "aggregates_1h.csv": AGGREGATE_COLUMNS,
}

HASH_PATTERN = re.compile(r"^[0-9a-fA-F]{64}$")

# Matches the UTC format produced by .NET SQLite,
# including optional fractional seconds up to 7 digits.
TIME_PATTERN = re.compile(
    r"^\d{4}-\d{2}-\d{2}[ T]"
    r"\d{2}:\d{2}:\d{2}"
    r"(?:\.\d{1,7})?"
    r"(?:Z|\+00:00)?$"
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


def parse_utc(
    value: str,
    *,
    nullable: bool = False,
) -> datetime | None:
    if value == "" and nullable:
        return None

    if not TIME_PATTERN.fullmatch(value):
        raise ValueError(
            f"Invalid SQLite UTC timestamp: {value!r}"
        )

    # Python datetime stores microseconds. The source
    # SQLite text is retained separately for fidelity.
    result = datetime.fromisoformat(
        value.replace("Z", "+00:00")
    )

    if result.tzinfo is None:
        result = result.replace(tzinfo=timezone.utc)
    else:
        result = result.astimezone(timezone.utc)

    return result


def nullable_int(value: str) -> int | None:
    return None if value == "" else int(value)


def nullable_decimal(value: str) -> Decimal | None:
    if value == "":
        return None

    try:
        result = Decimal(value)
    except InvalidOperation as exc:
        raise ValueError(
            f"Invalid decimal value: {value!r}"
        ) from exc

    if not result.is_finite():
        raise ValueError("Non-finite decimal value")

    return result


@dataclass(frozen=True, slots=True)
class CanonicalDataset:
    source: ResearchSource
    dataset_sha256: str
    item_id: int

    observations: tuple[MarketObservationV1, ...]
    aggregates_5m: tuple[MarketAggregateV1, ...]
    aggregates_1h: tuple[MarketAggregateV1, ...]


def read_csv(
    path: Path,
    expected_columns: tuple[str, ...],
    expected_hash: str,
    expected_count: int,
) -> list[dict[str, str]]:

    actual_hash = sha256_file(path)

    if actual_hash != expected_hash.lower():
        raise ValueError(
            f"{path.name}: SHA-256 mismatch"
        )

    with path.open(
        "r",
        newline="",
        encoding="utf-8",
    ) as stream:

        reader = csv.DictReader(stream)

        if tuple(reader.fieldnames or ()) != expected_columns:
            raise ValueError(
                f"{path.name}: CSV schema mismatch"
            )

        rows = []

        for row in reader:
            if None in row or any(
                value is None for value in row.values()
            ):
                raise ValueError(
                    f"{path.name}: malformed CSV row"
                )

            rows.append(row)

    if len(rows) != expected_count:
        raise ValueError(
            f"{path.name}: expected {expected_count} rows, "
            f"found {len(rows)}"
        )

    return rows


def load_canonical_dataset(
    dataset_dir: str | Path,
) -> CanonicalDataset:

    root = Path(dataset_dir).resolve()

    manifest_path = root / "manifest.json"

    manifest_bytes = manifest_path.read_bytes()
    manifest = json.loads(manifest_bytes)

    if manifest.get("dataset_version") != DATASET_VERSION:
        raise ValueError("Unsupported dataset version")

    if not isinstance(manifest.get("item_id"), int):
        raise ValueError("Invalid manifest item ID")

    item_id = manifest["item_id"]

    if item_id <= 0:
        raise ValueError("Invalid item ID")

    source_sha = manifest["source_sha256"]

    if not HASH_PATTERN.fullmatch(source_sha):
        raise ValueError("Invalid source SHA-256")

    source = ResearchSource(
        artifact_sha256=source_sha.lower(),
        source_label=manifest["source_label"],
    )

    # This fingerprints the exact manifest bytes.
    # It is a dataset identity, not the source DB hash.
    dataset_sha = hashlib.sha256(
        manifest_bytes
    ).hexdigest()

    expected_files = set(FILES)

    if set(manifest["files"]) != expected_files:
        raise ValueError("Unexpected manifest file set")

    counts = manifest["row_counts"]

    rows_by_file = {}

    for filename, columns in FILES.items():
        count_key = (
            "snapshots"
            if filename == "snapshots.csv"
            else filename
        )

        expected_hash = manifest["files"][filename]

        if not HASH_PATTERN.fullmatch(expected_hash):
            raise ValueError("Invalid CSV SHA-256")

        rows_by_file[filename] = read_csv(
            root / filename,
            columns,
            expected_hash,
            counts[count_key],
        )

    observations = []

    for row in rows_by_file["snapshots.csv"]:

        if int(row["MarketItemId"]) != item_id:
            raise ValueError("Snapshot item mismatch")

        observations.append(
            MarketObservationV1(
                source=source,
                source_row_id=int(row["Id"]),
                item_id=item_id,
                collection_batch_id=int(
                    row["CollectionBatchId"]
                ),
                instant_buy_price_gp=nullable_int(
                    row["InstantBuyPrice"]
                ),
                instant_sell_price_gp=nullable_int(
                    row["InstantSellPrice"]
                ),
                instant_buy_time_utc=parse_utc(
                    row["InstantBuyTimeUtc"],
                    nullable=True,
                ),
                instant_sell_time_utc=parse_utc(
                    row["InstantSellTimeUtc"],
                    nullable=True,
                ),
                collected_at_utc=parse_utc(
                    row["CollectedAtUtc"]
                ),
                batch_completed_at_utc=parse_utc(
                    row["BatchCompletedAtUtc"]
                ),
                raw_collected_at_utc=row["CollectedAtUtc"],
            )
        )

    def build_aggregates(
        filename: str,
        expected_interval: int,
    ) -> tuple[MarketAggregateV1, ...]:

        result = []

        for row in rows_by_file[filename]:

            if int(row["MarketItemId"]) != item_id:
                raise ValueError("Aggregate item mismatch")

            interval = int(row["Interval"])

            if interval != expected_interval:
                raise ValueError(
                    f"{filename}: incorrect interval"
                )

            result.append(
                MarketAggregateV1(
                    source=source,
                    source_row_id=int(row["Id"]),
                    item_id=item_id,
                    interval_minutes=interval,
                    bucket_start_utc=parse_utc(
                        row["BucketStartUtc"]
                    ),
                    collected_at_utc=parse_utc(
                        row["CollectedAtUtc"]
                    ),
                    avg_instant_buy_gp=nullable_decimal(
                        row["AverageInstantBuyPrice"]
                    ),
                    avg_instant_sell_gp=nullable_decimal(
                        row["AverageInstantSellPrice"]
                    ),
                    instant_buy_volume=int(
                        row["InstantBuyVolume"]
                    ),
                    instant_sell_volume=int(
                        row["InstantSellVolume"]
                    ),
                    raw_bucket_start_utc=row["BucketStartUtc"],
                )
            )

        return tuple(result)

    return CanonicalDataset(
        source=source,
        dataset_sha256=dataset_sha,
        item_id=item_id,
        observations=tuple(observations),
        aggregates_5m=build_aggregates(
            "aggregates_5m.csv", 5
        ),
        aggregates_1h=build_aggregates(
            "aggregates_1h.csv", 60
        ),
    )
