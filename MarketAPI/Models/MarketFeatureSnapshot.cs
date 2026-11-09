namespace MarketAPI.Models;
public class MarketFeatureSnapshot
{
    public long Id { get; set; }

    public int MarketItemId { get; set; }
    public MarketItem MarketItem { get; set; } = null!;

    public DateTime FeatureTimeUtc { get; set; }

    // Version of the feature calculation pipeline
    public string FeatureVersion { get; set; } = "1.0";

    // Current market conditions
    public long? InstantBuyPrice { get; set; }
    public long? InstantSellPrice { get; set; }

    public long? SpreadGp { get; set; }
    public decimal? SpreadPercent { get; set; }

    public long? EstimatedProfitPerItemGp { get; set; }
    public decimal? EstimatedRoiPercent { get; set; }

    // Price momentum, expressed as percentage changes
    public decimal? PriceChange1HourPct { get; set; }
    public decimal? PriceChange6HoursPct { get; set; }
    public decimal? PriceChange24HoursPct { get; set; }

    // Rolling price indicators
    public decimal? Sma1Hour { get; set; }
    public decimal? Sma24Hours { get; set; }

    public decimal? Ema1Hour { get; set; }
    public decimal? Ema24Hours { get; set; }

    public decimal? Volatility24Hours { get; set; }

    // Market liquidity
    public long? Volume1Hour { get; set; }
    public long? Volume24Hours { get; set; }

    public decimal? VolumeChangePercent { get; set; }
    public decimal? BuySellVolumeRatio { get; set; }

    // Data freshness
    public decimal? BuyQuoteAgeMinutes { get; set; }
    public decimal? SellQuoteAgeMinutes { get; set; }

    // Statistical input quality
    public int ObservationsUsed { get; set; }
    public bool IsDataSufficient { get; set; }
}