
# Test DB Script
**To create a new DB from our test DB, open the root dir in a powershell session and drop script**

```sh
cd C:\Users\Neo\Documents\Projects\OSRS_Market_Watch
```

```powershell

@'
import sqlite3
from pathlib import Path

root = Path.cwd()
source = root / "MarketAPI" / "Data" / "market.db"
target = root / "MarketAPI" / "Data" / "archive_planner_test.db"

if not source.is_file() or source.stat().st_size == 0:
    raise SystemExit("Source market.db is missing or empty.")

# Never overwrite an existing test database.
if target.exists():
    raise SystemExit(
        f"Test database already exists: {target}\n"
        "Remove it explicitly before recreating."
    )

print(f"Source: {source}")
print(f"Target: {target}")

try:
    # Source is read-only; backup includes committed WAL data.
    with sqlite3.connect(
        source.as_uri() + "?mode=ro", uri=True
    ) as src:
        with sqlite3.connect(target) as dst:
            src.backup(dst)

    with sqlite3.connect(
        target.as_uri() + "?mode=ro", uri=True
    ) as db:
        integrity = db.execute(
            "PRAGMA integrity_check"
        ).fetchone()[0]

        fk_errors = db.execute(
            "PRAGMA foreign_key_check"
        ).fetchall()

        if integrity != "ok" or fk_errors:
            raise RuntimeError(
                f"Integrity={integrity}; FK errors={len(fk_errors)}"
            )

        print("\n=== TEST DATABASE READY ===")

        for table in (
            "MarketItems",
            "MarketCollectionBatches",
            "MarketPriceSnapshots",
            "MarketPriceAggregates",
            "MarketArchiveSources",
            "MarketArchiveExports",
        ):
            count = db.execute(
                f'SELECT COUNT(*) FROM "{table}"'
            ).fetchone()[0]

            print(f"{table}: {count:,}")

        print(f"\nIntegrity: {integrity}")
        print("Foreign keys: OK")
        print(f"Size: {target.stat().st_size:,} bytes")

except Exception:
    if target.exists():
        target.unlink()
    raise
'@ | python -

```


### Test it out:
```powershell
cd C:\Users\Neo\Documents\Projects\OSRS_Market_Watch\MarketAPI

$testDb = (Resolve-Path ".\Data\archive_planner_test.db").Path

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:DOTNET_ENVIRONMENT = "Development"
$env:ConnectionStrings__MarketDb = "Data Source=$testDb;Foreign Keys=True;Default Timeout=10;Pooling=True"
$env:MarketCollector__Enabled = "false"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5066"

dotnet run --no-launch-profile --no-build
```