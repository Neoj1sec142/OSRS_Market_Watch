"""OSRS Wiki GE prices client + lightweight pandas market analytics.

Data: https://prices.runescape.wiki/api/v2/osrs

Install: pip install requests pandas

Usage:
    with OSRSGEMarket(user_agent='NuggetGE/0.1 (contact: your-email@example.com)') as ge:
        market = ge.market_snapshot()
        print(ge.flip_candidates(market, budget_gp=10_000_000).head(10).to_string(index=False))
        series = ge.item_history('Abyssal whip', lookback='30d')
        print(ge.history_features(series).tail(3).to_string(index=False))

This is an observational data client, not a trading bot. No RuneScape login required.
"""

from __future__ import annotations

import logging
import math
import time
from pathlib import Path
from typing import Any, Iterable
# pip install requests pandas
import pandas as pd
import requests
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry

LOG = logging.getLogger(__name__)


class OSRSGEMarket:
    BASE_URL = 'https://prices.runescape.wiki/api/v2/osrs'
    LOOKBACKS = frozenset({'6h', '24h', '7d', '30d', '6m', '1y'})
    AGGREGATIONS = frozenset({'5m', '1h'})
    TAX_RATE = 0.02
    TAX_CAP = 5_000_000

    def __init__(
        self,
        user_agent: str,
        *,
        timeout: float = 15.0,
        retries: int = 3,
        backoff_factor: float = 1.0,
        mapping_ttl_seconds: int = 24 * 3600,
        tax_exempt_ids: Iterable[int] = (),
        session: requests.Session | None = None,
    ) -> None:
        if not user_agent or ('contact:' not in user_agent.lower() and '@' not in user_agent):
            raise ValueError('Provide a descriptive User-Agent with contact details.')
        self.timeout = timeout
        self.mapping_ttl_seconds = mapping_ttl_seconds
        self.tax_exempt_ids = set(int(i) for i in tax_exempt_ids)
        self._mapping_cache: pd.DataFrame | None = None
        self._mapping_loaded_at = 0.0
        self._owns_session = session is None
        self.session = session or requests.Session()
        self.session.headers.update({'User-Agent': user_agent, 'Accept': 'application/json'})
        retry = Retry(
            total=retries,
            connect=retries,
            read=retries,
            status=retries,
            backoff_factor=backoff_factor,
            status_forcelist=(429, 500, 502, 503, 504),
            allowed_methods=frozenset({'GET'}),
            respect_retry_after_header=True,
        )
        self.session.mount('https://', HTTPAdapter(max_retries=retry))

    def __enter__(self) -> 'OSRSGEMarket':
        return self

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> None:
        self.close()

    def close(self) -> None:
        if self._owns_session:
            self.session.close()

    def _get(self, route: str, params: dict[str, Any] | None = None) -> Any:
        response = self.session.get(
            f'{self.BASE_URL}/{route.lstrip("/")}',
            params=params,
            timeout=self.timeout,
        )
        response.raise_for_status()
        return response.json()

    @staticmethod
    def _normalize_records(data: dict[str, dict], *, item_key: str = 'item_id') -> pd.DataFrame:
        if not isinstance(data, dict):
            raise ValueError('Expected an item-id keyed dictionary.')
        records = [{item_key: int(item_id), **(values or {})} for item_id, values in data.items()]
        return pd.DataFrame.from_records(records, columns=[item_key]) if not records else pd.DataFrame.from_records(records)

    @staticmethod
    def _utc_timestamp(values: pd.Series) -> pd.Series:
        return pd.to_datetime(values, unit='s', utc=True, errors='coerce')

    def items(self, *, refresh: bool = False) -> pd.DataFrame:
        """Get cached metadata: item_id, name, limit, examine, alch prices, etc."""
        now = time.monotonic()
        if (not refresh and self._mapping_cache is not None and
                now - self._mapping_loaded_at < self.mapping_ttl_seconds):
            return self._mapping_cache.copy()
        payload = self._get('mapping')
        if not isinstance(payload, list):
            raise ValueError('Unexpected /mapping schema.')
        df = pd.DataFrame(payload).rename(columns={'id': 'item_id', 'limit': 'buy_limit'})
        df['item_id'] = pd.to_numeric(df['item_id'], errors='raise').astype('int64')
        self._mapping_cache = df
        self._mapping_loaded_at = now
        return df.copy()

    def lookup(self, text: str, *, exact: bool = False) -> pd.DataFrame:
        items = self.items()
        mask = (items['name'].str.casefold() == text.casefold()) if exact else (
            items['name'].str.contains(text, case=False, regex=False, na=False)
        )
        return items.loc[mask].copy().reset_index(drop=True)

    def resolve_item(self, item: int | str) -> int:
        if isinstance(item, int):
            return item
        matches = self.lookup(item, exact=True)
        if len(matches) != 1:
            raise ValueError(f'Expected one exact item name match for {item!r}; got {len(matches)}. '
                             'Use lookup() or pass an item ID to disambiguate.')
        return int(matches.iloc[0]['item_id'])

    def latest(self, item: int | str | None = None) -> pd.DataFrame:
        """Bulk latest high (instant buy) and low (instant sell) trade prices."""
        params = {'id': self.resolve_item(item)} if item is not None else None
        payload = self._get('latest', params)
        df = self._normalize_records(payload['data'])
        df = df.rename(columns={
            'high': 'instant_buy', 'low': 'instant_sell',
            'highTime': 'instant_buy_time', 'lowTime': 'instant_sell_time',
        })
        for col in ('instant_buy_time', 'instant_sell_time'):
            if col in df:
                df[col] = self._utc_timestamp(df[col])
        return df

    def aggregate(self, window: str = '1h', *, timestamp: int | None = None) -> pd.DataFrame:
        """Bulk five-minute or hourly averages and observed trade volumes."""
        if window not in self.AGGREGATIONS:
            raise ValueError(f'window must be one of {sorted(self.AGGREGATIONS)}')
        payload = self._get(window, {'timestamp': timestamp} if timestamp is not None else None)
        df = self._normalize_records(payload['data']).rename(columns={
            'avgHighPrice': 'avg_instant_buy',
            'avgLowPrice': 'avg_instant_sell',
            'highPriceVolume': 'instant_buy_volume',
            'lowPriceVolume': 'instant_sell_volume',
        })
        if 'timestamp' in payload:
            df['bucket_time'] = pd.to_datetime(payload['timestamp'], unit='s', utc=True)
        return df

    def item_history(self, item: int | str, *, lookback: str = '30d') -> pd.DataFrame:
        """One item's history; v2 controls its own granularity by lookback."""
        if lookback not in self.LOOKBACKS:
            raise ValueError(f'lookback must be one of {sorted(self.LOOKBACKS)}')
        item_id = self.resolve_item(item)
        payload = self._get('timeseries', {'id': item_id, 'lookback': lookback})
        df = pd.DataFrame(payload.get('data', [])).rename(columns={
            'avgHighPrice': 'avg_instant_buy',
            'avgLowPrice': 'avg_instant_sell',
            'highPriceVolume': 'instant_buy_volume',
            'lowPriceVolume': 'instant_sell_volume',
        })
        if df.empty:
            return pd.DataFrame(columns=['item_id', 'timestamp', 'avg_instant_buy', 'avg_instant_sell'])
        df['timestamp'] = self._utc_timestamp(df['timestamp'])
        df['item_id'] = item_id
        # Metadata is included because the API may change returned granularity.
        df.attrs['interval_seconds'] = payload.get('timestep') or payload.get('interval')
        df.attrs['lookback'] = lookback
        return df.sort_values('timestamp').reset_index(drop=True)

    def market_snapshot(self, *, volume_window: str = '1h') -> pd.DataFrame:
        """Combine one bulk latest request, one bulk volume request, and item metadata."""
        latest = self.latest()
        agg = self.aggregate(volume_window)
        metadata_cols = [col for col in ('item_id', 'name', 'buy_limit', 'members', 'highalch')
                         if col in self.items().columns]
        market = (latest.merge(agg, on='item_id', how='left')
                        .merge(self.items()[metadata_cols], on='item_id', how='left'))
        for c in ('instant_buy', 'instant_sell', 'avg_instant_buy', 'avg_instant_sell',
                  'instant_buy_volume', 'instant_sell_volume', 'buy_limit'):
            if c in market:
                market[c] = pd.to_numeric(market[c], errors='coerce')
        market['observed_volume'] = market.get('instant_buy_volume', 0).fillna(0) + \
                                    market.get('instant_sell_volume', 0).fillna(0)
        market['quote_age_minutes'] = (
            (pd.Timestamp.now(tz='UTC') - market[['instant_buy_time', 'instant_sell_time']].min(axis=1))
            .dt.total_seconds().div(60).clip(lower=0)
        )
        market['spread_gp'] = market['instant_buy'] - market['instant_sell']
        return market

    @classmethod
    def ge_tax(cls, sell_gp: int, *, exempt: bool = False) -> int:
        """Per-item seller tax; caller must flag exempt item IDs if applicable."""
        if sell_gp < 0:
            raise ValueError('Sale price must be nonnegative.')
        return 0 if exempt else min(sell_gp // 50, cls.TAX_CAP)

    @classmethod
    def flip_profit(cls, buy_gp: int, sell_gp: int, *, exempt: bool = False) -> int:
        return sell_gp - buy_gp - cls.ge_tax(sell_gp, exempt=exempt)

    def flip_candidates(
        self,
        snapshot: pd.DataFrame,
        *,
        budget_gp: int = 10_000_000,
        max_quote_age_minutes: float = 30,
        min_volume: int = 100,
        min_profit_gp: int = 100,
        max_rows: int = 50,
    ) -> pd.DataFrame:
        """Rank *indicative* low-buy/high-sell flips; not guaranteed fill prices.

        Tax exemption is configurable. By default all items are modeled taxable.
        This conservatively understates profits on GE tax-exempt items.
        """
        if budget_gp <= 0:
            raise ValueError('budget_gp must be positive')
        df = snapshot.copy()
        df = df.dropna(subset=['instant_buy', 'instant_sell', 'quote_age_minutes',
                               'buy_limit', 'observed_volume'])
        df = df[(df['instant_sell'] > 0) & (df['instant_buy'] > 0) &
                (df['instant_buy'] >= df['instant_sell']) &
                (df['quote_age_minutes'] <= max_quote_age_minutes) &
                (df['observed_volume'] >= min_volume)].copy()
        if df.empty:
            return df
        df['buy_gp'] = df['instant_sell'].astype('int64')
        df['sell_gp'] = df['instant_buy'].astype('int64')
        exempt = df['item_id'].isin(self.tax_exempt_ids)
        df['tax_gp'] = (df['sell_gp'] // 50).clip(upper=self.TAX_CAP).where(~exempt, 0)
        df['profit_each_gp'] = df['sell_gp'] - df['buy_gp'] - df['tax_gp']
        df['roi_pct'] = 100 * df['profit_each_gp'] / df['buy_gp']
        df['units_by_budget'] = budget_gp // df['buy_gp']
        df['possible_units'] = df[['units_by_budget', 'buy_limit']].min(axis=1).clip(lower=0)
        df['gross_potential_gp'] = df['possible_units'] * df['profit_each_gp']
        df = df[(df['profit_each_gp'] >= min_profit_gp) & (df['possible_units'] >= 1)]
        cols = ['item_id', 'name', 'buy_gp', 'sell_gp', 'tax_gp', 'profit_each_gp',
                'roi_pct', 'observed_volume', 'quote_age_minutes', 'buy_limit',
                'possible_units', 'gross_potential_gp']
        return (df.sort_values(['gross_potential_gp', 'observed_volume'], ascending=False)
                  .loc[:, cols].head(max_rows).reset_index(drop=True))

    @staticmethod
    def history_features(history: pd.DataFrame, *, periods: int = 12) -> pd.DataFrame:
        """Simple features for exploration, not a trained prediction model.

        Rolling windows are based on observations, *not* fixed minutes because
        the v2 timeseries timestep is allowed to change by lookback.
        """
        df = history.copy().sort_values('timestamp')
        if df.empty:
            return df
        for col in ('avg_instant_buy', 'avg_instant_sell'):
            df[col] = pd.to_numeric(df[col], errors='coerce')
        df['mid_price'] = (df['avg_instant_buy'] + df['avg_instant_sell']) / 2
        df['spread_gp'] = df['avg_instant_buy'] - df['avg_instant_sell']
        df['spread_pct'] = 100 * df['spread_gp'] / df['mid_price'].replace(0, float('nan'))
        df['return_pct'] = df['mid_price'].pct_change(fill_method=None) * 100
        df['momentum_pct'] = df['mid_price'].pct_change(periods=periods, fill_method=None) * 100
        df['sma'] = df['mid_price'].rolling(periods, min_periods=periods).mean()
        df['volatility_pct'] = df['return_pct'].rolling(periods, min_periods=periods).std()
        if 'instant_buy_volume' in df and 'instant_sell_volume' in df:
            df['observed_volume'] = df['instant_buy_volume'].fillna(0) + df['instant_sell_volume'].fillna(0)
            df['volume_sma'] = df['observed_volume'].rolling(periods, min_periods=periods).mean()
            df['volume_ratio'] = df['observed_volume'] / df['volume_sma'].replace(0, float('nan'))
        return df

    @staticmethod
    def save_snapshot(snapshot: pd.DataFrame, directory: str | Path, *, label: str = 'market') -> Path:
        """Save one timestamped CSV for later backtesting. Not a historical backfill."""
        folder = Path(directory)
        folder.mkdir(parents=True, exist_ok=True)
        name = f'{label}_{pd.Timestamp.now(tz="UTC").strftime("%Y%m%dT%H%M%S%fZ")}.csv'
        destination = folder / name
        snapshot.to_csv(destination, index=False)
        return destination


if __name__ == '__main__':
    import os
    logging.basicConfig(level=logging.INFO)
    agent = os.environ.get('OSRS_GE_USER_AGENT')
    if not agent:
        raise SystemExit('Set OSRS_GE_USER_AGENT="MyGEMarket/0.1 (contact: me@example.com)"')
    with OSRSGEMarket(agent) as ge:
        snapshot = ge.market_snapshot(volume_window='1h')
        ge.save_snapshot(snapshot, 'ge_snapshots')
        flips = ge.flip_candidates(snapshot, budget_gp=10_000_000, min_volume=100)
        print(flips.head(15).to_string(index=False))
