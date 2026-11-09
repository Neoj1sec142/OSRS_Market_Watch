"""Version 1 normalized market persistence and research contracts.

Market observations are not executable order-book quotes.
All contract datetimes must be timezone-aware UTC.
"""
from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from decimal import Decimal
from enum import IntEnum
from uuid import UUID


class AggregateInterval(IntEnum):
    FIVE_MINUTES = 5
    ONE_HOUR = 60


class CollectionStatus(IntEnum):
    RUNNING = 0
    COMPLETED = 1
    FAILED = 2
    PARTIAL = 3


class ExecutionSource(IntEnum):
    MANUAL = 0
    IMPORTED = 1
    SIMULATED = 2


class SignalAction(IntEnum):
    BUY = 1
    SELL = 2
    HOLD = 3
    AVOID = 4


class SignalStatus(IntEnum):
    ACTIVE = 0
    EXPIRED = 1
    DISMISSED = 2
    EXECUTED = 3


class TradeMode(IntEnum):
    PAPER = 0
    LIVE = 1


class TradePositionStatus(IntEnum):
    OPEN = 0
    PARTIALLY_CLOSED = 1
    CLOSED = 2
    CANCELLED = 3


class TradeSide(IntEnum):
    BUY = 1
    SELL = 2


def require_utc(value: datetime | None, name: str) -> None:
    if value is None:
        return
    if value.tzinfo is None or value.utcoffset() != timedelta(0):
        raise ValueError(f"{name} must be an aware UTC datetime")


@dataclass(frozen=True, slots=True)
class MarketItem:
    id: int
    name: str
    members: bool
    buy_limit: int | None
    high_alch_value: int | None
    low_alch_value: int | None
    is_active: bool
    is_tax_exempt: bool
    last_metadata_update_utc: datetime
    examine: str | None = None
    icon: str | None = None

    def __post_init__(self) -> None:
        require_utc(self.last_metadata_update_utc, "last_metadata_update_utc")
        if self.id <= 0:
            raise ValueError("id must be positive")
        if self.buy_limit is not None and self.buy_limit < 0:
            raise ValueError("buy_limit cannot be negative")


@dataclass(frozen=True, slots=True)
class MarketCollectionBatch:
    id: int
    started_at_utc: datetime
    completed_at_utc: datetime | None
    source: str
    status: CollectionStatus
    items_collected: int
    error_message: str | None

    def __post_init__(self) -> None:
        require_utc(self.started_at_utc, "started_at_utc")
        require_utc(self.completed_at_utc, "completed_at_utc")


@dataclass(frozen=True, slots=True)
class MarketPriceSnapshot:
    id: int
    market_item_id: int
    collection_batch_id: int
    instant_buy_price: int | None
    instant_sell_price: int | None
    instant_buy_time_utc: datetime | None
    instant_sell_time_utc: datetime | None
    collected_at_utc: datetime

    def __post_init__(self) -> None:
        for name in ("instant_buy_time_utc", "instant_sell_time_utc", "collected_at_utc"):
            require_utc(getattr(self, name), name)
        for name in ("instant_buy_price", "instant_sell_price"):
            value = getattr(self, name)
            if value is not None and value <= 0:
                raise ValueError(f"{name} must be positive when provided")


@dataclass(frozen=True, slots=True)
class MarketPriceAggregate:
    id: int
    market_item_id: int
    interval: AggregateInterval
    bucket_start_utc: datetime
    average_instant_buy_price: Decimal | None
    average_instant_sell_price: Decimal | None
    instant_buy_volume: int
    instant_sell_volume: int
    collected_at_utc: datetime

    def __post_init__(self) -> None:
        require_utc(self.bucket_start_utc, "bucket_start_utc")
        require_utc(self.collected_at_utc, "collected_at_utc")
        if min(self.instant_buy_volume, self.instant_sell_volume) < 0:
            raise ValueError("volumes cannot be negative")

    @property
    def bucket_end_utc(self) -> datetime:
        return self.bucket_start_utc + timedelta(minutes=int(self.interval))


@dataclass(frozen=True, slots=True)
class MarketState:
    """Known market state at a prediction cutoff (not a guaranteed executable quote).

    Data access must ensure snapshot.collected_at_utc <= as_of_utc, and
    select the latest eligible snapshot per item (with explicit side semantics).
    """
    market_item_id: int
    as_of_utc: datetime
    source_snapshot_id: int
    source_collected_at_utc: datetime
    instant_buy_price: int | None
    instant_sell_price: int | None
    instant_buy_time_utc: datetime | None
    instant_sell_time_utc: datetime | None

    def __post_init__(self) -> None:
        for name in ("as_of_utc", "source_collected_at_utc", "instant_buy_time_utc", "instant_sell_time_utc"):
            require_utc(getattr(self, name), name)
        if self.source_collected_at_utc > self.as_of_utc:
            raise ValueError("source data was not yet available at prediction time")
        for name in ("instant_buy_time_utc", "instant_sell_time_utc"):
            value = getattr(self, name)
            if value is not None and value > self.as_of_utc:
                raise ValueError(f"{name} is after prediction time")

    @property
    def buy_quote_age(self) -> timedelta | None:
        return (self.as_of_utc - self.instant_buy_time_utc) if self.instant_buy_time_utc else None

    @property
    def sell_quote_age(self) -> timedelta | None:
        return (self.as_of_utc - self.instant_sell_time_utc) if self.instant_sell_time_utc else None


@dataclass(frozen=True, slots=True)
class FeatureVector:
    market_item_id: int
    feature_time_utc: datetime
    feature_version: str
    source_cutoff_utc: datetime
    features: dict[str, float | int | None]
    observations_used: int
    is_data_sufficient: bool

    def __post_init__(self) -> None:
        require_utc(self.feature_time_utc, "feature_time_utc")
        require_utc(self.source_cutoff_utc, "source_cutoff_utc")
        if self.source_cutoff_utc > self.feature_time_utc:
            raise ValueError("feature source cutoff exceeds prediction time")


@dataclass(frozen=True, slots=True)
class PredictionOutput:
    market_item_id: int
    prediction_model_id: UUID
    feature_snapshot_id: int
    predicted_at_utc: datetime
    target_time_utc: datetime
    reference_price_gp: int | None
    predicted_price_change_pct: Decimal | None
    expected_profit_gp: int | None = None
    expected_roi_pct: Decimal | None = None
    probability_of_profit: Decimal | None = None
    probability_of_fill: Decimal | None = None

    def __post_init__(self) -> None:
        require_utc(self.predicted_at_utc, "predicted_at_utc")
        require_utc(self.target_time_utc, "target_time_utc")
        if self.target_time_utc <= self.predicted_at_utc:
            raise ValueError("target_time_utc must follow predicted_at_utc")
        for name in ("probability_of_profit", "probability_of_fill"):
            value = getattr(self, name)
            if value is not None and not (Decimal(0) <= value <= Decimal(1)):
                raise ValueError(f"{name} must be between 0 and 1")
