
from osrs_ge_market import OSRSGEMarket 

with OSRSGEMarket(
    user_agent="NuggetCapital/0.1 (contact: neothej1sec@gmail.com)"
) as ge:

    # Collect current market data
    market = ge.market_snapshot(volume_window="1h")

    # Find potential flips within our bankroll
    flips = ge.flip_candidates(
        market,
        budget_gp=10_000_000,
        min_volume=100,
        min_profit_gp=1000,
        max_quote_age_minutes=30,
    )

    print(flips.head(20).to_string(index=False))

    # Analyze an item's historical movement
    history = ge.item_history(
        "Abyssal whip",
        lookback="30d"
    )

    features = ge.history_features(history, periods=12)

    print(features.tail(10).to_string(index=False))

    # Persist data for future ML training
    ge.save_snapshot(market, "./market_data")
