"""EF Core SQLite read adapter: all queries are read-only and cutoff constrained.

Important: aggregate UPSERTs can erase earlier revisions; current rows are NOT
necessarily what a historical strategy saw at the time. Use archived exports
for authentic point-in-time aggregate research.
"""
from __future__ import annotations

from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
from decimal import Decimal
from pathlib import Path
import sqlite3
from typing import Iterator

from .contracts import (
    AggregateInterval, CollectionStatus, MarketCollectionBatch,
    MarketItem, MarketPriceAggregate, MarketPriceSnapshot, MarketState,
    require_utc,
)


def parse_ef_datetime(value: str | datetime | None) -> datetime | None:
    """Read typical EF SQLite TEXT DateTime, treated as UTC by the app.

    EF DateTime storage is provider/configuration-dependent; verify against
    typeof() and actual values from the deployed database.
    """
    if value is None:
        return None
    if isinstance(value, datetime):
        dt = value
    else:
        dt = datetime.fromisoformat(value.strip().replace("Z", "+00:00"))
    if dt.tzinfo is None:
        return dt.replace(tzinfo=timezone.utc)
    if dt.utcoffset() != timedelta(0):
        return dt.astimezone(timezone.utc)
    return dt


def _db_stamp(value: datetime) -> str:
    require_utc(value, "cutoff")
    # EF SQLite DateTime is ordinarily TEXT and ordered lexically. Avoid
    # reliance on text formatting differences by filtering in Python for
    # correctness; time-indexed SQL predicates can be enabled after verifying
    # the provider's exact physical encoding.
    return value.isoformat()


