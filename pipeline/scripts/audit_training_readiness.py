
"""
OSRS Market Watch - Phase 6B Training Readiness Audit.

Reads an existing research run.
Validates feature/label alignment and maturity.

No model training.
No writes to source data.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
import pandas as pd

from osrs_market_ml.data.canonical_loader import sha256_file


def load_run(run_dir: Path):
    manifest = json.loads(
        (run_dir / "manifest.json").read_text(
            encoding="utf-8"
        )
    )

    if manifest["pipeline_version"] != "phase6b-v1":
        raise ValueError("Unsupported pipeline version")

    frames = {}

    for name in ("features", "labels"):
        filename = f"{name}.csv"

        actual_hash = sha256_file(
            run_dir / filename
        )

        if actual_hash != manifest["files"][filename]:
            raise ValueError(
                f"{filename} checksum mismatch"
            )

        frames[name] = pd.read_csv(
            run_dir / filename
        )

        if len(frames[name]) != manifest["row_counts"][name]:
            raise ValueError(
                f"{filename} row-count mismatch"
            )

    return manifest, frames["features"], frames["labels"]


def audit(run_dir: Path) -> None:
    manifest, features, labels = load_run(run_dir)

    time_columns = (
        "as_of_utc",
        "target_time_utc",
        "target_observation_time_utc",
        "label_available_at_utc",
    )

    features["as_of_utc"] = pd.to_datetime(
        features["as_of_utc"],
        utc=True,
    )

    for column in time_columns:
        if column in labels:
            labels[column] = pd.to_datetime(
                labels[column],
                utc=True,
            )

    feature_key = ["item_id", "as_of_utc"]
    label_key = [
        "item_id",
        "as_of_utc",
        "horizon_minutes",
    ]

    if features.duplicated(feature_key).any():
        raise ValueError("Duplicate feature identity")

    if labels.duplicated(label_key).any():
        raise ValueError("Duplicate label identity")

    # Every label must reference one existing feature row.
    joined = labels.merge(
        features,
        on=feature_key,
        how="left",
        validate="many_to_one",
        indicator=True,
    )

    if not joined["_merge"].eq("both").all():
        raise ValueError(
            "Labels without matching feature rows"
        )

    expected_target = (
        joined["as_of_utc"]
        + pd.to_timedelta(
            joined["horizon_minutes"],
            unit="min",
        )
    )

    if not (
        joined["target_time_utc"] == expected_target
    ).all():
        raise ValueError("Incorrect label horizons")

    valid = joined["status"].eq("valid")

    tolerance = pd.Timedelta(
        manifest["label_tolerance"]
    )

    v = joined.loc[valid]

    if v[
        [
            "forward_log_return",
            "target_observation_time_utc",
            "label_available_at_utc",
        ]
    ].isna().any().any():
        raise ValueError("Incomplete valid labels")

    if not np.isfinite(
        v["forward_log_return"]
    ).all():
        raise ValueError("Nonfinite forward returns")

    if not (
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
    ).all():
        raise ValueError(
            "Future label violates temporal contract"
        )

    invalid = joined.loc[~valid]

    if invalid["forward_log_return"].notna().any():
        raise ValueError(
            "Invalid labels contain target returns"
        )

    # The frozen research artifact is the training
    # cutoff for this first audit, not the current clock.
    #
    # This assumes the frozen export represents a
    # complete observation set up to that cutoff.
    cutoff = joined["as_of_utc"].max()

    joined["training_eligible"] = (
        joined["feature_eligible"].astype(bool)
        & valid
        & joined["label_available_at_utc"].le(cutoff)
    )

    print("\n=== NUGGET CAPITAL ===")
    print("Phase 6B - Training Readiness Audit\n")

    print("Research run:", manifest["run_sha256"])
    print("Item:", manifest["item_id"])
    print("Training cutoff (provisional):", cutoff)

    for horizon, group in joined.groupby(
        "horizon_minutes"
    ):
        eligible = group["training_eligible"]

        print(f"\nHorizon: {horizon} minutes")
        print("  Decision rows:", len(group))
        print(
            "  Valid labels:",
            int(group["status"].eq("valid").sum()),
        )
        print(
            "  Eligible features:",
            int(group["feature_eligible"].sum()),
        )
        print(
            "  Eligible training pairs:",
            int(eligible.sum()),
        )

        ready = group.loc[eligible]

        if not ready.empty:
            print(
                "  First eligible:",
                ready["as_of_utc"].min(),
            )
            print(
                "  Last eligible:",
                ready["as_of_utc"].max(),
            )
            print(
                "  Mean forward return:",
                round(
                    ready["forward_log_return"].mean(),
                    8,
                ),
            )

    print("\nTRAINING READINESS AUDIT PASSED")


def main():
    parser = argparse.ArgumentParser()

    parser.add_argument(
        "--run",
        type=Path,
        required=True,
    )

    args = parser.parse_args()
    audit(args.run)


if __name__ == "__main__":
    main()
