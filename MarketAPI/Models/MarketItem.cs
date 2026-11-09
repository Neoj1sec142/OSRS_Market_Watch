

using System.ComponentModel.DataAnnotations;

namespace MarketAPI.Models;
public class MarketItem
{
    // OSRS item ID
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Examine { get; set; }
    public string? Icon { get; set; }

    public bool Members { get; set; }
    public int? BuyLimit { get; set; }

    public long? HighAlchValue { get; set; }
    public long? LowAlchValue { get; set; }

    public bool IsActive { get; set; } = true;
    public bool IsTaxExempt { get; set; }

    public DateTime LastMetadataUpdateUtc { get; set; }

    public ICollection<MarketPriceSnapshot> PriceSnapshots { get; set; }
        = new List<MarketPriceSnapshot>();

    public ICollection<MarketPriceAggregate> PriceAggregates { get; set; }
        = new List<MarketPriceAggregate>();
}