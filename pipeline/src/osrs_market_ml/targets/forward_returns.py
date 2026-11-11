
"""
Phase 6B - Forward-return label generation.

Uses future observations ONLY for offline targets.

A valid target requires:
- A valid decision-time reference price.
- Both future quote timestamps >= target time.
- A completed observation available within tolerance.
- Both future quotes passing freshness checks.

No assumptions about executable GE orders.
"""

from __future__ import annotations

import math

import pandas as pd


TARGET_VERSION = "forward-return-v1"


def build_labels(
    dataset,
    grid: pd.DataFrame,
    *,
    horizons: tuple[int, ...] = (30, 120),
    tolerance: str = "10min",
    max_quote_age: str = "30min",
) -> pd.DataFrame:

    candidates = []

    for o in dataset.observations:
        if (
            o.instant_buy_price_gp is None
            or o.instant_sell_price_gp is None
            or o.instant_buy_time_utc is None
            or o.instant_sell_time_utc is None
        ):
            continue

        if (
            o.instant_buy_price_gp <= 0
            or o.instant_sell_price_gp <= 0
        ):
            continue

        candidates.append(
            (
                pd.Timestamp(o.available_at_utc),
                pd.Timestamp(o.instant_buy_time_utc),
                pd.Timestamp(o.instant_sell_time_utc),
                (
                    o.instant_buy_price_gp
                    + o.instant_sell_price_gp
                ) / 2.0,
            )
        )

    candidates.sort(key=lambda x: x[0])

    last_available = max(
        pd.Timestamp(o.available_at_utc)
        for o in dataset.observations
    )

    tol = pd.Timedelta(tolerance)
    max_age = pd.Timedelta(max_quote_age)

    rows = []

    for record in grid.itertuples(index=False):
        decision = pd.Timestamp(record.as_of_utc)
        reference = record.reference_price_gp

        for horizon in horizons:
            target = decision + pd.Timedelta(
                minutes=horizon
            )

            status = "missing"
            forward_return = None
            observation_time = None
            available_time = None

            if not math.isfinite(reference):
                status = "unavailable"

            else:
                for available, buy_time, sell_time, price in candidates:

                    if available < target:
                        continue

                    if available > target + tol:
                        break

                    # Both underlying transactions must
                    # have occurred at or after the horizon.
                    if (
                        buy_time >= target
                        and sell_time >= target
                        and buy_time <= available
                        and sell_time <= available
                        and available - buy_time <= max_age
                        and available - sell_time <= max_age
                    ):
                        forward_return = math.log(
                            price / reference
                        )

                        status = "valid"
                        available_time = available

                        observation_time = max(
                            buy_time, sell_time
                        )

                        break

                # The extraction ended before the entire
                # allowed label window was observable.
                if (
                    status != "valid"
                    and last_available < target + tol
                ):
                    status = "censored"

            rows.append({
                "item_id": dataset.item_id,
                "as_of_utc": decision,
                "horizon_minutes": horizon,
                "target_time_utc": target,
                "target_version": TARGET_VERSION,
                "status": status,
                "forward_log_return": forward_return,
                "target_observation_time_utc": observation_time,
                "label_available_at_utc": available_time,
            })

    return pd.DataFrame.from_records(rows)
