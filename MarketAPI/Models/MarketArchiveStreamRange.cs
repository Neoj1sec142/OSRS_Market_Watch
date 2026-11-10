
using MarketAPI.Enums;

namespace MarketAPI.Models;

public sealed class MarketArchiveStreamRange
{
    public long Id { get; set; }

    public Guid ArchiveExportId { get; set; }

    public MarketArchiveExport ArchiveExport { get; set; }
        = null!;

    public ArchiveStream Stream { get; set; }

    // Used by the append-only snapshot stream:
    // (StartIdExclusive, EndIdInclusive]
    public long? StartIdExclusive { get; set; }
    public long? EndIdInclusive { get; set; }

    // Used by time-bucket aggregate streams:
    // [StartUtcInclusive, EndUtcExclusive)
    public DateTime? StartUtcInclusive { get; set; }
    public DateTime? EndUtcExclusive { get; set; }

    public long RecordCount { get; set; }
}
