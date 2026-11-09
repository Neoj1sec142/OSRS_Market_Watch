namespace MarketAPI.Models;
public class MarketWatchlist
{
    public long Id { get; set; }

    public int MarketItemId { get; set; }
    public MarketItem MarketItem { get; set; } = null!;

    public DateTime AddedAtUtc { get; set; }

    public long? TargetBuyPriceGp { get; set; }
    public long? TargetSellPriceGp { get; set; }

    public decimal? MinimumProfitMarginPct { get; set; }

    public bool AlertsEnabled { get; set; } = true;

    public string? Notes { get; set; }
}