class SQLiteMarketReader:
    def __init__(self, database: str | Path):
        self.database = Path(database).expanduser().resolve(strict=True)
        if not self.database.is_file():
            raise ValueError("database must be a file")

    @contextmanager
    def connect(self) -> Iterator[sqlite3.Connection]:
        # URI mode=ro supports WAL without ever opening a writer connection.
        # Do NOT use immutable=1 with an actively written WAL database.
        db_uri = self.database.as_uri() + "?mode=ro"
        connection = sqlite3.connect(db_uri, uri=True, timeout=5)
        connection.row_factory = sqlite3.Row
        try:
            connection.execute("PRAGMA query_only=ON")
            yield connection
        finally:
            connection.close()

    def verify_schema(self) -> dict[str, list[str]]:
        required = {
            "MarketItems": {"Id", "Name", "BuyLimit", "IsActive", "IsTaxExempt", "LastMetadataUpdateUtc"},
            "MarketCollectionBatches": {"Id", "StartedAtUtc", "CompletedAtUtc", "Status"},
            "MarketPriceSnapshots": {"Id", "MarketItemId", "CollectionBatchId", "InstantBuyPrice", "InstantSellPrice", "InstantBuyTimeUtc", "InstantSellTimeUtc", "CollectedAtUtc"},
            "MarketPriceAggregates": {"Id", "MarketItemId", "Interval", "BucketStartUtc", "CollectedAtUtc", "InstantBuyVolume", "InstantSellVolume"},
        }
        actual = {}
        with self.connect() as db:
            for table, needed in required.items():
                rows = db.execute(f'PRAGMA table_info("{table}")').fetchall()
                cols = [r[1] for r in rows]
                if not needed.issubset(cols):
                    raise ValueError(f"{table}: missing columns {sorted(needed - set(cols))}")
                actual[table] = cols
        return actual

    def items(self) -> list[MarketItem]:
        with self.connect() as db:
            rows = db.execute('''SELECT Id, Name, Members, BuyLimit, HighAlchValue, LowAlchValue,
                                 IsActive, IsTaxExempt, LastMetadataUpdateUtc, Examine, Icon
                          FROM MarketItems ORDER BY Id''').fetchall()
        return [MarketItem(
            id=r["Id"], name=r["Name"], members=bool(r["Members"]),
            buy_limit=r["BuyLimit"], high_alch_value=r["HighAlchValue"],
            low_alch_value=r["LowAlchValue"], is_active=bool(r["IsActive"]),
            is_tax_exempt=bool(r["IsTaxExempt"]),
            last_metadata_update_utc=parse_ef_datetime(r["LastMetadataUpdateUtc"]),
            examine=r["Examine"], icon=r["Icon"],
        ) for r in rows]

    def snapshots(self, cutoff: datetime, item_ids: set[int] | None = None) -> list[MarketPriceSnapshot]:
        """Return completed-batch snapshots known by cutoff.

        SQL uses broad batch-ID or unrestricted scan to avoid assuming the EF
        TEXT datetime serialization; timestamps are filtered precisely in Python.
        For production-scale history, export to a static research DB first.
        """
        require_utc(cutoff, "cutoff")
        if item_ids is not None and not item_ids:
            return []
        with self.connect() as db:
            rows = db.execute('''SELECT s.Id, s.MarketItemId, s.CollectionBatchId,
                         s.InstantBuyPrice, s.InstantSellPrice,
                         s.InstantBuyTimeUtc, s.InstantSellTimeUtc, s.CollectedAtUtc,
                         b.CompletedAtUtc, b.Status
                  FROM MarketPriceSnapshots s
                  JOIN MarketCollectionBatches b ON b.Id = s.CollectionBatchId
                  ORDER BY s.MarketItemId, s.CollectedAtUtc, s.Id''').fetchall()
        result = []
        for r in rows:
            if r["Status"] != int(CollectionStatus.COMPLETED):
                continue
            collected = parse_ef_datetime(r["CollectedAtUtc"])
            completed = parse_ef_datetime(r["CompletedAtUtc"])
            if collected > cutoff or completed is None or completed > cutoff:
                continue
            if item_ids is not None and r["MarketItemId"] not in item_ids:
                continue
            result.append(MarketPriceSnapshot(
                id=r["Id"], market_item_id=r["MarketItemId"],
                collection_batch_id=r["CollectionBatchId"],
                instant_buy_price=r["InstantBuyPrice"], instant_sell_price=r["InstantSellPrice"],
                instant_buy_time_utc=parse_ef_datetime(r["InstantBuyTimeUtc"]),
                instant_sell_time_utc=parse_ef_datetime(r["InstantSellTimeUtc"]),
                collected_at_utc=collected,
            ))
        return result

    def market_states(self, as_of: datetime, item_ids: set[int] | None = None) -> list[MarketState]:
        """Latest completed, eligible whole snapshot per item (no side merging)."""
        snapshots = self.snapshots(as_of, item_ids)
        latest = {}
        for s in snapshots:
            key = (s.collected_at_utc, s.id)
            prior = latest.get(s.market_item_id)
            if prior is None or key > (prior.collected_at_utc, prior.id):
                latest[s.market_item_id] = s
        states = []
        for item_id, s in sorted(latest.items()):
            # Retain data quality issues instead of applying future timestamps.
            # Values from invalid future-dated source observations are masked.
            buy_time = s.instant_buy_time_utc
            sell_time = s.instant_sell_time_utc
            buy_ok = buy_time is None or buy_time <= as_of
            sell_ok = sell_time is None or sell_time <= as_of
            states.append(MarketState(
                market_item_id=item_id, as_of_utc=as_of,
                source_snapshot_id=s.id, source_collected_at_utc=s.collected_at_utc,
                instant_buy_price=s.instant_buy_price if buy_ok else None,
                instant_sell_price=s.instant_sell_price if sell_ok else None,
                instant_buy_time_utc=buy_time if buy_ok else None,
                instant_sell_time_utc=sell_time if sell_ok else None,
            ))
        return states

    def aggregates_for_research(self, cutoff: datetime, interval: AggregateInterval) -> list[MarketPriceAggregate]:
        """Current aggregate rows available by cutoff; NOT a historical revision log.

        For backtests before latest UPSERT, reject this source or use an archived
        point-in-time export recorded at the time of collection.
        """
        require_utc(cutoff, "cutoff")
        with self.connect() as db:
            rows = db.execute('''SELECT Id, MarketItemId, Interval, BucketStartUtc,
                         AverageInstantBuyPrice, AverageInstantSellPrice,
                         InstantBuyVolume, InstantSellVolume, CollectedAtUtc
                  FROM MarketPriceAggregates WHERE Interval = ?
                  ORDER BY MarketItemId, BucketStartUtc''', (int(interval),)).fetchall()
        result = []
        for r in rows:
            start = parse_ef_datetime(r["BucketStartUtc"])
            collected = parse_ef_datetime(r["CollectedAtUtc"])
            if collected > cutoff or start + timedelta(minutes=int(interval)) > cutoff:
                continue
            result.append(MarketPriceAggregate(
                id=r["Id"], market_item_id=r["MarketItemId"], interval=interval,
                bucket_start_utc=start,
                average_instant_buy_price=Decimal(str(r["AverageInstantBuyPrice"])) if r["AverageInstantBuyPrice"] is not None else None,
                average_instant_sell_price=Decimal(str(r["AverageInstantSellPrice"])) if r["AverageInstantSellPrice"] is not None else None,
                instant_buy_volume=r["InstantBuyVolume"], instant_sell_volume=r["InstantSellVolume"],
                collected_at_utc=collected,
            ))
        return result
