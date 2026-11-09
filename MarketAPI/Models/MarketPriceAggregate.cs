using MarketAPI.Enums;

namespace MarketAPI.Models;
public class MarketPriceAggregate
{
    public long Id { get; set; }

    public int MarketItemId { get; set; }
    public MarketItem MarketItem { get; set; } = null!;

    public AggregateInterval Interval { get; set; }

    // Beginning of the source's aggregation bucket
    public DateTime BucketStartUtc { get; set; }

    public decimal? AverageInstantBuyPrice { get; set; }
    public decimal? AverageInstantSellPrice { get; set; }

    public long InstantBuyVolume { get; set; }
    public long InstantSellVolume { get; set; }

    public DateTime CollectedAtUtc { get; set; }
}
