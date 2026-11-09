
"""
OSRS Market Watch - Phase 1C diagnostic.

Usage:
python scripts/inspect_market.py \
    --database "C:/path/to/market.db" \
    --item-id 4151
"""

from __future__ import annotations

import argparse
import sqlite3
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pandas as pd

from osrs_market_ml.data.snapshot_export import (
    backup_market_database,
)
from osrs_market_ml.data.sqlite_reader import (
    SQLiteMarketReader,
)
from osrs_market_ml.data.research_loader import (
    load_price_history,
)
from osrs_market_ml.features.market_grid import (
    build_market_grid,
)


def main() -> None:
    parser = argparse.ArgumentParser()

    parser.add_argument(
        "--database",
        required=True,
        type=Path,
        help="Primary .NET SQLite database",
    )

    parser.add_argument(
        "--item-id",
        type=int,
        default=4151,
        help="OSRS item ID to inspect",
    )

    parser.add_argument(
        "--days",
        type=int,
        default=7,
        help="Historical window in days",
    )

    args = parser.parse_args()

    if args.days < 1:
        parser.error("--days must be positive")

    now = datetime.now(timezone.utc)

    # Do not overwrite prior research backups.
    run_id = now.strftime("%Y%m%d_%H%M%S_%f")

    root = Path(__file__).resolve().parents[1]

    backup_path = (
        root
        / "artifacts"
        / "datasets"
        / f"research_{run_id}.db"
    )

    print("\n=== OSRS MARKET WATCH ===")
    print("Phase 1C - Data Integration\n")

    print("Creating consistent database backup...")

    backup_market_database(
        args.database,
        backup_path,
    )

    print(f"Backup: {backup_path}")

    reader = SQLiteMarketReader(backup_path)

    schema = reader.verify_schema()

    print("\nSchema verification:")
    for table, columns in schema.items():
        print(f"  {table}: {len(columns)} columns")

    print("\nSQLite timestamp storage samples:")

    with reader.connect() as connection:
        samples = connection.execute("""
            SELECT
                typeof(CollectedAtUtc) AS StorageType,
                CollectedAtUtc AS Sample
            FROM MarketPriceSnapshots
            ORDER BY Id DESC
            LIMIT 3
        """).fetchall()

        for sample in samples:
            print(
                f"  {sample['StorageType']}: "
                f"{sample['Sample']}"
            )

        counts = connection.execute("""
            SELECT
                (SELECT COUNT(*) FROM MarketItems)
                    AS Items,
                (SELECT COUNT(*) FROM MarketPriceSnapshots)
                    AS Snapshots,
                (SELECT COUNT(*) FROM MarketPriceAggregates)
                    AS Aggregates,
                (SELECT COUNT(*) FROM MarketCollectionBatches)
                    AS Batches
        """).fetchone()

        print("\nDatabase counts:")
        for key in counts.keys():
            print(f"  {key}: {counts[key]:,}")

    end = pd.Timestamp(now)
    start = end - pd.Timedelta(days=args.days)

    print(
        f"\nLoading item {args.item_id} "
        f"for the last {args.days} days..."
    )

    history = load_price_history(
        backup_path,
        item_ids=[args.item_id],
        start_utc=start.to_pydatetime(),
        end_utc=end.to_pydatetime(),
    )

    print(f"Eligible historical snapshots: {len(history)}")

    if history.empty:
        print("No eligible observations for this item.")
        return

    print("\nSnapshot integrity:")
    print(
        "  Earliest collected:",
        history["CollectedAtUtc"].min(),
    )
    print(
        "  Latest collected:",
        history["CollectedAtUtc"].max(),
    )

    duplicate_ids = history["Id"].duplicated().sum()
    print("  Duplicate snapshot IDs:", duplicate_ids)

    future_buy = (
        history["InstantBuyTimeUtc"]
        > history["AvailableAtUtc"]
    ).sum()

    future_sell = (
        history["InstantSellTimeUtc"]
        > history["AvailableAtUtc"]
    ).sum()

    print("  Future-dated buy timestamps:", future_buy)
    print("  Future-dated sell timestamps:", future_sell)

    grid = build_market_grid(
        history,
        item_id=args.item_id,
        start_utc=start,
        end_utc=end,
    )

    print("\nFive-minute state grid:")
    print("  Time points:", len(grid))

    print(
        "  Known states:",
        int(grid["HasKnownState"].sum()),
    )

    print(
        "  Valid reference prices:",
        int(grid["HasValidReference"].sum()),
    )

    if grid["HasKnownState"].any():
        print(
            "  Median state age (minutes):",
            round(
                grid["StateAgeMinutes"].median(),
                2,
            ),
        )

    print("\nMost recent reconstructed states:")

    columns = [
        "PredictionTimeUtc",
        "InstantBuyPrice",
        "InstantSellPrice",
        "BuyQuoteAgeMinutes",
        "SellQuoteAgeMinutes",
        "ReferencePrice",
        "ObservedGapGp",
        "HasValidReference",
    ]

    print(
        grid[columns].tail(10).to_string(
            index=False
        )
    )

    output = (
        root
        / "artifacts"
        / "datasets"
        / f"item_{args.item_id}_{run_id}.csv"
    )

    grid.to_csv(output, index=False)

    print(f"\nExported timeline: {output}")
    print("\nPhase 1C diagnostic completed.")


if __name__ == "__main__":
    main()
