
import csv
import hashlib
import json

from pathlib import Path

import pytest

from osrs_market_ml.data.canonical_loader import (
    AGGREGATE_COLUMNS,
    SNAPSHOT_COLUMNS,
    load_canonical_dataset,
    sha256_file,
)
from osrs_market_ml.data.dataset_validation import (
    validate_dataset,
)


SOURCE_HASH = "a" * 64


def write_csv(path, columns, rows):
    with path.open(
        "w", newline="", encoding="utf-8"
    ) as stream:
        writer = csv.writer(
            stream, lineterminator="\n"
        )
        writer.writerow(columns)
        writer.writerows(rows)


@pytest.fixture
def dataset_dir(tmp_path):
    root = tmp_path / "dataset"
    root.mkdir()

    snapshots = [
        [
            1, 4151, 10,
            680000, 660000,
            "2026-11-11 12:00:00",
            "2026-11-11 12:00:00",
            "2026-11-11 12:00:10.1234567",
            "2026-11-11 12:00:20.1234567",
        ],
        [
            2, 4151, 11,
            681000, 661000,
            "2026-11-11 12:05:00",
            "2026-11-11 12:05:00",
            "2026-11-11 12:05:10",
            "2026-11-11 12:05:20",
        ],
    ]

    five_minute = [
        [
            10, 4151, 5,
            "2026-11-11 12:00:00",
            "680000.125000", "660000.500000",
            100, 90,
            "2026-11-11 12:06:00",
        ],
        [
            11, 4151, 5,
            "2026-11-11 12:10:00",
            "681000.000000", "661000.000000",
            110, 95,
            "2026-11-11 12:16:00",
        ],
    ]

    hourly = [
        [
            20, 4151, 60,
            "2026-11-11 12:00:00",
            "680500.000000", "660500.000000",
            1000, 900,
            "2026-11-11 13:01:00",
        ],
    ]

    write_csv(
        root / "snapshots.csv",
        SNAPSHOT_COLUMNS,
        snapshots,
    )

    write_csv(
        root / "aggregates_5m.csv",
        AGGREGATE_COLUMNS,
        five_minute,
    )

    write_csv(
        root / "aggregates_1h.csv",
        AGGREGATE_COLUMNS,
        hourly,
    )

    files = [
        "snapshots.csv",
        "aggregates_5m.csv",
        "aggregates_1h.csv",
    ]

    manifest = {
        "dataset_version": "research-raw-v1",
        "item_id": 4151,
        "source_label": "test-backup",
        "source_sha256": SOURCE_HASH,
        "source_size_bytes": 1000,
        "row_counts": {
            "snapshots": 2,
            "aggregates_5m.csv": 2,
            "aggregates_1h.csv": 1,
        },
        "files": {
            name: sha256_file(root / name)
            for name in files
        },
    }

    (root / "manifest.json").write_text(
        json.dumps(
            manifest, indent=2, sort_keys=True
        ) + "\n",
        encoding="utf-8",
    )

    return root


def test_canonical_loader_preserves_records(dataset_dir):
    data = load_canonical_dataset(dataset_dir)

    assert data.item_id == 4151

    assert len(data.observations) == 2
    assert len(data.aggregates_5m) == 2
    assert len(data.aggregates_1h) == 1

    first = data.observations[0]

    assert first.source_row_id == 1
    assert first.instant_buy_price_gp == 680000
    assert first.collection_batch_id == 10

    assert first.raw_collected_at_utc == (
        "2026-11-11 12:00:10.1234567"
    )


def test_dataset_fingerprint_is_deterministic(dataset_dir):
    first = load_canonical_dataset(dataset_dir)
    second = load_canonical_dataset(dataset_dir)

    assert first.dataset_sha256 == second.dataset_sha256

    expected = hashlib.sha256(
        (dataset_dir / "manifest.json").read_bytes()
    ).hexdigest()

    assert first.dataset_sha256 == expected


def test_loader_rejects_modified_csv(dataset_dir):
    csv_path = dataset_dir / "snapshots.csv"

    with csv_path.open("a", encoding="utf-8") as stream:
        stream.write("tampered\n")

    with pytest.raises(ValueError, match="SHA-256"):
        load_canonical_dataset(dataset_dir)


def test_validator_reports_missing_bucket(dataset_dir):
    data = load_canonical_dataset(dataset_dir)
    report = validate_dataset(data)

    assert report.passed
    assert report.five_minute.row_count == 2
    assert report.five_minute.expected_buckets == 3
    assert report.five_minute.missing_buckets == 1


def test_validator_rejects_duplicate_snapshot_identity(
    dataset_dir,
):
    # Construct an invalid in-memory dataset while keeping
    # the verified source files unchanged.
    from dataclasses import replace

    data = load_canonical_dataset(dataset_dir)

    invalid = replace(
        data,
        observations=(
            data.observations[0],
            data.observations[0],
        ),
    )

    report = validate_dataset(invalid)

    assert not report.passed
    assert any(
        "Duplicate snapshot" in error
        for error in report.errors
    )
