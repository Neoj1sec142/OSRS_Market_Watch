
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

import pandas as pd

from osrs_market_ml.features.point_in_time import (
    build_grid,
)
from osrs_market_ml.features.feature_engine import (
    build_features,
)
from osrs_market_ml.targets.forward_returns import (
    build_labels,
)


UTC = timezone.utc
START = datetime(2026, 11, 11, 12, 1, tzinfo=UTC)


@dataclass(frozen=True)
class SyntheticObservation:
    source_row_id: int
    available_at_utc: datetime
    instant_buy_price_gp: int
    instant_sell_price_gp: int
    instant_buy_time_utc: datetime
    instant_sell_time_utc: datetime


@dataclass(frozen=True)
class SyntheticDataset:
    item_id: int
    observations: tuple[SyntheticObservation, ...]


def make_dataset(count=60):
    observations = []

    for i in range(count):
        available = START + timedelta(minutes=5 * i)
        quote_time = available - timedelta(seconds=1)

        observations.append(
            SyntheticObservation(
                source_row_id=i + 1,
                available_at_utc=available,
                instant_buy_price_gp=1000 + i,
                instant_sell_price_gp=900 + i,
                instant_buy_time_utc=quote_time,
                instant_sell_time_utc=quote_time,
            )
        )

    return SyntheticDataset(
        item_id=4151,
        observations=tuple(observations),
    )


def test_grid_never_uses_future_snapshots():
    dataset = make_dataset(3)
    grid = build_grid(dataset)

    # First available snapshot is at 12:01.
    # First decision is 12:05.
    first = grid.iloc[0]

    assert first["as_of_utc"] == pd.Timestamp(
        "2026-11-11T12:05:00Z"
    )

    assert first["snapshot_id"] == 1


def test_future_observations_cannot_change_old_features():
    original = make_dataset(40)
    extended = make_dataset(60)

    first = build_features(build_grid(original))
    second = build_features(build_grid(extended))

    cutoff = first["as_of_utc"].max()

    second = second.loc[
        second["as_of_utc"] <= cutoff
    ].reset_index(drop=True)

    pd.testing.assert_frame_equal(
        first.reset_index(drop=True),
        second,
    )


def test_stale_quotes_do_not_generate_reference():
    dataset = make_dataset(1)

    old = dataset.observations[0]

    # Introduce a later snapshot with no newer
    # underlying market transactions.
    late = SyntheticObservation(
        source_row_id=2,
        available_at_utc=(
            old.available_at_utc + timedelta(hours=1)
        ),
        instant_buy_price_gp=old.instant_buy_price_gp,
        instant_sell_price_gp=old.instant_sell_price_gp,
        instant_buy_time_utc=old.instant_buy_time_utc,
        instant_sell_time_utc=old.instant_sell_time_utc,
    )

    dataset = SyntheticDataset(
        item_id=4151,
        observations=(old, late),
    )

    grid = build_grid(dataset)

    last = grid.iloc[-1]

    assert not last["fresh_pair"]
    assert pd.isna(last["reference_price_gp"])


def test_future_return_label_is_generated():
    dataset = make_dataset(60)
    grid = build_grid(dataset)
    labels = build_labels(dataset, grid)

    valid = labels[
        (labels["status"] == "valid")
        & (labels["horizon_minutes"] == 30)
    ]

    assert not valid.empty

    for row in valid.itertuples():
        assert row.target_observation_time_utc >= (
            row.target_time_utc
        )

        assert row.label_available_at_utc >= (
            row.target_observation_time_utc
        )

        assert row.label_available_at_utc > row.as_of_utc


def test_end_of_dataset_labels_are_censored():
    dataset = make_dataset(40)
    grid = build_grid(dataset)

    labels = build_labels(dataset, grid)

    final_time = grid["as_of_utc"].max()

    final_labels = labels.loc[
        labels["as_of_utc"] == final_time
    ]

    assert set(final_labels["status"]) == {
        "censored"
    }


def test_feature_engine_requires_history():
    dataset = make_dataset(5)
    grid = build_grid(dataset)
    features = build_features(grid)

    assert not features["feature_eligible"].any()

    assert features["return_2h"].isna().all()
