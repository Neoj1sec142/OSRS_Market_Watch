
"""
Phase 6C Pass 2 - Chronological split quality gates.

Validates:
- Minimum samples per horizon and partition
- Minimum effective chronological duration
- Maximum gaps between eligible decision timestamps
- Minimum temporal coverage
- Explicit test-label maturity cutoff

Does not choose split boundaries or inspect returns.
"""

from __future__ import annotations

from dataclasses import dataclass, replace

import pandas as pd

from .walk_forward import TemporalSplits


@dataclass(frozen=True, slots=True)
class SplitQualityPolicy:
    horizons: tuple[int, ...] = (30, 120)

    min_train_samples: int = 100
    min_validation_samples: int = 40
    min_test_samples: int = 40

    min_train_duration: str = "8h"
    min_validation_duration: str = "4h"
    min_test_duration: str = "4h"

    max_eligible_gap: str = "30min"

    # Strict mode fails on excessive eligible-decision gaps.
    # Exploratory mode records gaps as warnings instead.
    enforce_max_eligible_gap: bool = True

    grid_frequency: str = "5min"
    min_coverage_fraction: float = 0.50

    def __post_init__(self) -> None:
        if not self.horizons or len(set(self.horizons)) != len(
            self.horizons
        ):
            raise ValueError("Invalid horizon configuration")

        if any(h <= 0 for h in self.horizons):
            raise ValueError("Horizons must be positive")

        for value in (
            self.min_train_samples,
            self.min_validation_samples,
            self.min_test_samples,
        ):
            if value < 1:
                raise ValueError("Minimum samples must be positive")

        for value in (
            self.min_train_duration,
            self.min_validation_duration,
            self.min_test_duration,
            self.max_eligible_gap,
            self.grid_frequency,
        ):
            if pd.Timedelta(value) <= pd.Timedelta(0):
                raise ValueError("Durations must be positive")

        if not 0 < self.min_coverage_fraction <= 1:
            raise ValueError("Invalid coverage threshold")


class SplitQualityError(ValueError):
    pass


def utc_timestamp(value) -> pd.Timestamp:
    result = pd.Timestamp(value)

    if result.tzinfo is None:
        raise ValueError("Timestamp must be timezone-aware")

    return result.tz_convert("UTC").as_unit("ns")


