using MarketAPI.Enums;

namespace MarketAPI.Models;
public class TradeExecution
{
    public long Id { get; set; }

    public Guid TradePositionId { get; set; }
    public TradePosition TradePosition { get; set; } = null!;

    public TradeSide Side { get; set; }

    public DateTime ExecutedAtUtc { get; set; }

    // Filled quantity, supports partial fills
    public int Quantity { get; set; }

    public long PricePerItemGp { get; set; }
    public long TotalValueGp { get; set; }
    public long TaxPaidGp { get; set; }

    // Manual entry, import, or simulation
    public ExecutionSource Source { get; set; }
}