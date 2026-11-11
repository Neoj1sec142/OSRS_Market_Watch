
"""
Phase 6B - Generate versioned research features and labels.

Reads a verified canonical dataset.
Writes CSV outputs and a reproducibility manifest.
Never modifies the source dataset.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from osrs_market_ml.data.canonical_loader import (
    load_canonical_dataset,
    sha256_file,
)
from osrs_market_ml.data.dataset_validation import (
    validate_dataset,
)
from osrs_market_ml.features.point_in_time import (
    FEATURE_VERSION,
    build_grid,
)
from osrs_market_ml.features.feature_engine import (
    build_features,
)
from osrs_market_ml.targets.forward_returns import (
    TARGET_VERSION,
    build_labels,
)


def main() -> None:
    parser = argparse.ArgumentParser()

    parser.add_argument(
        "--dataset",
        type=Path,
        required=True,
    )

    parser.add_argument(
        "--output",
        type=Path,
        required=True,
    )

    args = parser.parse_args()

    dataset = load_canonical_dataset(args.dataset)

    report = validate_dataset(dataset)

    if not report.passed:
        raise RuntimeError(
            f"Source validation failed: {report.errors}"
        )

    grid = build_grid(dataset)
    features = build_features(grid)
    labels = build_labels(dataset, grid)

    output = args.output

    # Fail rather than overwrite any earlier run.
    output.mkdir(parents=True, exist_ok=False)

    grid_path = output / "market_grid.csv"
    feature_path = output / "features.csv"
    label_path = output / "labels.csv"

    grid.to_csv(grid_path, index=False)
    features.to_csv(feature_path, index=False)
    labels.to_csv(label_path, index=False)

    files = (
        grid_path,
        feature_path,
        label_path,
    )

    manifest = {
        "pipeline_version": "phase6b-v1",
        "item_id": dataset.item_id,
        "source_dataset_sha256": dataset.dataset_sha256,
        "source_artifact_sha256": (
            dataset.source.artifact_sha256
        ),
        "feature_version": FEATURE_VERSION,
        "target_version": TARGET_VERSION,
        "grid_frequency": "5min",
        "maximum_quote_age": "30min",
        "label_horizons_minutes": [30, 120],
        "label_tolerance": "10min",
        "row_counts": {
            "grid": len(grid),
            "features": len(features),
            "labels": len(labels),
        },
        "eligible_feature_rows": int(
            features["feature_eligible"].sum()
        ),
        "label_status_counts": {
            str(key): int(value)
            for key, value in labels[
                "status"
            ].value_counts().items()
        },
        "files": {
            path.name: sha256_file(path)
            for path in files
        },
    }

    # A semantic run fingerprint based on manifest
    # fields before the fingerprint is added.
    canonical = json.dumps(
        manifest,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")

    manifest["run_sha256"] = hashlib.sha256(
        canonical
    ).hexdigest()

    (output / "manifest.json").write_text(
        json.dumps(
            manifest,
            indent=2,
            sort_keys=True,
        ) + "\n",
        encoding="utf-8",
    )

    print("\n=== PHASE 6B RESEARCH RESULTS ===")
    print("Item:", dataset.item_id)
    print("Grid rows:", len(grid))
    print("Feature rows:", len(features))
    print("Eligible features:", manifest["eligible_feature_rows"])
    print("Label rows:", len(labels))

    print("\nLabel status counts:")
    print(labels["status"].value_counts().to_string())

    print("\nRun SHA-256:", manifest["run_sha256"])
    print("Output:", output)


if __name__ == "__main__":
    main()
