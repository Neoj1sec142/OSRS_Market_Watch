
from datetime import datetime, timedelta, timezone

import numpy as np
import pandas as pd
import pytest

from osrs_market_ml.evaluation.walk_forward import (
    chronological_splits,
    evaluate_persistence,
    prepare_pairs,
)


START = datetime(
    2026, 11, 11, 0, 0,
    tzinfo=timezone.utc,
)


def make_frames(count=100, horizon=120):
    features = []
    labels = []

    for i in range(count):
        as_of = START + timedelta(
            minutes=5 * i
        )

        target = as_of + timedelta(
            minutes=horizon
        )

        available = target + timedelta(
            minutes=1
        )

        features.append({
            "item_id": 4151,
            "as_of_utc": as_of,
            "feature_eligible": True,
            "reference_price_gp": 1000.0,
        })

        labels.append({
            "item_id": 4151,
            "as_of_utc": as_of,
            "horizon_minutes": horizon,
            "target_time_utc": target,
            "target_observation_time_utc": target,
            "label_available_at_utc": available,
            "status": "valid",
            "forward_log_return": 0.01,
        })

    return (
        pd.DataFrame(features),
        pd.DataFrame(labels),
    )


def test_temporal_splits_do_not_overlap():
    features, labels = make_frames()

    pairs = prepare_pairs(features, labels)
    splits = chronological_splits(pairs)

    assert (
        splits.train["as_of_utc"]
        < splits.validation_start_utc
    ).all()

    assert (
        splits.validation["as_of_utc"]
        >= splits.validation_start_utc
    ).all()

    assert (
        splits.validation["as_of_utc"]
        < splits.test_start_utc
    ).all()

    assert (
        splits.test["as_of_utc"]
        >= splits.test_start_utc
    ).all()


def test_train_labels_mature_before_validation():
    features, labels = make_frames()

    pairs = prepare_pairs(features, labels)
    splits = chronological_splits(pairs)

    assert not splits.train.empty

    assert (
        splits.train["label_available_at_utc"]
        < splits.validation_start_utc
    ).all()

    assert (
        splits.train["target_time_utc"]
        < splits.validation_start_utc
    ).all()


def test_validation_labels_mature_before_test():
    # 300 points gives the validation interval
    # 300 minutes, enough for 120-minute labels
    # to mature before the test boundary.
    features, labels = make_frames(
        count=300,
        horizon=120,
    )

    pairs = prepare_pairs(features, labels)
    splits = chronological_splits(pairs)

    assert not splits.validation.empty

    assert (
        splits.validation["label_available_at_utc"]
        < splits.test_start_utc
    ).all()

    assert (
        splits.validation["target_time_utc"]
        < splits.test_start_utc
    ).all()



def test_validation_can_be_empty_when_horizon_exceeds_window():
    features, labels = make_frames(
        count=100,
        horizon=120,
    )

    pairs = prepare_pairs(features, labels)
    splits = chronological_splits(pairs)

    # The synthetic validation period is only
    # 100 minutes, shorter than the 120-minute
    # prediction horizon.
    assert splits.validation.empty

    # Train and test partitions still exist.
    assert not splits.train.empty
    assert not splits.test.empty



def test_overlapping_labels_are_purged():
    features, labels = make_frames(
        count=100, horizon=120
    )

    pairs = prepare_pairs(features, labels)
    splits = chronological_splits(pairs)

    # The first 60 decision timestamps fall inside
    # the nominal training interval.
    #
    # However, later labels cross the validation
    # boundary and must be excluded.
    assert len(splits.train) < 60

    # The first 20 validation timestamps similarly
    # contain some labels crossing into test.
    assert len(splits.validation) < 20


def test_persistence_metrics():
    frame = pd.DataFrame({
        "forward_log_return": [
            0.01, -0.02, 0.03
        ]
    })

    metrics = evaluate_persistence(frame)

    assert metrics["samples"] == 3

    assert metrics["mae"] == pytest.approx(
        0.02
    )

    assert metrics["rmse"] == pytest.approx(
        np.sqrt(
            (0.01**2 + 0.02**2 + 0.03**2) / 3
        )
    )

    assert metrics["bias"] == pytest.approx(
        (0.01 - 0.02 + 0.03) / 3
    )


def test_duplicate_labels_are_rejected():
    features, labels = make_frames()

    duplicate = pd.concat(
        [labels, labels.iloc[[0]]],
        ignore_index=True,
    )

    with pytest.raises(
        ValueError,
        match="Duplicate label",
    ):
        prepare_pairs(features, duplicate)


def test_feature_eligibility_strings():
    features, labels = make_frames(20)

    features["feature_eligible"] = "False"

    pairs = prepare_pairs(features, labels)

    assert not pairs["candidate_eligible"].any()


def test_invalid_temporal_label_rejected():
    features, labels = make_frames()

    labels.loc[0, "label_available_at_utc"] = (
        labels.loc[0, "as_of_utc"]
    )

    with pytest.raises(
        ValueError,
        match="temporal",
    ):
        prepare_pairs(features, labels)
