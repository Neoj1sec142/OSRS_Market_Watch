
"""
Phase 6A - Inspect a verified canonical research dataset.

Usage:
python scripts/inspect_research_dataset.py \
    --dataset artifacts/datasets/phase6a_item4151_v1
"""

from __future__ import annotations

import argparse
import json

from dataclasses import asdict
from datetime import datetime
from pathlib import Path

from osrs_market_ml.data.canonical_loader import (
    load_canonical_dataset,
)
from osrs_market_ml.data.dataset_validation import (
    validate_dataset,
)


def json_default(value):
    if isinstance(value, datetime):
        return value.isoformat()

    raise TypeError(
        f"Cannot serialize {type(value).__name__}"
    )


def main() -> None:
    parser = argparse.ArgumentParser()

    parser.add_argument(
        "--dataset",
        type=Path,
        required=True,
    )

    parser.add_argument(
        "--max-quote-age",
        type=int,
        default=30,
    )

    args = parser.parse_args()

    if args.max_quote_age <= 0:
        parser.error("--max-quote-age must be positive")

    dataset = load_canonical_dataset(args.dataset)

    report = validate_dataset(
        dataset,
        maximum_quote_age_minutes=args.max_quote_age,
    )

    print("\n=== OSRS MARKET WATCH ===")
    print("Phase 6A - Canonical Research Inspection\n")

    print("Item:", dataset.item_id)
    print("Source:", dataset.source.source_label)

    print(
        "Source SHA-256:",
        dataset.source.artifact_sha256,
    )

    print(
        "Dataset SHA-256:",
        dataset.dataset_sha256,
    )

    print("\nRecord counts:")
    print("  Snapshots:", report.snapshot_count)
    print("  Five-minute:", report.five_minute.row_count)
    print("  Hourly:", report.hourly.row_count)

    print("\nSnapshot availability:")
    print("  First:", report.first_available_utc)
    print("  Last: ", report.last_available_utc)

    print("\nMedian quote age at availability:")
    print(
        "  Buy:",
        report.median_buy_quote_age_minutes,
        "minutes",
    )
    print(
        "  Sell:",
        report.median_sell_quote_age_minutes,
        "minutes",
    )

    for title, coverage in (
        ("Five-minute", report.five_minute),
        ("Hourly", report.hourly),
    ):
        print(f"\n{title} coverage:")
        print("  First bucket:", coverage.first_bucket_utc)
        print("  Last bucket: ", coverage.last_bucket_utc)
        print("  Expected:    ", coverage.expected_buckets)
        print("  Observed:    ", coverage.row_count)
        print("  Missing:     ", coverage.missing_buckets)

    print("\nValidation:")
    print("  Errors:", len(report.errors))
    print("  Warnings:", len(report.warnings))

    for error in report.errors[:20]:
        print("  ERROR:", error)

    for warning in report.warnings[:20]:
        print("  WARNING:", warning)

    if len(report.warnings) > 20:
        print(
            "  ... additional warnings omitted "
            "from console"
        )

    output = {
        "report_version": "coverage-v1",
        "source_sha256": dataset.source.artifact_sha256,
        "dataset_sha256": dataset.dataset_sha256,
        "validation": asdict(report),
    }

    report_path = (
        args.dataset.parent
        / f"{args.dataset.name}_coverage_report.json"
    )

    # The verified raw dataset remains immutable.
    # Only write the report if it doesn't already exist.
    if report_path.exists():
        raise FileExistsError(
            f"Report already exists: {report_path}"
        )

    report_path.write_text(
        json.dumps(
            output,
            indent=2,
            sort_keys=True,
            default=json_default,
        ) + "\n",
        encoding="utf-8",
    )

    print("\nReport:", report_path)

    if not report.passed:
        raise SystemExit(
            "DATASET VALIDATION FAILED"
        )

    print("\nCANONICAL DATASET VALIDATION PASSED")


if __name__ == "__main__":
    main()
