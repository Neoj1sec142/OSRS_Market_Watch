
from dataclasses import replace
from datetime import datetime, timedelta, timezone

import pandas as pd
import pytest

from osrs_market_ml.evaluation.walk_forward import (
    chronological_splits,
    prepare_pairs,
)
from osrs_market_ml.evaluation.gap_attribution import (
    build_gap_attribution,
)


START = datetime(
    2026, 11, 11, 0, 0,
    tzinfo=timezone.utc,
)


def make_data(
    *,
    count=120,
    feature_exclusions=(),
    missing_labels=(),
):
    features = []
    labels = []

    for i in range(count):
        as_of = START + timedelta(minutes=5 * i)

        target = as_of + timedelta(minutes=30)
        available = target + timedelta(minutes=1)

        features.append({
            "item_id": 4151,
            "as_of_utc": as_of,
            "feature_eligible": (
                i not in feature_exclusions
            ),
        })

        is_missing = i in missing_labels

        labels.append({
            "item_id": 4151,
            "as_of_utc": as_of,
            "horizon_minutes": 30,
            "target_time_utc": target,
            "target_observation_time_utc": (
                pd.NaT if is_missing else target
            ),
            "label_available_at_utc": (
                pd.NaT if is_missing else available
            ),
            "status": (
                "missing" if is_missing else "valid"
            ),
            "forward_log_return": (
                None if is_missing else 0.001
            ),
        })

    pairs = prepare_pairs(
        pd.DataFrame(features),
        pd.DataFrame(labels),
    )

    splits = chronological_splits(pairs)

    cutoff = (
        pairs["label_available_at_utc"].max()
        + pd.Timedelta(minutes=1)
    )

    return pairs, splits, cutoff


def test_gap_attribution_matches_actual_splits():
    pairs, splits, cutoff = make_data()

    report = build_gap_attribution(
        pairs,
        splits,
        evaluation_cutoff_utc=cutoff,
    )

    assert report["item_id"] == 4151

    by_name = {
        entry["partition"]: entry
        for entry in report["partitions"]
    }

    assert by_name["train"]["eligible"] == len(
        splits.train
    )

    assert by_name["validation"]["eligible"] == len(
        splits.validation
    )

    assert by_name["test"]["eligible"] == len(
        splits.test
    )


def test_feature_exclusions_explain_long_gap():
    pairs, splits, cutoff = make_data(
        feature_exclusions=range(20, 29),
    )

    report = build_gap_attribution(
        pairs,
        splits,
        evaluation_cutoff_utc=cutoff,
    )

    train_gaps = [
        gap
        for gap in report["long_gaps"]
        if gap["partition"] == "train"
    ]

    assert train_gaps

    assert any(
        gap["primary_reasons"].get(
            "feature_ineligible", 0
        ) == 9
        for gap in train_gaps
    )


def test_missing_labels_are_attributed():
    pairs, splits, cutoff = make_data(
        missing_labels=range(20, 29),
    )

    report = build_gap_attribution(
        pairs,
        splits,
        evaluation_cutoff_utc=cutoff,
    )

    train_gaps = [
        gap
        for gap in report["long_gaps"]
        if gap["partition"] == "train"
    ]

    assert any(
        gap["primary_reasons"].get(
            "label_missing", 0
        ) == 9
        for gap in train_gaps
    )


def test_auditor_rejects_split_mismatch():
    pairs, splits, cutoff = make_data()

    # Deliberately remove a legitimate training
    # example from the splitter's output.
    altered = replace(
        splits,
        train=splits.train.iloc[1:].copy(),
    )

    with pytest.raises(
        ValueError,
        match="Attribution/split mismatch",
    ):
        build_gap_attribution(
            pairs,
            altered,
            evaluation_cutoff_utc=cutoff,
        )
