
"""
Reconstruct a five-minute market-state timeline.

No future observations are allowed to influence
an earlier prediction time.
"""

from __future__ import annotations

import pandas as pd


def build_market_grid(
    history: pd.DataFrame,
    *,
    item_id: int,
    start_utc: pd.Timestamp,
    end_utc: pd.Timestamp,
    frequency: str = "5min",
    maximum_quote_age: str = "30min",
) -> pd.DataFrame:

    start = pd.Timestamp(start_utc)
    end = pd.Timestamp(end_utc)

    if start.tzinfo is None or end.tzinfo is None:
        raise ValueError("Grid timestamps must be UTC-aware")

    start = start.tz_convert("UTC")
    end = end.tz_convert("UTC")

    if start >= end:
        raise ValueError("Invalid grid time window")

    states = history.loc[
        history["MarketItemId"] == item_id
    ].copy()

    states = states.dropna(
        subset=["AvailableAtUtc"]
    )

    # Normalize observation timestamps to nanoseconds.
    # Pandas merge_asof requires identical datetime dtypes.
    states["AvailableAtUtc"] = pd.to_datetime(
        states["AvailableAtUtc"],
        utc=True,
    ).dt.as_unit("ns")

    states = states.sort_values(
        ["AvailableAtUtc", "Id"]
    )

    # A regular timeline of prediction decision points.
    timeline = pd.DataFrame({
        "PredictionTimeUtc": pd.date_range(
            start=start.ceil(frequency),
            end=end.floor(frequency),
            freq=frequency,
            tz="UTC",
        ).as_unit("ns")
    })


    if timeline.empty:
        return timeline

    if states.empty:
        timeline["MarketItemId"] = item_id
        timeline["HasKnownState"] = False
        return timeline

    # Select the latest snapshot actually available
    # at each historical decision point.
    grid = pd.merge_asof(
        timeline,
        states,
        left_on="PredictionTimeUtc",
        right_on="AvailableAtUtc",
        direction="backward",
        allow_exact_matches=True,
    )

    grid["MarketItemId"] = item_id

    grid["HasKnownState"] = grid["Id"].notna()

    # Time since the collector made this snapshot
    # available. This is NOT market quote age.
    grid["StateAgeMinutes"] = (
        grid["PredictionTimeUtc"]
        - grid["AvailableAtUtc"]
    ).dt.total_seconds() / 60.0

    max_age = pd.Timedelta(maximum_quote_age)

    for side in ("Buy", "Sell"):

        source_time = f"Instant{side}TimeUtc"
        price_column = f"Instant{side}Price"

        age_column = f"{side}QuoteAgeMinutes"
        fresh_column = f"{side}QuoteFresh"

        age = (
            grid["PredictionTimeUtc"]
            - grid[source_time]
        )

        grid[age_column] = (
            age.dt.total_seconds() / 60.0
        )

        grid[fresh_column] = (
            grid[price_column].gt(0)
            & age.ge(pd.Timedelta(0))
            & age.le(max_age)
        ).fillna(False)

        # Preserve raw source observations,
        # but don't use stale or future-dated
        # quotes in our initial price reference.
        grid[f"Valid{side}Price"] = (
            grid[price_column].where(
                grid[fresh_column]
            )
        )

    both_fresh = (
        grid["BuyQuoteFresh"]
        & grid["SellQuoteFresh"]
    )

    
    # --------------------------------------------------
    # Last-known market reference
    # --------------------------------------------------
    # This is a historical state reference, not a
    # claim that these prices are currently tradable.

    known_buy = grid["InstantBuyPrice"]
    known_sell = grid["InstantSellPrice"]

    both_known = (
        known_buy.gt(0)
        & known_sell.gt(0)
        & grid["HasKnownState"]
    )

    grid["LastKnownReferencePrice"] = (
        (known_buy + known_sell) / 2.0
    ).where(both_known)

    # Preserve the existing strict freshness behavior.
    # This value is only present when both quote sides
    # meet our configured freshness threshold.
    grid["FreshReferencePrice"] = (
        (
            grid["ValidBuyPrice"]
            + grid["ValidSellPrice"]
        ) / 2.0
    ).where(both_fresh)


    buy = grid["ValidBuyPrice"]
    sell = grid["ValidSellPrice"]

    # An observed price reference, NOT an
    # executable bid/ask or guaranteed fill.
    grid["ReferencePrice"] = (
        buy + sell
    ) / 2.0

    grid["ObservedGapGp"] = (
        buy - sell
    ).where(both_fresh)

    grid["ObservedGapPct"] = (
        100.0
        * grid["ObservedGapGp"]
        / grid["ReferencePrice"]
    )

    grid["HasValidReference"] = (
        both_fresh
        & grid["ReferencePrice"].gt(0)
    )

    return grid
