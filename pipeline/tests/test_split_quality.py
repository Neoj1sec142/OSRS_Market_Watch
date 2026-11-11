
from dataclasses import replace
from datetime import datetime, timedelta, timezone

import pandas as pd
import pytest

from osrs_market_ml.evaluation.walk_forward import (
    chronological_splits,
    prepare_pairs,
)
from osrs_market_ml.evaluation.split_quality import (
    SplitQualityError,
    SplitQualityPolicy,
    validate_split_quality,
)


START = datetime(
    2026, 11, 11, 0, 0,
    tzinfo=timezone.utc,
)


def make_splits(count=500):
    features = []
    labels = []

    for i in range(count):
        as_of = START + timedelta(minutes=5 * i)

        features.append({
            "item_id": 4151,
            "as_of_utc": as_of,
            "feature_eligible": True,
        })

        for horizon in (30, 120):
            target = as_of + timedelta(
                minutes=horizon
            )

            labels.append({
                "item_id": 4151,
                "as_of_utc": as_of,
                "horizon_minutes": horizon,
                "target_time_utc": target,
                "target_observation_time_utc": target,
                "label_available_at_utc": (
                    target + timedelta(minutes=1)
                ),
                "status": "valid",
                "forward_log_return": 0.001,
            })

    pairs = prepare_pairs(
        pd.DataFrame(features),
        pd.DataFrame(labels),
    )

    return chronological_splits(pairs)


def cutoff_for(splits):
    return (
        splits.test["label_available_at_utc"].max()
        + pd.Timedelta(minutes=1)
    )


def test_healthy_chronological_splits_pass():
    splits = make_splits()

    filtered, report = validate_split_quality(
        splits,
        evaluation_cutoff_utc=cutoff_for(splits),
    )

    assert report["passed"]
    assert len(report["partitions"]) == 6

    assert not filtered.train.empty
    assert not filtered.validation.empty
    assert not filtered.test.empty


def test_minimum_sample_count_is_enforced():
    splits = make_splits()

    policy = SplitQualityPolicy(
        min_validation_samples=1000,
    )

    with pytest.raises(
        SplitQualityError,
        match="samples",
    ):
        validate_split_quality(
            splits,
            evaluation_cutoff_utc=cutoff_for(splits),
            policy=policy,
        )


def test_minimum_effective_duration_is_enforced():
    splits = make_splits()

    policy = SplitQualityPolicy(
        min_validation_duration="20h",
    )

    with pytest.raises(
        SplitQualityError,
        match="effective duration",
    ):
        validate_split_quality(
            splits,
            evaluation_cutoff_utc=cutoff_for(splits),
            policy=policy,
        )


def test_large_eligible_gap_is_rejected():
    splits = make_splits()

    # Remove a 90-minute section of validation
    # decisions for both prediction horizons.
    validation = splits.validation.copy()

    first = validation["as_of_utc"].min()

    sparse = validation.loc[
        ~validation["as_of_utc"].between(
            first + pd.Timedelta(hours=2),
            first + pd.Timedelta(hours=3, minutes=30),
        )
    ].copy()

    modified = replace(
        splits,
        validation=sparse,
    )

    with pytest.raises(
        SplitQualityError,
        match="largest eligible gap",
    ):
        validate_split_quality(
            modified,
            evaluation_cutoff_utc=cutoff_for(modified),
        )


def test_exploratory_mode_records_large_gap_warning():
    splits = make_splits()

    validation = splits.validation.copy()
    first = validation["as_of_utc"].min()

    sparse = validation.loc[
        ~validation["as_of_utc"].between(
            first + pd.Timedelta(hours=2),
            first + pd.Timedelta(hours=3, minutes=30),
        )
    ].copy()

    modified = replace(
        splits,
        validation=sparse,
    )

    policy = SplitQualityPolicy(
        enforce_max_eligible_gap=False,
    )

    filtered, report = validate_split_quality(
        modified,
        evaluation_cutoff_utc=cutoff_for(modified),
        policy=policy,
    )

    assert report["passed"]
    assert report["warnings"]

    assert any(
        "largest eligible gap" in warning
        for warning in report["warnings"]
    )


def test_strict_mode_rejects_same_gap():
    splits = make_splits()

    validation = splits.validation.copy()
    first = validation["as_of_utc"].min()

    sparse = validation.loc[
        ~validation["as_of_utc"].between(
            first + pd.Timedelta(hours=2),
            first + pd.Timedelta(hours=3, minutes=30),
        )
    ].copy()

    modified = replace(
        splits,
        validation=sparse,
    )

    policy = SplitQualityPolicy(
        enforce_max_eligible_gap=True,
    )

    with pytest.raises(
        SplitQualityError,
        match="largest eligible gap",
    ):
        validate_split_quality(
            modified,
            evaluation_cutoff_utc=cutoff_for(modified),
            policy=policy,
        )


def test_low_temporal_coverage_is_rejected():
    splits = make_splits()

    # Keep every fourth eligible decision.
    # This produces one decision every 20 minutes.
    sparse_validation = pd.concat(
        [
            group.sort_values("as_of_utc").iloc[::4]
            for _, group in splits.validation.groupby(
                "horizon_minutes"
            )
        ],
        ignore_index=True,
    )

    modified = replace(
        splits,
        validation=sparse_validation,
    )

    policy = SplitQualityPolicy(
        min_validation_samples=5,
        max_eligible_gap="30min",
        min_coverage_fraction=0.75,
    )

    with pytest.raises(
        SplitQualityError,
        match="coverage",
    ):
        validate_split_quality(
            modified,
            evaluation_cutoff_utc=cutoff_for(modified),
            policy=policy,
        )


def test_immature_test_labels_are_excluded():
    splits = make_splits()

    # Only allow outcomes available through the
    # midpoint of the test decision interval.
    midpoint = (
        splits.test_start_utc
        + pd.Timedelta(hours=5)
    )

    policy = SplitQualityPolicy(
        min_test_samples=5,
        min_test_duration="1h",
    )

    filtered, report = validate_split_quality(
        splits,
        evaluation_cutoff_utc=midpoint,
        policy=policy,
    )

    assert report["passed"]

    assert (
        filtered.test["label_available_at_utc"]
        <= midpoint
    ).all()

    assert len(filtered.test) < len(splits.test)


def test_insufficient_matured_test_data_fails():
    splits = make_splits()

    cutoff = (
        splits.test_start_utc
        + pd.Timedelta(minutes=10)
    )

    with pytest.raises(SplitQualityError):
        validate_split_quality(
            splits,
            evaluation_cutoff_utc=cutoff,
        )
