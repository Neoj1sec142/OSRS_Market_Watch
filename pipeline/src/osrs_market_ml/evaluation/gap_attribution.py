
"""
Phase 6C Pass 3 - Chronological gap attribution.

Explains why individual decision timestamps were
excluded from train, validation, or test.

Does not change evaluation boundaries, feature data,
labels, or eligibility rules.

Primary reason priority:
    feature_ineligible
    label_<status>
    label_not_mature
    eligible

Missing grid positions are reported separately.
"""

from __future__ import annotations

from collections import Counter
from itertools import pairwise

import pandas as pd

from .walk_forward import TemporalSplits


ALLOWED_STATUSES = {
    "valid",
    "missing",
    "stale",
    "censored",
    "unavailable",
}


def utc_timestamp(value) -> pd.Timestamp:
    timestamp = pd.Timestamp(value)

    if timestamp.tzinfo is None:
        raise ValueError("Timestamp must be timezone-aware")

    return timestamp.tz_convert("UTC").as_unit("ns")


def classify_row(
    row: pd.Series,
    *,
    maturity_boundary: pd.Timestamp,
    inclusive_cutoff: bool,
) -> str:
    """
    Return the primary reason a row is excluded.

    The feature and label checks occur before maturity,
    so a row with several defects receives one
    deterministic primary reason.
    """

    if not bool(row["feature_eligible"]):
        return "feature_ineligible"

    status = str(row["status"])

    if status not in ALLOWED_STATUSES:
        raise ValueError(f"Unknown label status: {status}")

    if status != "valid":
        return f"label_{status}"

    available = row["label_available_at_utc"]
    target = row["target_time_utc"]

    if pd.isna(available) or pd.isna(target):
        raise ValueError(
            "Valid label has missing temporal fields"
        )

    if inclusive_cutoff:
        matured = (
            available <= maturity_boundary
            and target <= maturity_boundary
        )
    else:
        matured = (
            available < maturity_boundary
            and target < maturity_boundary
        )

    return "eligible" if matured else "label_not_mature"


def partition_for_time(
    timestamp: pd.Timestamp,
    splits: TemporalSplits,
) -> str:
    if timestamp < splits.validation_start_utc:
        return "train"

    if timestamp < splits.test_start_utc:
        return "validation"

    return "test"


