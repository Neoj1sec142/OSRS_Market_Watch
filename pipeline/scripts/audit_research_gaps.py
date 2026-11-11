
"""
OSRS Market Watch - Phase 6C Gap Attribution CLI.

Reads an immutable Phase 6B research run.
Produces an independent diagnostic JSON artifact.

No production access.
No modification of source or evaluation artifacts.
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
    prepare_pairs,
)
from osrs_market_ml.evaluation.gap_attribution import (
    build_gap_attribution,
)


def load_run(run_dir: Path):
    manifest = json.loads(
        (run_dir / "manifest.json").read_text(
            encoding="utf-8"
        )
    )

    if manifest.get("pipeline_version") != "phase6b-v1":
        raise ValueError(
            "Unsupported research pipeline version"
        )

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

    fingerprint = hashlib.sha256(
        canonical
    ).hexdigest()

    if fingerprint != manifest["run_sha256"]:
        raise ValueError(
            "Research run fingerprint mismatch"
        )

    for filename in (
        "market_grid.csv",
        "features.csv",
        "labels.csv",
    ):
        if sha256_file(
            run_dir / filename
        ) != manifest["files"][filename]:
            raise ValueError(
                f"{filename}: checksum mismatch"
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


def main() -> None:
    parser = argparse.ArgumentParser()

    parser.add_argument(
        "--run",
        type=Path,
        required=True,
    )

    parser.add_argument(
        "--output",
        type=Path,
        required=True,
    )

    parser.add_argument(
        "--evaluation-cutoff-utc",
        required=True,
    )

    parser.add_argument(
        "--gap-threshold",
        default="30min",
    )

    args = parser.parse_args()

    manifest, features, labels = load_run(args.run)

    pairs = prepare_pairs(
        features,
        labels,
        label_tolerance=manifest["label_tolerance"],
    )

    splits = chronological_splits(pairs)

    report = build_gap_attribution(
        pairs,
        splits,
        evaluation_cutoff_utc=(
            args.evaluation_cutoff_utc
        ),
        grid_frequency=manifest["grid_frequency"],
        gap_threshold=args.gap_threshold,
    )

    report["research_run_sha256"] = (
        manifest["run_sha256"]
    )

    report["source_dataset_sha256"] = (
        manifest["source_dataset_sha256"]
    )

    print("\n=== NUGGET CAPITAL ===")
    print("Phase 6C - Gap Attribution\n")

    print("Item:", report["item_id"])
    print("Research run:", manifest["run_sha256"])
    print("Long gaps:", report["long_gap_count"])

    print("\nPartition attribution:")

    for entry in report["partitions"]:
        print(
            f"  {entry['partition']:<11} "
            f"h={entry['horizon_minutes']:<3} "
            f"eligible={entry['eligible']:<5} "
            f"excluded={entry['excluded']:<5}"
        )

        reasons = {
            key: value
            for key, value in entry[
                "reason_counts"
            ].items()
            if key != "eligible"
        }

        if reasons:
            print("    Reasons:", reasons)

    print("\nLong eligible-decision gaps:")

    if not report["long_gaps"]:
        print("  None above threshold")

    for gap in report["long_gaps"]:
        print(
            f"  {gap['partition']:<11} "
            f"h={gap['horizon_minutes']:<3} "
            f"duration={gap['duration_minutes']:.0f}m"
        )

        print(
            "    Start:",
            gap["start_utc"],
        )

        print(
            "    End:  ",
            gap["end_utc"],
        )

        print(
            "    Exclusions:",
            gap["primary_reasons"],
        )

    canonical = json.dumps(
        report,
        sort_keys=True,
        separators=(",", ":"),
        allow_nan=False,
    ).encode("utf-8")

    report["audit_sha256"] = hashlib.sha256(
        canonical
    ).hexdigest()

    output = args.output
    output.parent.mkdir(
        parents=True,
        exist_ok=True,
    )

    with output.open(
        "x",
        encoding="utf-8",
    ) as stream:
        json.dump(
            report,
            stream,
            indent=2,
            sort_keys=True,
            allow_nan=False,
        )
        stream.write("\n")

    print("\nAudit fingerprint:")
    print(report["audit_sha256"])

    print("\nReport:", output)
    print("\nGAP ATTRIBUTION COMPLETED")


if __name__ == "__main__":
    main()
