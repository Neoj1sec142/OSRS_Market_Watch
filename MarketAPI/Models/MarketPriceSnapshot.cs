namespace MarketAPI.Models;
public class MarketPriceSnapshot
{
    public long Id { get; set; }

    public int MarketItemId { get; set; }
    public MarketItem MarketItem { get; set; } = null!;

    public long CollectionBatchId { get; set; }
    public MarketCollectionBatch CollectionBatch { get; set; } = null!;

    // Latest observed instant-buy price
    public long? InstantBuyPrice { get; set; }

    // Latest observed instant-sell price
    public long? InstantSellPrice { get; set; }

    // Each side has its own source timestamp
    public DateTime? InstantBuyTimeUtc { get; set; }
    public DateTime? InstantSellTimeUtc { get; set; }

    // When we fetched this observation
    public DateTime CollectedAtUtc { get; set; }
}