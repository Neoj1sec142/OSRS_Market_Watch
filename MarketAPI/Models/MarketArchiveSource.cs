
namespace MarketAPI.Models;

public sealed class MarketArchiveSource
{
    // Single source identity row per collection database.
    public int Id { get; set; } = 1;

    public Guid SourceId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
