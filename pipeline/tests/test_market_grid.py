
import pandas as pd

from osrs_market_ml.features.market_grid import (
    build_market_grid,
)


def test_market_grid_handles_mixed_datetime_precision():

    history = pd.DataFrame({
        "Id": [1],
        "MarketItemId": [4151],
        "CollectionBatchId": [1],
        "InstantBuyPrice": [1000000],
        "InstantSellPrice": [990000],
        "InstantBuyTimeUtc": [
            "2026-11-09 12:00:00.0000000"
        ],
        "InstantSellTimeUtc": [
            "2026-11-09 12:00:00.0000000"
        ],
        "CollectedAtUtc": [
            "2026-11-09 12:00:01.1234567"
        ],
        "CompletedAtUtc": [
            "2026-11-09 12:00:02.1234567"
        ],
        "AvailableAtUtc": [
            "2026-11-09 12:00:02.1234567"
        ],
    })

    for column in [
        "InstantBuyTimeUtc",
        "InstantSellTimeUtc",
        "CollectedAtUtc",
        "CompletedAtUtc",
        "AvailableAtUtc",
    ]:
        history[column] = pd.to_datetime(
            history[column],
            utc=True,
        )

    grid = build_market_grid(
        history,
        item_id=4151,
        start_utc=pd.Timestamp(
            "2026-11-09T12:00:00Z"
        ),
        end_utc=pd.Timestamp(
            "2026-11-09T12:10:00Z"
        ),
    )

    assert len(grid) == 3

    # Snapshot was not available at 12:00.
    assert not grid.iloc[0]["HasKnownState"]

    # Snapshot became available before 12:05.
    assert grid.iloc[1]["HasKnownState"]

    assert grid.iloc[1]["ReferencePrice"] == 995000

    assert grid.iloc[1]["HasValidReference"]
