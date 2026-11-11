
from datetime import datetime, timedelta, timezone
from decimal import Decimal

import pytest

from osrs_market_ml.data.research_contracts import (
    LabelStatus,
    MarketAggregateV1,
    MarketFeatureV1,
    MarketLabelV1,
    MarketObservationV1,
    ObservationKind,
    ResearchSource,
)


UTC = timezone.utc
T0 = datetime(2026, 11, 11, 12, 0, tzinfo=UTC)
HASH = "a" * 64


@pytest.fixture
def source():
    return ResearchSource(
        artifact_sha256=HASH,
        source_label="test-frozen-backup",
    )


def make_observation(source):
    return MarketObservationV1(
        source=source,
        source_row_id=10,
        item_id=4151,
        collection_batch_id=2,
        instant_buy_price_gp=680000,
        instant_sell_price_gp=660000,
        instant_buy_time_utc=T0,
        instant_sell_time_utc=T0,
        collected_at_utc=T0 + timedelta(seconds=10),
        batch_completed_at_utc=T0 + timedelta(seconds=20),
        raw_collected_at_utc="2026-11-11 12:00:10",
    )


def test_observation_not_visible_before_batch_completion(source):
    observation = make_observation(source)

    assert not observation.known_at(
        T0 + timedelta(seconds=15)
    )

    assert observation.known_at(
        T0 + timedelta(seconds=20)
    )


def test_observation_requires_utc(source):
    observation = make_observation(source)

    with pytest.raises(ValueError):
        observation.known_at(
            datetime(2026, 11, 11, 12, 5)
        )


def test_aggregate_preserves_resolution(source):
    aggregate = MarketAggregateV1(
        source=source,
        source_row_id=7,
        item_id=4151,
        interval_minutes=5,
        bucket_start_utc=T0,
        collected_at_utc=T0 + timedelta(minutes=6),
        avg_instant_buy_gp=Decimal("680000.125000"),
        avg_instant_sell_gp=Decimal("660000.500000"),
        instant_buy_volume=100,
        instant_sell_volume=90,
        raw_bucket_start_utc="2026-11-11 12:00:00",
    )

    assert aggregate.kind == ObservationKind.AGGREGATE_5M
    assert aggregate.bucket_end_utc == (
        T0 + timedelta(minutes=5)
    )

    assert aggregate.avg_instant_buy_gp == Decimal(
        "680000.125000"
    )


def test_feature_rejects_nonfinite_values():
    with pytest.raises(ValueError):
        MarketFeatureV1(
            item_id=4151,
            as_of_utc=T0,
            source_dataset_sha256=HASH,
            feature_version="features-v1",
            return_30m=float("nan"),
        )


def test_label_horizon_must_match():
    with pytest.raises(ValueError):
        MarketLabelV1(
            item_id=4151,
            as_of_utc=T0,
            horizon_minutes=30,
            target_time_utc=T0 + timedelta(hours=2),
            target_version="labels-v1",
            status=LabelStatus.MISSING,
        )


def test_label_cannot_be_known_at_prediction_time():
    with pytest.raises(ValueError):
        MarketLabelV1(
            item_id=4151,
            as_of_utc=T0,
            horizon_minutes=30,
            target_time_utc=T0 + timedelta(minutes=30),
            target_version="labels-v1",
            status=LabelStatus.VALID,
            forward_log_return=0.012,
            target_observation_time_utc=T0,
            label_available_at_utc=T0,
        )


def test_valid_future_label():
    label = MarketLabelV1(
        item_id=4151,
        as_of_utc=T0,
        horizon_minutes=30,
        target_time_utc=T0 + timedelta(minutes=30),
        target_version="labels-v1",
        status=LabelStatus.VALID,
        forward_log_return=0.012,
        target_observation_time_utc=(
            T0 + timedelta(minutes=30)
        ),
        label_available_at_utc=(
            T0 + timedelta(minutes=31)
        ),
    )

    assert label.status == LabelStatus.VALID
    assert label.forward_log_return == 0.012
