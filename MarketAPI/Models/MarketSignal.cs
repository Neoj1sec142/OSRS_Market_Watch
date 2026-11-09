
using MarketAPI.Enums;

namespace MarketAPI.Models;
public class MarketSignal
{
    public long Id { get; set; }

    public int MarketItemId { get; set; }
    public MarketItem MarketItem { get; set; } = null!;

    public long? MarketPredictionId { get; set; }
    public MarketPrediction? MarketPrediction { get; set; }

    public Guid? TradingStrategyId { get; set; }

    public SignalAction Action { get; set; }

    public DateTime GeneratedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }

    public long? SuggestedBuyPriceGp { get; set; }
    public long? SuggestedSellPriceGp { get; set; }

    public int SuggestedQuantity { get; set; }
    public long EstimatedTotalProfitGp { get; set; }

    public decimal? ConfidenceScore { get; set; }

    // Human-readable explanation for dashboard
    public string? Reason { get; set; }

    public SignalStatus Status { get; set; }
}