def inspect_partition(
    frame: pd.DataFrame,
    *,
    name: str,
    horizon: int,
    min_samples: int,
    min_duration: str,
    policy: SplitQualityPolicy,
) -> tuple[dict, list[str]]:

    problems = []
    warnings = []

    timestamps = pd.DatetimeIndex(
        pd.to_datetime(
            frame["as_of_utc"], utc=True
        ).drop_duplicates().sort_values()
    )

    count = len(frame)
    unique_times = len(timestamps)

    if count != unique_times:
        problems.append(
            f"{name}/{horizon}m: duplicate decision times"
        )

    if count < min_samples:
        problems.append(
            f"{name}/{horizon}m: {count} samples; "
            f"minimum {min_samples}"
        )

    required_duration = pd.Timedelta(min_duration)
    frequency = pd.Timedelta(policy.grid_frequency)
    maximum_gap = pd.Timedelta(policy.max_eligible_gap)

    if unique_times >= 2:
        effective_duration = (
            timestamps[-1] - timestamps[0]
        )

        gaps = timestamps.to_series().diff().dropna()
        largest_gap = gaps.max()

        # Coverage is measured over the effective
        # eligible time span, not the nominal split.
        #
        # Missing decision points reduce coverage.
        expected_points = (
            int(effective_duration // frequency) + 1
        )

        coverage = (
            unique_times / expected_points
            if expected_points > 0 else 0.0
        )
    else:
        effective_duration = pd.Timedelta(0)
        largest_gap = pd.Timedelta(0)
        expected_points = unique_times
        coverage = 0.0

    if effective_duration < required_duration:
        problems.append(
            f"{name}/{horizon}m: effective duration "
            f"{effective_duration}; "
            f"minimum {required_duration}"
        )

    
    if largest_gap > maximum_gap:
        message = (
            f"{name}/{horizon}m: largest eligible gap "
            f"{largest_gap}; maximum {maximum_gap}"
        )

        if policy.enforce_max_eligible_gap:
            problems.append(message)
        else:
            warnings.append(message)


    if coverage < policy.min_coverage_fraction:
        problems.append(
            f"{name}/{horizon}m: coverage {coverage:.3f}; "
            f"minimum {policy.min_coverage_fraction:.3f}"
        )

    report = {
        "partition": name,
        "horizon_minutes": horizon,
        "samples": count,
        "unique_decision_times": unique_times,
        "first_decision_utc": (
            timestamps[0].isoformat()
            if unique_times else None
        ),
        "last_decision_utc": (
            timestamps[-1].isoformat()
            if unique_times else None
        ),
        "effective_duration_minutes": (
            effective_duration.total_seconds() / 60.0
        ),
        "largest_eligible_gap_minutes": (
            largest_gap.total_seconds() / 60.0
        ),
        "expected_points_effective_span": expected_points,
        "coverage_fraction": float(coverage),
        "passed": not problems,
        "issues": problems,
        "warnings": warnings,
    }

    return report, problems


def validate_split_quality(
    splits: TemporalSplits,
    *,
    evaluation_cutoff_utc,
    policy: SplitQualityPolicy | None = None,
) -> tuple[TemporalSplits, dict]:
    """
    Validate split quality without changing boundaries.

    Test rows whose labels were not available at
    evaluation_cutoff_utc are removed before scoring.

    Returns:
        (maturity-filtered splits, quality report)

    Raises:
        SplitQualityError when any required gate fails.
    """

    policy = policy or SplitQualityPolicy()

    cutoff = utc_timestamp(evaluation_cutoff_utc)

    if cutoff <= splits.test_start_utc:
        raise SplitQualityError(
            "Evaluation cutoff must follow test start"
        )

    if (
        splits.test["label_available_at_utc"].isna().any()
        or splits.test["target_time_utc"].isna().any()
    ):
        raise SplitQualityError(
            "Test contains missing label timestamps"
        )

    # Explicit evaluation cutoff: never score an
    # outcome that wasn't observable yet.
    matured_test = splits.test.loc[
        (
            splits.test["label_available_at_utc"]
            <= cutoff
        )
        & (
            splits.test["target_time_utc"]
            <= cutoff
        )
    ].copy()

    filtered = replace(
        splits,
        test=matured_test,
    )

    requirements = {
        "train": (
            filtered.train,
            policy.min_train_samples,
            policy.min_train_duration,
        ),
        "validation": (
            filtered.validation,
            policy.min_validation_samples,
            policy.min_validation_duration,
        ),
        "test": (
            filtered.test,
            policy.min_test_samples,
            policy.min_test_duration,
        ),
    }

    reports = []
    problems = []
    warnings = []
    available_horizons = set()

    for frame, _, _ in requirements.values():
        available_horizons.update(
            int(h) for h in frame["horizon_minutes"].unique()
        )

    unknown = available_horizons - set(policy.horizons)
    if unknown:
        problems.append(
            f"Unexpected horizons: {sorted(unknown)}"
        )

    for horizon in policy.horizons:
        for name, (
            frame,
            min_samples,
            min_duration,
        ) in requirements.items():

            subset = frame.loc[
                frame["horizon_minutes"] == horizon
            ]

            result, issues = inspect_partition(
                subset,
                name=name,
                horizon=horizon,
                min_samples=min_samples,
                min_duration=min_duration,
                policy=policy,
            )

            reports.append(result)
            problems.extend(issues)
            warnings.extend(result["warnings"])

    report = {
        "policy_version": "split-quality-v1",
        "evaluation_cutoff_utc": cutoff.isoformat(),
        "validation_start_utc": (
            splits.validation_start_utc.isoformat()
        ),
        "test_start_utc": (
            splits.test_start_utc.isoformat()
        ),
        "policy": {
            "horizons": list(policy.horizons),
            "min_train_samples": policy.min_train_samples,
            "min_validation_samples": (
                policy.min_validation_samples
            ),
            "min_test_samples": policy.min_test_samples,
            "min_train_duration": policy.min_train_duration,
            "min_validation_duration": (
                policy.min_validation_duration
            ),
            "min_test_duration": policy.min_test_duration,
            "max_eligible_gap": policy.max_eligible_gap,
            "grid_frequency": policy.grid_frequency,
            "min_coverage_fraction": (
                policy.min_coverage_fraction
            ),
            "enforce_max_eligible_gap": (
                policy.enforce_max_eligible_gap
            ),
        },
        "partitions": reports,
        "passed": not problems,
        "issues": problems,
        "warnings": warnings,
    }

    if problems:
        details = "\n".join(
            f"  - {problem}" for problem in problems
        )
        raise SplitQualityError(
            "Chronological split quality failed:\n"
            + details
        )

    return filtered, report
