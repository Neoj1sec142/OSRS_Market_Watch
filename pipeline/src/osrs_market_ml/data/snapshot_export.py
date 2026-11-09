"""Create consistent offline SQLite research copies using the backup API."""
from __future__ import annotations

from pathlib import Path
import sqlite3

from .sqlite_reader import SQLiteMarketReader


def backup_market_database(source: str | Path, destination: str | Path) -> Path:
    """Take SQLite online backup (includes WAL commits) to a *new* file.

    Source is opened read-only. Destination is created outside the live DB.
    Never manually copy only the primary .db file from a WAL database.
    """
    reader = SQLiteMarketReader(source)
    output = Path(destination).expanduser().resolve()
    if output.exists():
        raise FileExistsError(output)
    if output == reader.database:
        raise ValueError("destination must differ from source")
    output.parent.mkdir(parents=True, exist_ok=True)
    try:
        with reader.connect() as source_db:
            with sqlite3.connect(str(output)) as destination_db:
                source_db.backup(destination_db, pages=1024, sleep=0.05)
        return output
    except BaseException:
        output.unlink(missing_ok=True)
        raise
