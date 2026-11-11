
"""
Phase 6C - Chronological research evaluation.

Single purged train/validation/test holdout.

This module never trains a model and never
treats GE observations as executable trades.
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np
import pandas as pd


KEY = ["item_id", "as_of_utc"]


@dataclass(frozen=True)
class TemporalSplits:
    train: pd.DataFrame
    validation: pd.DataFrame
    test: pd.DataFrame

    validation_start_utc: pd.Timestamp
    test_start_utc: pd.Timestamp


def prepare_pairs(
    features: pd.DataFrame,
    labels: pd.DataFrame,
    *,
    label_tolerance: str = "10min",
) -> pd.DataFrame:
    """
    Validate and join feature/label records.

    One row per:
        (item_id, as_of_utc, horizon_minutes)
    """

    features = features.copy()
    labels = labels.copy()

    features["as_of_utc"] = pd.to_datetime(
        features["as_of_utc"], utc=True
    ).dt.as_unit("ns")

    for column in (
        "as_of_utc",
        "target_time_utc",
        "target_observation_time_utc",
        "label_available_at_utc",
    ):
        labels[column] = pd.to_datetime(
            labels[column], utc=True
        ).dt.as_unit("ns")

    if features.duplicated(KEY).any():
        raise ValueError("Duplicate feature identity")

    label_key = KEY + ["horizon_minutes"]

    if labels.duplicated(label_key).any():
        raise ValueError("Duplicate label identity")

    if features["feature_eligible"].isna().any():
        raise ValueError("Missing feature eligibility")

    # Never let CSV string values such as "False"
    # become truthy through bool("False").
    def parse_eligible(value):
        if isinstance(value, (bool, np.bool_)):
            return bool(value)

        if isinstance(value, str):
            if value.lower() == "true":
                return True
            if value.lower() == "false":
                return False

        raise ValueError(
            f"Invalid feature eligibility: {value!r}"
        )

    features["feature_eligible"] = (
        features["feature_eligible"]
        .map(parse_eligible)
        .astype(bool)
    )

    pairs = labels.merge(
        features,
        on=KEY,
        how="left",
        validate="many_to_one",
        indicator=True,
    )

    if not pairs["_merge"].eq("both").all():
        raise ValueError("Unmatched feature/label rows")

    pairs = pairs.drop(columns="_merge")

    expected_target = (
        pairs["as_of_utc"]
        + pd.to_timedelta(
            pairs["horizon_minutes"], unit="min"
        )
    )

    if not pairs["target_time_utc"].eq(
        expected_target
    ).all():
        raise ValueError("Invalid target horizon")

    valid = pairs["status"].eq("valid")
    v = pairs.loc[valid]

    required = [
        "forward_log_return",
        "target_observation_time_utc",
        "label_available_at_utc",
    ]

    if v[required].isna().any().any():
        raise ValueError("Incomplete valid labels")

    if not np.isfinite(
        v["forward_log_return"].to_numpy(
            dtype=float
        )
    ).all():
        raise ValueError("Nonfinite labels")

    tolerance = pd.Timedelta(label_tolerance)

    temporal_valid = (
        (
            v["target_observation_time_utc"]
            >= v["target_time_utc"]
        )
        & (
            v["target_observation_time_utc"]
            <= v["label_available_at_utc"]
        )
        & (
            v["label_available_at_utc"]
            >= v["target_time_utc"]
        )
        & (
            v["label_available_at_utc"]
            <= v["target_time_utc"] + tolerance
        )
        & (
            v["label_available_at_utc"]
            > v["as_of_utc"]
        )
    )

    if not temporal_valid.all():
        raise ValueError(
            "Label violates temporal availability"
        )

    invalid = pairs.loc[~valid]

    if invalid["forward_log_return"].notna().any():
        raise ValueError(
            "Non-valid labels contain returns"
        )

    pairs["candidate_eligible"] = (
        pairs["feature_eligible"] & valid
    )

    return pairs.sort_values(
        ["as_of_utc", "item_id", "horizon_minutes"]
    ).reset_index(drop=True)


def chronological_splits(
    pairs: pd.DataFrame,
    *,
    train_fraction: float = 0.60,
    validation_fraction: float = 0.20,
) -> TemporalSplits:
    """
    Split on distinct decision timestamps.

    Train labels must mature before validation.
    Validation labels must mature before test.
    """

    if not (
        0 < train_fraction < 1
        and 0 < validation_fraction < 1
        and train_fraction + validation_fraction < 1
    ):
        raise ValueError("Invalid split fractions")

    times = pd.DatetimeIndex(
        pairs["as_of_utc"].drop_duplicates().sort_values()
    )

    if len(times) < 15:
        raise ValueError(
            "Insufficient decision timestamps"
        )

    train_end = int(len(times) * train_fraction)
    validation_end = int(
        len(times) * (
            train_fraction + validation_fraction
        )
    )

    if not 0 < train_end < validation_end < len(times):
        raise ValueError("Empty chronological split")

    validation_start = times[train_end]
    test_start = times[validation_end]

    train = pairs.loc[
        (pairs["as_of_utc"] < validation_start)
        & pairs["candidate_eligible"]
        & (
            pairs["label_available_at_utc"]
            < validation_start
        )
        & (
            pairs["target_time_utc"]
            < validation_start
        )
    ].copy()

    validation = pairs.loc[
        (pairs["as_of_utc"] >= validation_start)
        & (pairs["as_of_utc"] < test_start)
        & pairs["candidate_eligible"]
        & (pairs["label_available_at_utc"] < test_start)
        & (pairs["target_time_utc"] < test_start)
    ].copy()

    # Test outcomes are used only to score predictions.
    # They must never be used for model selection.
    test = pairs.loc[
        (pairs["as_of_utc"] >= test_start)
        & pairs["candidate_eligible"]
    ].copy()

    return TemporalSplits(
        train=train,
        validation=validation,
        test=test,
        validation_start_utc=validation_start,
        test_start_utc=test_start,
    )


def evaluate_persistence(
    frame: pd.DataFrame,
) -> dict:
    """
    Persistence baseline:
        predicted log return = 0

    This evaluates price prediction error,
    NOT trading profitability.
    """

    if frame.empty:
        return {
            "samples": 0,
            "mae": None,
            "rmse": None,
            "bias": None,
            "mean_actual_return": None,
        }

    actual = frame[
        "forward_log_return"
    ].to_numpy(dtype=float)

    if not np.isfinite(actual).all():
        raise ValueError("Invalid evaluation returns")

    predicted = np.zeros_like(actual)
    errors = actual - predicted

    return {
        "samples": int(len(actual)),
        "mae": float(np.mean(np.abs(errors))),
        "rmse": float(
            np.sqrt(np.mean(np.square(errors)))
        ),
        "bias": float(np.mean(errors)),
        "mean_actual_return": float(
            np.mean(actual)
        ),
    }
