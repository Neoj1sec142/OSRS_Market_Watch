using MarketAPI.Enums;

namespace MarketAPI.Models;

public class TradePosition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int MarketItemId { get; set; }
    public MarketItem MarketItem { get; set; } = null!;

    public Guid? TradingStrategyId { get; set; }
    public TradingStrategy? TradingStrategy { get; set; }

    public long? MarketSignalId { get; set; }
    public MarketSignal? MarketSignal { get; set; }

    public TradeMode Mode { get; set; }
    public TradePositionStatus Status { get; set; }

    public DateTime OpenedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }

    public int QuantityBought { get; set; }
    public int QuantitySold { get; set; }

    public long TotalBuyCostGp { get; set; }
    public long TotalSaleRevenueGp { get; set; }
    public long TotalTaxGp { get; set; }

    // Only final when position is fully closed
    public long? RealizedProfitGp { get; set; }

    public ICollection<TradeExecution> Executions { get; set; }
        = new List<TradeExecution>();
}
