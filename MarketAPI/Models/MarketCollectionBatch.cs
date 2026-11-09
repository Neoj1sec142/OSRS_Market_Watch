
using MarketAPI.Enums;

namespace MarketAPI.Models;
public class MarketCollectionBatch
{
    public long Id { get; set; }

    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public string Source { get; set; } = "OSRSWiki";

    public CollectionStatus Status { get; set; }

    public int ItemsCollected { get; set; }
    public string? ErrorMessage { get; set; }
}