def build_gap_attribution(
    pairs: pd.DataFrame,
    splits: TemporalSplits,
    *,
    evaluation_cutoff_utc,
    grid_frequency: str = "5min",
    gap_threshold: str = "30min",
) -> dict:
    """
    Build attribution for a single-item research run.

    Inputs:
        pairs: output of prepare_pairs()
        splits: output of chronological_splits()

    The test membership is restricted to outcomes
    matured by evaluation_cutoff_utc.

    A gap is measured between two eligible decisions.
    Leading/trailing exclusions are counted separately,
    not misrepresented as interior gaps.
    """

    frequency = pd.Timedelta(grid_frequency)
    threshold = pd.Timedelta(gap_threshold)
    cutoff = utc_timestamp(evaluation_cutoff_utc)

    if frequency <= pd.Timedelta(0):
        raise ValueError("Grid frequency must be positive")

    if threshold <= pd.Timedelta(0):
        raise ValueError("Gap threshold must be positive")

    if pairs.empty:
        raise ValueError("No feature/label pairs")

    item_ids = pairs["item_id"].unique()

    if len(item_ids) != 1:
        raise ValueError(
            "This audit requires exactly one item"
        )

    if pairs[
        ["item_id", "as_of_utc", "horizon_minutes"]
    ].duplicated().any():
        raise ValueError("Duplicate research pair identity")

    if cutoff <= splits.test_start_utc:
        raise ValueError(
            "Evaluation cutoff must follow test start"
        )

    work = pairs.copy()
    work["as_of_utc"] = pd.to_datetime(
        work["as_of_utc"], utc=True
    ).dt.as_unit("ns")

    work["target_time_utc"] = pd.to_datetime(
        work["target_time_utc"], utc=True
    ).dt.as_unit("ns")

    work["label_available_at_utc"] = pd.to_datetime(
        work["label_available_at_utc"], utc=True
    ).dt.as_unit("ns")

    work = work.sort_values(
        ["horizon_minutes", "as_of_utc"]
    ).reset_index(drop=True)

    horizons = sorted(
        int(h) for h in work["horizon_minutes"].unique()
    )

    all_decisions = pd.DatetimeIndex(
        work["as_of_utc"].drop_duplicates().sort_values()
    )

    start = all_decisions.min()
    end = all_decisions.max()

    expected_times = pd.date_range(
        start, end, freq=frequency, tz="UTC"
    ).as_unit("ns")

    if not all_decisions.isin(expected_times).all():
        raise ValueError(
            "Decision timestamps do not align to grid"
        )

    selected_sets = {}

    for name, frame in (
        ("train", splits.train),
        ("validation", splits.validation),
        ("test", splits.test),
    ):
        for horizon in horizons:
            subset = frame.loc[
                frame["horizon_minutes"] == horizon
            ]

            times = set(
                pd.to_datetime(
                    subset["as_of_utc"], utc=True
                ).dt.as_unit("ns")
            )

            if name == "test":
                subset = subset.loc[
                    (
                        subset["label_available_at_utc"]
                        <= cutoff
                    )
                    & (
                        subset["target_time_utc"]
                        <= cutoff
                    )
                ]

                times = set(
                    pd.to_datetime(
                        subset["as_of_utc"], utc=True
                    ).dt.as_unit("ns")
                )

            selected_sets[(name, horizon)] = times

    reports = []
    long_gaps = []

    for horizon in horizons:
        horizon_rows = work.loc[
            work["horizon_minutes"] == horizon
        ]

        lookup = {
            row["as_of_utc"]: row
            for _, row in horizon_rows.iterrows()
        }

        by_partition = {
            name: []
            for name in ("train", "validation", "test")
        }

        for timestamp in expected_times:
            name = partition_for_time(timestamp, splits)

            if name == "train":
                boundary = utc_timestamp(
                    splits.validation_start_utc
                )
                inclusive = False

            elif name == "validation":
                boundary = utc_timestamp(
                    splits.test_start_utc
                )
                inclusive = False

            else:
                boundary = cutoff
                inclusive = True

            row = lookup.get(timestamp)

            if row is None:
                reason = "missing_decision"
            else:
                reason = classify_row(
                    row,
                    maturity_boundary=boundary,
                    inclusive_cutoff=inclusive,
                )

            by_partition[name].append(
                (timestamp, reason)
            )

        for name, entries in by_partition.items():
            counts = Counter(
                reason for _, reason in entries
            )

            eligible = [
                timestamp
                for timestamp, reason in entries
                if reason == "eligible"
            ]

            expected_selected = selected_sets[
                (name, horizon)
            ]

            # The auditor must agree with the
            # existing chronological splitter.
            if set(eligible) != expected_selected:
                raise ValueError(
                    f"Attribution/split mismatch: "
                    f"{name}/{horizon}m"
                )

            for left, right in pairwise(eligible):
                duration = right - left

                if duration <= threshold:
                    continue

                interior = [
                    (timestamp, reason)
                    for timestamp, reason in entries
                    if left < timestamp < right
                ]

                reason_counts = Counter(
                    reason for _, reason in interior
                )

                long_gaps.append({
                    "partition": name,
                    "horizon_minutes": horizon,
                    "start_utc": left.isoformat(),
                    "end_utc": right.isoformat(),
                    "duration_minutes": (
                        duration.total_seconds() / 60
                    ),
                    "excluded_decisions": len(interior),
                    "primary_reasons": dict(
                        sorted(reason_counts.items())
                    ),
                    "excluded_timestamps": [
                        {
                            "as_of_utc": t.isoformat(),
                            "reason": reason,
                        }
                        for t, reason in interior
                    ],
                })

            reports.append({
                "partition": name,
                "horizon_minutes": horizon,
                "total_decision_slots": len(entries),
                "eligible": len(eligible),
                "excluded": (
                    len(entries) - len(eligible)
                ),
                "reason_counts": dict(
                    sorted(counts.items())
                ),
            })

    long_gaps.sort(
        key=lambda gap: (
            -gap["duration_minutes"],
            gap["partition"],
            gap["horizon_minutes"],
            gap["start_utc"],
        )
    )

    return {
        "audit_version": "gap-attribution-v1",
        "item_id": int(item_ids[0]),
        "evaluation_cutoff_utc": cutoff.isoformat(),
        "grid_frequency": grid_frequency,
        "gap_threshold": gap_threshold,
        "validation_start_utc": (
            splits.validation_start_utc.isoformat()
        ),
        "test_start_utc": (
            splits.test_start_utc.isoformat()
        ),
        "reason_priority": [
            "feature_ineligible",
            "label_<status>",
            "label_not_mature",
            "eligible",
        ],
        "partitions": reports,
        "long_gaps": long_gaps,
        "long_gap_count": len(long_gaps),
    }
