
"""
Phase 6A - Canonical research contracts.

Persistence data is immutable here.
No database access, Pandas operations, or ML dependencies.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from enum import StrEnum
from math import isfinite
from decimal import Decimal


SCHEMA_VERSION = "research-v1"


def require_utc(value: datetime, field: str) -> None:
    if value.tzinfo is None or value.utcoffset() is None:
        raise ValueError(f"{field} must be timezone-aware")

    if value.utcoffset().total_seconds() != 0:
        raise ValueError(f"{field} must use UTC")


def require_sha256(value: str) -> None:
    if len(value) != 64:
        raise ValueError("Expected a 64-character SHA-256")

    try:
        bytes.fromhex(value)
    except ValueError as exc:
        raise ValueError("Invalid SHA-256") from exc


class ObservationKind(StrEnum):
    SNAPSHOT = "snapshot"
    AGGREGATE_5M = "aggregate_5m"
    AGGREGATE_1H = "aggregate_1h"


class LabelStatus(StrEnum):
    VALID = "valid"
    MISSING = "missing"
    STALE = "stale"
    CENSORED = "censored"
    UNAVAILABLE = "unavailable"


@dataclass(frozen=True, slots=True)
class ResearchSource:
    """
    Identity of a verified input artifact.

    source_guid is nullable until extracted from
    validated archive metadata.
    """

    artifact_sha256: str
    source_label: str
    source_guid: str | None = None
    dataset_version: str = SCHEMA_VERSION

    def __post_init__(self) -> None:
        require_sha256(self.artifact_sha256)

        if not self.source_label.strip():
            raise ValueError("Source label is required")


@dataclass(frozen=True, slots=True)
class MarketObservationV1:
    """
    Original snapshot semantics from MarketPriceSnapshots.

    Price fields correspond to Wiki high/low traded prices,
    not executable bid/ask quotes.
    """

    source: ResearchSource

    source_row_id: int
    item_id: int
    collection_batch_id: int

    instant_buy_price_gp: int | None
    instant_sell_price_gp: int | None

    instant_buy_time_utc: datetime | None
    instant_sell_time_utc: datetime | None

    collected_at_utc: datetime
    batch_completed_at_utc: datetime

    # Keep exact original SQLite timestamp text.
    raw_collected_at_utc: str

    def __post_init__(self) -> None:
        if self.source_row_id <= 0 or self.item_id <= 0:
            raise ValueError("Invalid observation identity")

        if self.collection_batch_id <= 0:
            raise ValueError("Invalid collection batch")

        for name in (
            "collected_at_utc",
            "batch_completed_at_utc",
        ):
            require_utc(getattr(self, name), name)

        for name in (
            "instant_buy_time_utc",
            "instant_sell_time_utc",
        ):
            value = getattr(self, name)
            if value is not None:
                require_utc(value, name)

        for name in (
            "instant_buy_price_gp",
            "instant_sell_price_gp",
        ):
            value = getattr(self, name)
            if value is not None and value <= 0:
                raise ValueError(f"{name} must be positive")

        if not self.raw_collected_at_utc:
            raise ValueError("Original timestamp is required")

    @property
    def available_at_utc(self) -> datetime:
        return max(
            self.collected_at_utc,
            self.batch_completed_at_utc,
        )

    def known_at(self, decision_time: datetime) -> bool:
        require_utc(decision_time, "decision_time")
        return self.available_at_utc <= decision_time


@dataclass(frozen=True, slots=True)
class MarketAggregateV1:
    """
    Latest persisted aggregate bucket.

    IMPORTANT:
    This is not an immutable historical revision.
    """

    source: ResearchSource
    source_row_id: int
    item_id: int

    interval_minutes: int
    bucket_start_utc: datetime
    collected_at_utc: datetime

    avg_instant_buy_gp: Decimal | None
    avg_instant_sell_gp: Decimal | None

    instant_buy_volume: int
    instant_sell_volume: int

    raw_bucket_start_utc: str

    def __post_init__(self) -> None:
        if self.source_row_id <= 0 or self.item_id <= 0:
            raise ValueError("Invalid aggregate identity")

        if self.interval_minutes not in (5, 60):
            raise ValueError("Unsupported aggregate interval")

        require_utc(
            self.bucket_start_utc,
            "bucket_start_utc",
        )
        require_utc(
            self.collected_at_utc,
            "collected_at_utc",
        )

        if not self.raw_bucket_start_utc:
            raise ValueError("Original bucket timestamp required")

        if (
            self.instant_buy_volume < 0
            or self.instant_sell_volume < 0
        ):
            raise ValueError("Negative aggregate volume")

        for price in (
            self.avg_instant_buy_gp,
            self.avg_instant_sell_gp,
        ):
            if price is not None and (
                not price.is_finite() or price <= 0
            ):
                raise ValueError("Invalid aggregate price")

    @property
    def kind(self) -> ObservationKind:
        return (
            ObservationKind.AGGREGATE_5M
            if self.interval_minutes == 5
            else ObservationKind.AGGREGATE_1H
        )

    @property
    def bucket_end_utc(self) -> datetime:
        from datetime import timedelta

        return self.bucket_start_utc + timedelta(
            minutes=self.interval_minutes
        )


@dataclass(frozen=True, slots=True)
class MarketFeatureV1:
    """
    Point-in-time feature vector.

    No future labels or actual outcomes belong here.
    """

    item_id: int
    as_of_utc: datetime

    source_dataset_sha256: str
    feature_version: str

    reference_price_gp: float | None = None
    observed_gap_pct: float | None = None

    return_30m: float | None = None
    return_2h: float | None = None
    volatility_2h: float | None = None

    quote_age_buy_seconds: float | None = None
    quote_age_sell_seconds: float | None = None

    observation_count_2h: int = 0
    data_quality_flags: tuple[str, ...] = ()

    def __post_init__(self) -> None:
        require_utc(self.as_of_utc, "as_of_utc")
        require_sha256(self.source_dataset_sha256)

        if self.item_id <= 0:
            raise ValueError("Invalid item ID")

        if not self.feature_version:
            raise ValueError("Feature version is required")

        if self.observation_count_2h < 0:
            raise ValueError("Negative observation count")

        numeric_fields = (
            "reference_price_gp",
            "observed_gap_pct",
            "return_30m",
            "return_2h",
            "volatility_2h",
            "quote_age_buy_seconds",
            "quote_age_sell_seconds",
        )

        for name in numeric_fields:
            value = getattr(self, name)
            if value is not None and not isfinite(value):
                raise ValueError(f"{name} must be finite")

        for name in (
            "quote_age_buy_seconds",
            "quote_age_sell_seconds",
            "volatility_2h",
        ):
            value = getattr(self, name)
            if value is not None and value < 0:
                raise ValueError(f"{name} cannot be negative")


@dataclass(frozen=True, slots=True)
class MarketLabelV1:
    """
    Offline supervised-learning target.

    Never include this in inference input.
    """

    item_id: int
    as_of_utc: datetime
    horizon_minutes: int
    target_time_utc: datetime

    target_version: str
    status: LabelStatus

    forward_log_return: float | None = None
    target_observation_time_utc: datetime | None = None
    label_available_at_utc: datetime | None = None

    def __post_init__(self) -> None:
        from datetime import timedelta

        require_utc(self.as_of_utc, "as_of_utc")
        require_utc(
            self.target_time_utc,
            "target_time_utc",
        )

        if self.item_id <= 0:
            raise ValueError("Invalid item ID")

        if not self.target_version:
            raise ValueError("Target version is required")

        if self.horizon_minutes not in (30, 120):
            raise ValueError("Unsupported prediction horizon")

        expected = self.as_of_utc + timedelta(
            minutes=self.horizon_minutes
        )

        if self.target_time_utc != expected:
            raise ValueError("Target horizon mismatch")

        for name in (
            "target_observation_time_utc",
            "label_available_at_utc",
        ):
            value = getattr(self, name)
            if value is not None:
                require_utc(value, name)

        if self.status == LabelStatus.VALID:
            if (
                self.forward_log_return is None
                or not isfinite(self.forward_log_return)
                or self.target_observation_time_utc is None
                or self.label_available_at_utc is None
            ):
                raise ValueError(
                    "Valid labels require return, "
                    "observation time and availability time"
                )

            if self.label_available_at_utc <= self.as_of_utc:
                raise ValueError(
                    "Label cannot be available at prediction time"
                )

            if (
                self.label_available_at_utc
                < self.target_observation_time_utc
            ):
                raise ValueError(
                    "Label availability precedes observation"
                )
        elif self.forward_log_return is not None:
            raise ValueError(
                "Non-valid labels must not expose a return"
            )
