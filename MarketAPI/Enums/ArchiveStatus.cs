
namespace MarketAPI.Enums;

public enum ArchiveStatus
{
    Prepared = 0,
    Exported = 1,
    Transferred = 2,
    Verified = 3,
    Committed = 4,
    Failed = 5
}

public enum ArchiveStream
{
    Snapshots = 0,
    FiveMinuteAggregates = 1,
    HourlyAggregates = 2
}
