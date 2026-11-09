
"""
Point-in-time research data loading.

Reads exclusively from an isolated SQLite research backup.

A snapshot becomes available only after its collection
batch has completed successfully.
"""

from __future__ import annotations

from datetime import datetime, timezone
from pathlib import Path

import pandas as pd

from .sqlite_reader import SQLiteMarketReader


TIMESTAMP_COLUMNS = (
    "InstantBuyTimeUtc",
    "InstantSellTimeUtc",
    "CollectedAtUtc",
    "CompletedAtUtc",
)


def load_price_history(
    database: str | Path,
    *,
    item_ids: list[int],
    start_utc: datetime,
    end_utc: datetime,
) -> pd.DataFrame:
    """
    Load completed-batch price snapshots.

    Includes observations preceding start_utc so that
    market state can be reconstructed at the beginning
    of the requested window.

    Returned rows are availability-filtered to end_utc.
    """

    if not item_ids:
        raise ValueError("At least one item ID is required")

    if (
        start_utc.tzinfo is None
        or end_utc.tzinfo is None
        or start_utc.utcoffset() is None
        or end_utc.utcoffset() is None
    ):
        raise ValueError("Timestamps must be timezone-aware")

    start_utc = start_utc.astimezone(timezone.utc)
    end_utc = end_utc.astimezone(timezone.utc)

    if start_utc >= end_utc:
        raise ValueError("start_utc must precede end_utc")

    reader = SQLiteMarketReader(database)
    reader.verify_schema()

    placeholders = ",".join("?" for _ in item_ids)

    query = f"""
        SELECT
            s.Id,
            s.MarketItemId,
            s.CollectionBatchId,
            s.InstantBuyPrice,
            s.InstantSellPrice,
            s.InstantBuyTimeUtc,
            s.InstantSellTimeUtc,
            s.CollectedAtUtc,
            b.CompletedAtUtc

        FROM MarketPriceSnapshots AS s

        INNER JOIN MarketCollectionBatches AS b
            ON b.Id = s.CollectionBatchId

        WHERE
            b.Status = 1
            AND b.CompletedAtUtc IS NOT NULL
            AND s.MarketItemId IN ({placeholders})

        ORDER BY
            s.MarketItemId,
            s.CollectedAtUtc,
            s.Id
    """

    with reader.connect() as connection:
        frame = pd.read_sql_query(
            query,
            connection,
            params=item_ids,
        )

    for column in TIMESTAMP_COLUMNS:
        frame[column] = (
            pd.to_datetime(
                frame[column],
                utc=True,
                errors="coerce",
            )
            .dt.as_unit("ns")
        )

    # Availability is governed by both collection
    # and successful batch completion.
    frame["AvailableAtUtc"] = frame[
        ["CollectedAtUtc", "CompletedAtUtc"]
    ].max(axis=1)

    frame = frame[
        frame["CollectedAtUtc"].notna()
        & frame["CompletedAtUtc"].notna()
        & (frame["AvailableAtUtc"] <= end_utc)
    ].copy()

    frame = frame.sort_values(
        ["MarketItemId", "AvailableAtUtc", "Id"]
    ).reset_index(drop=True)

    return frame
