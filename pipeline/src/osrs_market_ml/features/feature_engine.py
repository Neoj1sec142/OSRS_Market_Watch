
"""
Phase 6B - Price-based feature engineering.

Input:
    A causal five-minute market-state grid.

Output:
    Versionable feature DataFrame.

No target labels or future outcomes are accessed.
"""

from __future__ import annotations

import numpy as np
import pandas as pd


def build_features(
    grid: pd.DataFrame,
) -> pd.DataFrame:

    features = grid[
        [
            "item_id",
            "as_of_utc",
            "reference_price_gp",
            "observed_gap_pct",
            "buy_quote_age_seconds",
            "sell_quote_age_seconds",
            "observation_count_2h",
            "fresh_pair",
        ]
    ].copy()

    p = features["reference_price_gp"]

    # 5-minute grid: 6 rows = 30 minutes;
    # 24 rows = 2 hours.
    #
    # Missing/stale endpoints produce NaN,
    # not an invented zero return.
    for steps, name in (
        (6, "30m"),
        (24, "2h"),
    ):
        previous = p.shift(steps)

        features[f"return_{name}"] = np.log(
            p / previous
        )

    # One-step price changes. We don't invent
    # changes where a valid reference is absent.
    r5 = np.log(p / p.shift(1))

    # At least 12 valid returns in the last
    # 24 intervals before publishing volatility.
    features["volatility_2h"] = (
        r5.rolling(
            window=24,
            min_periods=12,
        ).std()
    )

    features["feature_eligible"] = (
        features["fresh_pair"]
        & features["return_30m"].notna()
        & features["return_2h"].notna()
        & features["volatility_2h"].notna()
    )

    return features
