from __future__ import annotations

from datetime import datetime, timedelta, timezone
import sqlite3
import pytest

from osrs_market_ml.data.contracts import AggregateInterval
from osrs_market_ml.data.sqlite_reader import SQLiteMarketReader, parse_ef_datetime
from osrs_market_ml.data.snapshot_export import backup_market_database


def dt(minutes):
    return (datetime(2026, 11, 9, 12, 0, tzinfo=timezone.utc) + timedelta(minutes=minutes))


def ef(minutes):
    return dt(minutes).replace(tzinfo=None).isoformat(sep=" ", timespec="microseconds")


@pytest.fixture
def db_path(tmp_path):
    path = tmp_path / "market.db"
    db = sqlite3.connect(path)
    db.executescript("""
    CREATE TABLE MarketItems (Id INTEGER PRIMARY KEY, Name TEXT, Members INTEGER,
      BuyLimit INTEGER, HighAlchValue INTEGER, LowAlchValue INTEGER,
      IsActive INTEGER, IsTaxExempt INTEGER, LastMetadataUpdateUtc TEXT, Examine TEXT, Icon TEXT);
    CREATE TABLE MarketCollectionBatches (Id INTEGER PRIMARY KEY, StartedAtUtc TEXT,
      CompletedAtUtc TEXT, Status INTEGER);
    CREATE TABLE MarketPriceSnapshots (Id INTEGER PRIMARY KEY, MarketItemId INTEGER,
      CollectionBatchId INTEGER, InstantBuyPrice INTEGER, InstantSellPrice INTEGER,
      InstantBuyTimeUtc TEXT, InstantSellTimeUtc TEXT, CollectedAtUtc TEXT);
    CREATE TABLE MarketPriceAggregates (Id INTEGER PRIMARY KEY, MarketItemId INTEGER,
      Interval INTEGER, BucketStartUtc TEXT, AverageInstantBuyPrice TEXT,
      AverageInstantSellPrice TEXT, InstantBuyVolume INTEGER,
      InstantSellVolume INTEGER, CollectedAtUtc TEXT);
    """)
    db.execute("INSERT INTO MarketItems VALUES (4151,'Abyssal whip',1,70,NULL,NULL,1,0,?,NULL,NULL)", (ef(0),))
    batches = [(1,ef(0),ef(1),1),(2,ef(5),ef(6),1),
               (3,ef(10),ef(11),2),(4,ef(15),ef(17),1)]
    db.executemany("INSERT INTO MarketCollectionBatches VALUES (?,?,?,?)",batches)
    snapshots = [
      (1,4151,1,1000,900,ef(-5),ef(-8),ef(0)),
      (2,4151,2,1100,950,ef(5),ef(-8),ef(5)),
      (3,4151,3,9999,8000,ef(10),ef(10),ef(10)),
      (4,4151,4,1200,970,ef(18),ef(15),ef(15)),
    ]
    db.executemany("INSERT INTO MarketPriceSnapshots VALUES (?,?,?,?,?,?,?,?)",snapshots)
    db.execute("INSERT INTO MarketPriceAggregates VALUES (1,4151,5,?,?,?,12,8,?)",(ef(0),'1010.25','920.25',ef(7)))
    db.commit()
    db.close()
    return path


def test_schema_and_items(db_path):
    r=SQLiteMarketReader(db_path)
    assert set(r.verify_schema()) == {"MarketItems","MarketCollectionBatches","MarketPriceSnapshots","MarketPriceAggregates"}
    item=r.items()[0]
    assert item.id==4151 and item.buy_limit==70


def test_only_completed_batches_available_by_cutoff(db_path):
    reader=SQLiteMarketReader(db_path)
    assert [s.id for s in reader.snapshots(dt(5))]==[1]
    assert [s.id for s in reader.snapshots(dt(6))]==[1,2]
    assert [s.id for s in reader.snapshots(dt(20))]==[1,2,4]
    assert reader.market_states(dt(6))[0].source_snapshot_id==2


def test_future_source_trade_masked_and_ages_are_exchange_side(db_path):
    reader=SQLiteMarketReader(db_path)
    state=reader.market_states(dt(17))[0]
    assert state.source_snapshot_id==4
    assert state.instant_buy_price is None  # source buy timestamp is at 12:18
    assert state.instant_sell_price==970
    assert state.sell_quote_age==timedelta(minutes=2)


def test_aggregate_completion_gate(db_path):
    reader=SQLiteMarketReader(db_path)
    assert reader.aggregates_for_research(dt(6),AggregateInterval.FIVE_MINUTES)==[]
    rows=reader.aggregates_for_research(dt(7),AggregateInterval.FIVE_MINUTES)
    assert len(rows)==1 and str(rows[0].average_instant_buy_price)=="1010.25"


def test_backup_wal_and_read_only(db_path,tmp_path):
    writable=sqlite3.connect(db_path)
    writable.execute("PRAGMA journal_mode=WAL")
    writable.execute("UPDATE MarketItems SET BuyLimit=80 WHERE Id=4151")
    writable.commit()
    reader=SQLiteMarketReader(db_path)
    assert reader.items()[0].buy_limit==80
    with reader.connect() as conn:
        with pytest.raises(sqlite3.OperationalError):
            conn.execute("UPDATE MarketItems SET BuyLimit=90 WHERE Id=4151")
    copy=backup_market_database(db_path,tmp_path/'backup.db')
    assert SQLiteMarketReader(copy).items()[0].buy_limit==80
    writable.close()


def test_ef_datetime_parsing():
    assert parse_ef_datetime('2026-11-09 12:00:00.1234567').tzinfo==timezone.utc
    assert parse_ef_datetime('2026-11-09T13:00:00+01:00').hour==12
