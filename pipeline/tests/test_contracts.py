from dataclasses import replace
from datetime import datetime, timedelta, timezone
from decimal import Decimal
import pytest

from osrs_market_ml.data.contracts import (
    AggregateInterval, MarketPriceAggregate, MarketState,
)

T = datetime(2026, 11, 9, 12, 0, tzinfo=timezone.utc)


def test_market_state_rejects_future_collected_data():
    kwargs = dict(market_item_id=4151, as_of_utc=T, source_snapshot_id=1,
                  source_collected_at_utc=T + timedelta(minutes=1),
                  instant_buy_price=1_000_000, instant_sell_price=990_000,
                  instant_buy_time_utc=T - timedelta(minutes=2),
                  instant_sell_time_utc=T - timedelta(minutes=3))
    with pytest.raises(ValueError, match="not yet available"):
        MarketState(**kwargs)


def test_quote_age_uses_exchange_side_timestamp():
    state = MarketState(4151, T, 1, T - timedelta(minutes=1),
                        1_000_000, 990_000, T - timedelta(minutes=9),
                        T - timedelta(minutes=15))
    assert state.buy_quote_age == timedelta(minutes=9)
    assert state.sell_quote_age == timedelta(minutes=15)


def test_aggregate_bucket_end():
    aggregate = MarketPriceAggregate(1, 4151, AggregateInterval.FIVE_MINUTES,
                                     T, Decimal("100.5"), Decimal("98.5"),
                                     100, 200, T + timedelta(minutes=6))
    assert aggregate.bucket_end_utc == T + timedelta(minutes=5)


def test_rejects_naive_datetime():
    with pytest.raises(ValueError, match="UTC"):
        MarketState(4151, T.replace(tzinfo=None), 1, T, 10, 9, T, T)
