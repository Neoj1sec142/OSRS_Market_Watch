# OSRS Market Watch — Python ML pipeline, Phase 1B

Place the **contents of this archive** into:

`C:\Users\Neo\Documents\Projects\OSRS_Market_Watch\pipeline\`


C:\Users\Neo\Documents\Projects\OSRS_Market_Watch\MarketAPI\Data\market.db

## Run (PowerShell)

```powershell
cd C:\Users\Neo\Documents\Projects\OSRS_Market_Watch\pipeline
py -3 -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -e ".[test]"
python -m pytest -q
```

## Data access

```python
from datetime import datetime, timezone
from osrs_market_ml.data.sqlite_reader import SQLiteMarketReader
from osrs_market_ml.data.snapshot_export import backup_market_database

# Prefer exporting first for research workloads:
backup_market_database(r"C:\path\to\market.db", r"C:\path\to\research-copy.db")
reader = SQLiteMarketReader(r"C:\path\to\research-copy.db")
reader.verify_schema()
states = reader.market_states(datetime.now(timezone.utc))
print(len(states), states[:2])
```

### Accuracy boundaries
- SQL reader opens `mode=ro` and `query_only=ON`; no writes to production database.
- Historical snapshots restricted to *completed* batches with `CompletedAtUtc <= cutoff`.
- Uses complete latest snapshot per item; does not independently merge both sides.
- The database's exact SQLite `DateTime` encoding remains to be verified against your actual database.
- To avoid incorrect lexical comparisons across differing timestamp string encodings, the proof-of-concept snapshot reader filters in Python. **It is not ready for large-scale history**; next optimization is index-friendly time-bounded SQL once storage encoding is confirmed.
- Current aggregate UPSERT rows are not necessarily point-in-time historical truth after revisions. Do not train/backtest against them as if they were immutable.
- Metadata is current, not historical. Do not backtest old buy limits without historical metadata.
- Need `SqliteMarketBulkWriter.InsertSnapshotsAsync` and `UpsertAggregatesAsync` for final dedup / revision semantics.
