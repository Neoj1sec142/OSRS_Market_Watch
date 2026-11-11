
"""
Phase 6A - Canonical dataset validation.

Errors prevent research acceptance.
Warnings describe data quality without inventing data.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from statistics import median

from .canonical_loader import CanonicalDataset


@dataclass(frozen=True, slots=True)
class StreamCoverage:
    interval_minutes: int
    row_count: int

    first_bucket_utc: datetime | None
    last_bucket_utc: datetime | None

    expected_buckets: int
    missing_buckets: int


@dataclass(frozen=True, slots=True)
class DatasetValidationReport:
    item_id: int
    dataset_sha256: str

    snapshot_count: int
    first_available_utc: datetime | None
    last_available_utc: datetime | None

    median_buy_quote_age_minutes: float | None
    median_sell_quote_age_minutes: float | None

    five_minute: StreamCoverage
    hourly: StreamCoverage

    errors: tuple[str, ...]
    warnings: tuple[str, ...]

    @property
    def passed(self) -> bool:
        return not self.errors


def validate_bucket_stream(
    aggregates,
    interval_minutes: int,
    errors: list[str],
    warnings: list[str],
) -> StreamCoverage:

    times = sorted(
        row.bucket_start_utc
        for row in aggregates
    )

    if not times:
        warnings.append(
            f"No {interval_minutes}-minute aggregates"
        )

        return StreamCoverage(
            interval_minutes,
            0,
            None,
            None,
            0,
            0,
        )

    unique_keys = set()
    interval_seconds = interval_minutes * 60

    for row in aggregates:

        key = (
            row.item_id,
            row.interval_minutes,
            row.raw_bucket_start_utc,
        )

        if key in unique_keys:
            errors.append(
                f"Duplicate aggregate bucket: {key}"
            )

        unique_keys.add(key)

        start = row.bucket_start_utc

        if int(start.timestamp()) % interval_seconds != 0:
            errors.append(
                f"Misaligned bucket: {row.source_row_id}"
            )

        if row.collected_at_utc < row.bucket_start_utc:
            errors.append(
                f"Aggregate collected before bucket start: "
                f"{row.source_row_id}"
            )

    first = times[0]
    last = times[-1]

    expected = (
        int((last - first).total_seconds())
        // interval_seconds
    ) + 1

    unique_times = len(set(times))
    missing = max(0, expected - unique_times)

    if missing:
        warnings.append(
            f"{interval_minutes}m stream missing "
            f"{missing} bucket(s) inside observed range"
        )

    return StreamCoverage(
        interval_minutes=interval_minutes,
        row_count=len(aggregates),
        first_bucket_utc=first,
        last_bucket_utc=last,
        expected_buckets=expected,
        missing_buckets=missing,
    )


def validate_dataset(
    dataset: CanonicalDataset,
    *,
    maximum_quote_age_minutes: int = 30,
) -> DatasetValidationReport:

    errors: list[str] = []
    warnings: list[str] = []

    seen_snapshots = set()

    availability_times = []
    buy_ages = []
    sell_ages = []

    for row in dataset.observations:

        key = (
            row.source.artifact_sha256,
            row.source_row_id,
        )

        if key in seen_snapshots:
            errors.append(
                f"Duplicate snapshot identity: {key}"
            )

        seen_snapshots.add(key)

        if row.batch_completed_at_utc < row.collected_at_utc:
            errors.append(
                f"Batch completes before collection: "
                f"snapshot {row.source_row_id}"
            )

        available = row.available_at_utc
        availability_times.append(available)

        for side, quote_time, price, ages in (
            (
                "buy",
                row.instant_buy_time_utc,
                row.instant_buy_price_gp,
                buy_ages,
            ),
            (
                "sell",
                row.instant_sell_time_utc,
                row.instant_sell_price_gp,
                sell_ages,
            ),
        ):

            if price is None or quote_time is None:
                continue

            age = (
                available - quote_time
            ).total_seconds() / 60.0

            if age < 0:
                warnings.append(
                    f"Future-dated {side} quote: "
                    f"snapshot {row.source_row_id}"
                )
                continue

            ages.append(age)

    def quote_summary(ages, side):
        if not ages:
            return None

        stale = sum(
            age > maximum_quote_age_minutes
            for age in ages
        )

        if stale:
            warnings.append(
                f"{stale} {side} quotes exceed "
                f"{maximum_quote_age_minutes} minutes"
            )

        return median(ages)

    median_buy = quote_summary(buy_ages, "buy")
    median_sell = quote_summary(sell_ages, "sell")

    five_minute = validate_bucket_stream(
        dataset.aggregates_5m,
        5,
        errors,
        warnings,
    )

    hourly = validate_bucket_stream(
        dataset.aggregates_1h,
        60,
        errors,
        warnings,
    )

    return DatasetValidationReport(
        item_id=dataset.item_id,
        dataset_sha256=dataset.dataset_sha256,
        snapshot_count=len(dataset.observations),
        first_available_utc=(
            min(availability_times)
            if availability_times else None
        ),
        last_available_utc=(
            max(availability_times)
            if availability_times else None
        ),
        median_buy_quote_age_minutes=median_buy,
        median_sell_quote_age_minutes=median_sell,
        five_minute=five_minute,
        hourly=hourly,
        errors=tuple(errors),
        warnings=tuple(warnings),
    )
