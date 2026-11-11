
"""
Phase 6C - Persistence baseline evaluation.

Input: Phase 6B feature/label research run.
Output: New, versioned evaluation report.

No training, trading, or production database access.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import pandas as pd

from osrs_market_ml.data.canonical_loader import (
    sha256_file,
)
from osrs_market_ml.evaluation.walk_forward import (
    chronological_splits,
    evaluate_persistence,
    prepare_pairs,
)
from osrs_market_ml.evaluation.split_quality import (
    SplitQualityPolicy,
    validate_split_quality,
)

EVALUATION_VERSION = "persistence-holdout-v3"


def load_verified_run(run_dir: Path):
    manifest = json.loads(
        (run_dir / "manifest.json").read_text(
            encoding="utf-8"
        )
    )

    if manifest.get("pipeline_version") != "phase6b-v1":
        raise ValueError("Unsupported research version")

    # Verify the existing semantic run fingerprint.
    original = {
        key: value
        for key, value in manifest.items()
        if key != "run_sha256"
    }

    canonical = json.dumps(
        original,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")

    expected_run = hashlib.sha256(
        canonical
    ).hexdigest()

    if expected_run != manifest["run_sha256"]:
        raise ValueError("Research run fingerprint mismatch")

    for filename in (
        "market_grid.csv",
        "features.csv",
        "labels.csv",
    ):
        actual = sha256_file(run_dir / filename)

        if actual != manifest["files"][filename]:
            raise ValueError(
                f"{filename} checksum mismatch"
            )

    features = pd.read_csv(
        run_dir / "features.csv"
    )

    labels = pd.read_csv(
        run_dir / "labels.csv"
    )

    if (
        len(features) != manifest["row_counts"]["features"]
        or len(labels) != manifest["row_counts"]["labels"]
    ):
        raise ValueError("Research row count mismatch")

    return manifest, features, labels


def main():
    parser = argparse.ArgumentParser()

    parser.add_argument(
        "--run", type=Path, required=True
    )

    parser.add_argument(
        "--output", type=Path, required=True
    )

    parser.add_argument(
        "--evaluation-cutoff-utc",
        required=True,
        help=(
            "Latest UTC time through which the frozen "
            "source is accepted for outcome evaluation."
        ),
    )

    args = parser.parse_args()

    manifest, features, labels = load_verified_run(
        args.run
    )

    pairs = prepare_pairs(
        features,
        labels,
        label_tolerance=manifest["label_tolerance"],
    )

    
    splits = chronological_splits(pairs)

    policy = SplitQualityPolicy(
        horizons=(30, 120),

        min_train_samples=100,
        min_validation_samples=40,
        min_test_samples=40,

        min_train_duration="8h",
        min_validation_duration="4h",
        min_test_duration="4h",

        max_eligible_gap="30min",
        grid_frequency="5min",
        min_coverage_fraction=0.50,
        enforce_max_eligible_gap=False,
    )

    splits, quality_report = validate_split_quality(
        splits,
        evaluation_cutoff_utc=(
            args.evaluation_cutoff_utc
        ),
        policy=policy,
    )

    print("\nChronological split quality: PASSED")

    for partition in quality_report["partitions"]:
        print(
            f"  {partition['partition']:<11} "
            f"h={partition['horizon_minutes']:<3} "
            f"n={partition['samples']:<5} "
            f"duration="
            f"{partition['effective_duration_minutes']:.0f}m "
            f"coverage="
            f"{partition['coverage_fraction']:.1%} "
            f"max_gap="
            f"{partition['largest_eligible_gap_minutes']:.0f}m"
        )
    if quality_report["warnings"]:
        print("\nExploratory quality warnings:")

        for warning in quality_report["warnings"]:
            print("  WARNING:", warning)

    print("\n=== NUGGET CAPITAL ===")
    print("Phase 6C - Persistence Baseline\n")

    print("Research run:", manifest["run_sha256"])
    print("Item:", manifest["item_id"])

    print(
        "\nValidation begins:",
        splits.validation_start_utc,
    )
    print("Test begins:", splits.test_start_utc)

    # Never publish an evaluation with no eligible
    # observations in any required partition.
    for name, frame in (
        ("train", splits.train),
        ("validation", splits.validation),
        ("test", splits.test),
    ):
        if frame.empty:
            raise RuntimeError(
                f"{name} contains no eligible observations"
            )

    results = {}

    for horizon in sorted(
        pairs["horizon_minutes"].unique()
    ):
        horizon = int(horizon)

        print(f"\nHorizon: {horizon} minutes")

        results[str(horizon)] = {}

        for name, frame in (
            ("train", splits.train),
            ("validation", splits.validation),
            ("test", splits.test),
        ):
            subset = frame.loc[
                frame["horizon_minutes"] == horizon
            ]

            metrics = evaluate_persistence(subset)

            results[str(horizon)][name] = metrics

            print(
                f"  {name:<11} "
                f"n={metrics['samples']:<5} "
                f"MAE={metrics['mae']} "
                f"RMSE={metrics['rmse']}"
            )

    report = {
        "evaluation_version": EVALUATION_VERSION,
        "split_quality": quality_report,
        "research_run_sha256": manifest["run_sha256"],
        "source_dataset_sha256": (
            manifest["source_dataset_sha256"]
        ),
        "item_id": manifest["item_id"],
        "baseline": "zero_log_return",
        "split_policy": {
            "train_fraction": 0.60,
            "validation_fraction": 0.20,
            "validation_start_utc": (
                splits.validation_start_utc.isoformat()
            ),
            "test_start_utc": (
                splits.test_start_utc.isoformat()
            ),
            "training_label_policy": (
                "Label available and target time "
                "strictly before validation start"
            ),
            "validation_label_policy": (
                "Label available and target time "
                "strictly before test start"
            ),
        },
        "metrics": results,
        "interpretation": (
            "Reference-price forecast error only. "
            "No execution or profitability assumptions."
        ),
        "evaluation_mode": "exploratory",
    }

    canonical = json.dumps(
        report,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")

    report["evaluation_sha256"] = hashlib.sha256(
        canonical
    ).hexdigest()

    output = args.output

    # Don't overwrite an existing evaluation.
    output.parent.mkdir(
        parents=True, exist_ok=True
    )

    with output.open(
        "x", encoding="utf-8"
    ) as stream:
        json.dump(
            report,
            stream,
            indent=2,
            sort_keys=True,
            allow_nan=False,
        )
        stream.write("\n")

    print("\nEvaluation fingerprint:")
    print(report["evaluation_sha256"])
    print("\nReport:", output)
    print("\nPERSISTENCE BASELINE COMPLETED")


if __name__ == "__main__":
    main()
