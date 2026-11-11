
"""
Phase 6B - Point-in-time market state reconstruction.

Consumes a validated CanonicalDataset.
Does not use aggregate data or future observations.
"""

from __future__ import annotations

import pandas as pd


FEATURE_VERSION = "features-pit-v1"


def build_grid(
    dataset,
    *,
    frequency: str = "5min",
    max_quote_age: str = "30min",
) -> pd.DataFrame:

    records = [
        {
            "snapshot_id": o.source_row_id,
            "available_at_utc": o.available_at_utc,
            "buy_price_gp": o.instant_buy_price_gp,
            "sell_price_gp": o.instant_sell_price_gp,
            "buy_time_utc": o.instant_buy_time_utc,
            "sell_time_utc": o.instant_sell_time_utc,
        }
        for o in dataset.observations
    ]

    if not records:
        raise ValueError("No completed snapshots")

    events = pd.DataFrame.from_records(records)

    # Normalize the merge keys to identical precision.
    for col in (
        "available_at_utc",
        "buy_time_utc",
        "sell_time_utc",
    ):
        events[col] = (
            pd.to_datetime(events[col], utc=True)
            .dt.as_unit("ns")
        )

    events = events.sort_values(
        ["available_at_utc", "snapshot_id"]
    ).reset_index(drop=True)

    start = events["available_at_utc"].min().ceil(frequency)
    end = events["available_at_utc"].max().floor(frequency)

    timeline = pd.DataFrame({
        "as_of_utc": pd.date_range(
            start, end, freq=frequency
        ).as_unit("ns")
    })

    if timeline.empty:
        raise ValueError("No complete decision intervals")

    # Backward-only join: never expose future snapshots.
    grid = pd.merge_asof(
        timeline,
        events,
        left_on="as_of_utc",
        right_on="available_at_utc",
        direction="backward",
    )

    age_limit = pd.Timedelta(max_quote_age)

    for side in ("buy", "sell"):
        age = (
            grid["as_of_utc"]
            - grid[f"{side}_time_utc"]
        )

        grid[f"{side}_quote_age_seconds"] = (
            age.dt.total_seconds()
        )

        grid[f"{side}_fresh"] = (
            grid[f"{side}_price_gp"].gt(0)
            & age.ge(pd.Timedelta(0))
            & age.le(age_limit)
        ).fillna(False)

    grid["fresh_pair"] = (
        grid["buy_fresh"]
        & grid["sell_fresh"]
    )

    grid["reference_price_gp"] = (
        (
            grid["buy_price_gp"]
            + grid["sell_price_gp"]
        ) / 2.0
    ).where(grid["fresh_pair"])

    grid["observed_gap_pct"] = (
        100.0
        * (
            grid["buy_price_gp"]
            - grid["sell_price_gp"]
        )
        / grid["reference_price_gp"]
    ).where(grid["fresh_pair"])

    # Count actual recorded snapshots, not
    # carried-forward five-minute grid rows.
    times = pd.DatetimeIndex(
        events["available_at_utc"]
    )

    asof = pd.DatetimeIndex(grid["as_of_utc"])

    grid["observation_count_2h"] = (
        times.searchsorted(asof, side="right")
        - times.searchsorted(
            asof - pd.Timedelta("2h"),
            side="right",
        )
    ).astype("int64")

    grid["item_id"] = dataset.item_id

    return grid
