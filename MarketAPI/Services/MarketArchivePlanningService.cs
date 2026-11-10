
using MarketAPI.Data;
using MarketAPI.Enums;
using MarketAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace MarketAPI.Services;

public sealed record MarketArchivePlanResult(
    Guid ArchiveId,
    Guid SourceId,
    string ArchiveFileName,
    IReadOnlyList<MarketArchiveStreamRange> Ranges);

public sealed class MarketArchivePlanningService
{
    private static readonly TimeSpan SnapshotDelay =
        TimeSpan.FromHours(48);

    private static readonly TimeSpan FiveMinuteDelay =
        TimeSpan.FromHours(6);

    private static readonly TimeSpan HourlyDelay =
        TimeSpan.FromHours(24);

    private readonly IDbContextFactory<MarketDbContext> _factory;
    private readonly DatabaseWriteCoordinator _coordinator;
    private readonly ILogger<MarketArchivePlanningService> _logger;

    public MarketArchivePlanningService(
        IDbContextFactory<MarketDbContext> factory,
        DatabaseWriteCoordinator coordinator,
        ILogger<MarketArchivePlanningService> logger)
    {
        _factory = factory;
        _coordinator = coordinator;
        _logger = logger;
    }

    public Task<MarketArchivePlanResult?> PrepareNextAsync(
        CancellationToken ct = default)
    {
        return _coordinator.ExecuteAsync(
            async token =>
            {
                await using var db = await _factory
                    .CreateDbContextAsync(token);

                await using var transaction =
                    await db.Database.BeginTransactionAsync(token);

                var now = DateTime.UtcNow;

                // A database's source ID is created exactly once.
                var source = await db.MarketArchiveSources
                    .SingleOrDefaultAsync(token);

                if (source == null)
                {
                    source = new MarketArchiveSource
                    {
                        Id = 1,
                        SourceId = Guid.NewGuid(),
                        CreatedAtUtc = now
                    };

                    db.MarketArchiveSources.Add(source);

                    await db.SaveChangesAsync(token);
                }

                // Only one unfinished archive is allowed.
                var pending = await db.MarketArchiveExports
                    .AnyAsync(x =>
                        x.SourceId == source.SourceId &&
                        x.Status != ArchiveStatus.Committed &&
                        x.Status != ArchiveStatus.Failed,
                        token);

                if (pending)
                {
                    throw new InvalidOperationException(
                        "An unfinished archive already exists. " +
                        "Complete or explicitly fail it before " +
                        "preparing another archive.");
                }

                var archiveId = Guid.NewGuid();

                var ranges =
                    new List<MarketArchiveStreamRange>();

                var snapshotRange =
                    await PlanSnapshotsAsync(
                        db, source.SourceId,
                        archiveId, now, token);

                if (snapshotRange != null)
                    ranges.Add(snapshotRange);

                var fiveMinuteRange =
                    await PlanAggregatesAsync(
                        db, source.SourceId,
                        archiveId,
                        ArchiveStream.FiveMinuteAggregates,
                        AggregateInterval.FiveMinutes,
                        FiveMinuteDelay,
                        now, token);

                if (fiveMinuteRange != null)
                    ranges.Add(fiveMinuteRange);

                var hourlyRange =
                    await PlanAggregatesAsync(
                        db, source.SourceId,
                        archiveId,
                        ArchiveStream.HourlyAggregates,
                        AggregateInterval.OneHour,
                        HourlyDelay,
                        now, token);

                if (hourlyRange != null)
                    ranges.Add(hourlyRange);

                if (ranges.Count == 0)
                {
                    await transaction.CommitAsync(token);

                    _logger.LogInformation(
                        "No historical market data is currently " +
                        "eligible for archival.");

                    return null;
                }

                var archive = new MarketArchiveExport
                {
                    Id = archiveId,
                    SourceId = source.SourceId,
                    Status = ArchiveStatus.Prepared,
                    SchemaVersion = 1,
                    CreatedAtUtc = now,
                    ArchiveFileName =
                        $"market_{now:yyyyMMdd}_{archiveId:N}.db",
                    Ranges = ranges
                };

                db.MarketArchiveExports.Add(archive);

                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);

                _logger.LogInformation(
                    "Prepared archive {ArchiveId} with " +
                    "{RangeCount} stream ranges.",
                    archiveId,
                    ranges.Count);

                return new MarketArchivePlanResult(
                    archive.Id,
                    source.SourceId,
                    archive.ArchiveFileName,
                    ranges.AsReadOnly());
            },
            ct);
    }

    private static async Task<MarketArchiveStreamRange?>
        PlanSnapshotsAsync(
            MarketDbContext db,
            Guid sourceId,
            Guid archiveId,
            DateTime now,
            CancellationToken ct)
    {
        // Only committed ranges advance progress.
        long lastCommitted =
            await db.MarketArchiveStreamRanges
                .Where(x =>
                    x.Stream == ArchiveStream.Snapshots &&
                    x.ArchiveExport.SourceId == sourceId &&
                    x.ArchiveExport.Status ==
                        ArchiveStatus.Committed)
                .Select(x => x.EndIdInclusive)
                .MaxAsync(ct) ?? 0;

        var cutoff = now.Subtract(SnapshotDelay);

        // The first recent record forms a hard boundary.
        // Do not skip over it to export higher IDs.
        long? firstRecentId =
            await db.MarketPriceSnapshots
                .AsNoTracking()
                .Where(x =>
                    x.Id > lastCommitted &&
                    x.CollectedAtUtc >= cutoff)
                .Select(x => (long?)x.Id)
                .MinAsync(ct);

        var eligible = db.MarketPriceSnapshots
            .AsNoTracking()
            .Where(x =>
                x.Id > lastCommitted &&
                x.CollectedAtUtc < cutoff);

        if (firstRecentId.HasValue)
        {
            eligible = eligible.Where(
                x => x.Id < firstRecentId.Value);
        }

        long? endId = await eligible
            .Select(x => (long?)x.Id)
            .MaxAsync(ct);

        if (!endId.HasValue)
            return null;

        long count = await db.MarketPriceSnapshots
            .AsNoTracking()
            .LongCountAsync(x =>
                x.Id > lastCommitted &&
                x.Id <= endId.Value,
                ct);

        return new MarketArchiveStreamRange
        {
            ArchiveExportId = archiveId,
            Stream = ArchiveStream.Snapshots,
            StartIdExclusive = lastCommitted,
            EndIdInclusive = endId.Value,
            RecordCount = count
        };
    }

    
    private static async Task<MarketArchiveStreamRange?>
        PlanAggregatesAsync(
            MarketDbContext db,
            Guid sourceId,
            Guid archiveId,
            ArchiveStream stream,
            AggregateInterval interval,
            TimeSpan finalizationDelay,
            DateTime now,
            CancellationToken ct)
    {
        var bucketDuration = interval switch
        {
            AggregateInterval.FiveMinutes =>
                TimeSpan.FromMinutes(5),

            AggregateInterval.OneHour =>
                TimeSpan.FromHours(1),

            _ => throw new NotSupportedException(
                $"Unsupported archive interval: {interval}")
        };

        // The ENTIRE bucket must finish before the
        // configured finalization delay has elapsed.
        var cutoff = now.Subtract(finalizationDelay);

        var cutoffTicks =
            cutoff.Ticks -
            cutoff.Ticks % bucketDuration.Ticks;

        var endBoundary = new DateTime(
            cutoffTicks, DateTimeKind.Utc);

        DateTime? lastCommittedEnd =
            await db.MarketArchiveStreamRanges
                .AsNoTracking()
                .Where(x =>
                    x.Stream == stream &&
                    x.ArchiveExport.SourceId == sourceId &&
                    x.ArchiveExport.Status ==
                        ArchiveStatus.Committed)
                .Select(x => x.EndUtcExclusive)
                .MaxAsync(ct);

        var eligible = db.MarketPriceAggregates
            .AsNoTracking()
            .Where(x =>
                x.Interval == interval &&
                x.BucketStartUtc < endBoundary);

        if (lastCommittedEnd.HasValue)
        {
            eligible = eligible.Where(x =>
                x.BucketStartUtc >= lastCommittedEnd.Value);
        }

        DateTime? firstBucket = await eligible
            .Select(x => (DateTime?)x.BucketStartUtc)
            .MinAsync(ct);

        DateTime? lastBucket = await eligible
            .Select(x => (DateTime?)x.BucketStartUtc)
            .MaxAsync(ct);

        if (!firstBucket.HasValue ||
            !lastBucket.HasValue)
        {
            return null;
        }

        var startInclusive =
            lastCommittedEnd ?? firstBucket.Value;

        var endExclusive =
            lastBucket.Value.Add(bucketDuration);

        long count = await db.MarketPriceAggregates
            .AsNoTracking()
            .LongCountAsync(x =>
                x.Interval == interval &&
                x.BucketStartUtc >= startInclusive &&
                x.BucketStartUtc < endExclusive,
                ct);

        return new MarketArchiveStreamRange
        {
            ArchiveExportId = archiveId,
            Stream = stream,
            StartUtcInclusive = startInclusive,
            EndUtcExclusive = endExclusive,
            RecordCount = count
        };
    }

}
