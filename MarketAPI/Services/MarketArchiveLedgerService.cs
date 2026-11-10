
using MarketAPI.Data;
using MarketAPI.Enums;
using Microsoft.EntityFrameworkCore;

namespace MarketAPI.Services;

public sealed record ArchiveStreamCoverage(
    ArchiveStream Stream,
    bool Initialized,
    bool Contiguous,
    int CommittedRangeCount,
    long? SnapshotWatermark,
    DateTime? AggregateWatermarkUtc,
    string Message);

public sealed record ArchiveCoverageReport(
    Guid? SourceId,
    bool AllInitializedStreamsContiguous,
    bool SafeToAuthorizeCleanup,
    IReadOnlyList<ArchiveStreamCoverage> Streams);

public sealed class MarketArchiveLedgerService
{
    private readonly IDbContextFactory<MarketDbContext> _factory;
    private readonly DatabaseWriteCoordinator _coordinator;
    private readonly ILogger<MarketArchiveLedgerService> _logger;

    public MarketArchiveLedgerService(
        IDbContextFactory<MarketDbContext> factory,
        DatabaseWriteCoordinator coordinator,
        ILogger<MarketArchiveLedgerService> logger)
    {
        _factory = factory;
        _coordinator = coordinator;
        _logger = logger;
    }

    public async Task<ArchiveCoverageReport> InspectCoverageAsync(
        CancellationToken ct = default)
    {
        await using var db =
            await _factory.CreateDbContextAsync(ct);

        var source = await db.MarketArchiveSources
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == 1, ct);

        var results = new List<ArchiveStreamCoverage>();

        if (source == null)
        {
            foreach (var stream in Enum.GetValues<ArchiveStream>())
            {
                results.Add(new ArchiveStreamCoverage(
                    stream,
                    false,
                    false,
                    0,
                    null,
                    null,
                    "Source database identity is not initialized."));
            }

            return new ArchiveCoverageReport(
                null, false, false, results);
        }

        var ranges = await db.MarketArchiveStreamRanges
            .AsNoTracking()
            .Where(x =>
                x.ArchiveExport.SourceId == source.SourceId &&
                x.ArchiveExport.Status ==
                    ArchiveStatus.Committed)
            .Select(x => new
            {
                x.Stream,
                x.StartIdExclusive,
                x.EndIdInclusive,
                x.StartUtcInclusive,
                x.EndUtcExclusive,
                x.RecordCount
            })
            .ToListAsync(ct);

        foreach (var stream in Enum.GetValues<ArchiveStream>())
        {
            var streamRanges = ranges
                .Where(x => x.Stream == stream)
                .ToList();

            if (streamRanges.Count == 0)
            {
                results.Add(new ArchiveStreamCoverage(
                    stream,
                    false,
                    false,
                    0,
                    null,
                    null,
                    "No committed ranges exist for this stream."));

                continue;
            }

            bool valid = true;
            string message = "Committed ranges are contiguous.";

            long? snapshotWatermark = null;
            DateTime? aggregateWatermark = null;

            if (stream == ArchiveStream.Snapshots)
            {
                var ordered = streamRanges
                    .OrderBy(x => x.StartIdExclusive)
                    .ToList();

                long expectedStart = 0;

                foreach (var range in ordered)
                {
                    if (!range.StartIdExclusive.HasValue ||
                        !range.EndIdInclusive.HasValue ||
                        range.StartUtcInclusive.HasValue ||
                        range.EndUtcExclusive.HasValue ||
                        range.RecordCount <= 0 ||
                        range.StartIdExclusive.Value != expectedStart ||
                        range.EndIdInclusive.Value <= expectedStart)
                    {
                        valid = false;
                        message =
                            "Gap, overlap, or invalid snapshot range.";
                        break;
                    }

                    expectedStart = range.EndIdInclusive.Value;
                }

                if (valid)
                    snapshotWatermark = expectedStart;
            }
            else
            {
                var duration = stream switch
                {
                    ArchiveStream.FiveMinuteAggregates =>
                        TimeSpan.FromMinutes(5),

                    ArchiveStream.HourlyAggregates =>
                        TimeSpan.FromHours(1),

                    _ => throw new NotSupportedException(
                        $"Unknown archive stream: {stream}")
                };

                var ordered = streamRanges
                    .OrderBy(x => x.StartUtcInclusive)
                    .ToList();

                DateTime? expectedStart = null;

                foreach (var range in ordered)
                {
                    if (!range.StartUtcInclusive.HasValue ||
                        !range.EndUtcExclusive.HasValue ||
                        range.StartIdExclusive.HasValue ||
                        range.EndIdInclusive.HasValue ||
                        range.RecordCount <= 0)
                    {
                        valid = false;
                        message = "Invalid aggregate range fields.";
                        break;
                    }

                    var start = range.StartUtcInclusive.Value;
                    var end = range.EndUtcExclusive.Value;

                    if (start >= end ||
                        start.Ticks % duration.Ticks != 0 ||
                        end.Ticks % duration.Ticks != 0 ||
                        (expectedStart.HasValue &&
                         start != expectedStart.Value))
                    {
                        valid = false;
                        message =
                            "Gap, overlap, or misaligned aggregate range.";
                        break;
                    }

                    expectedStart = end;
                }

                if (valid)
                    aggregateWatermark = expectedStart;
            }

            results.Add(new ArchiveStreamCoverage(
                stream,
                true,
                valid,
                streamRanges.Count,
                snapshotWatermark,
                aggregateWatermark,
                message));
        }

        // IMPORTANT:
        // Structural range continuity is not sufficient
        // to authorize deletion.
        // Verified file coverage and cleanup-specific
        // checks have not been implemented yet.
        return new ArchiveCoverageReport(
            source.SourceId,
            results
                .Where(x => x.Initialized)
                .All(x => x.Contiguous),
            false,
            results);
    }

    public async Task FailPreparedAsync(
        Guid archiveId,
        string reason,
        CancellationToken ct = default)
    {
        if (archiveId == Guid.Empty)
            throw new ArgumentException(
                "Archive ID is required.", nameof(archiveId));

        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException(
                "A failure reason is required.", nameof(reason));

        var errorMessage = reason.Trim();

        if (errorMessage.Length > 4000)
            throw new ArgumentException(
                "Failure reason exceeds 4000 characters.",
                nameof(reason));

        await _coordinator.ExecuteAsync(
            async token =>
            {
                await using var db =
                    await _factory.CreateDbContextAsync(token);

                // An atomic conditional update allows us to
                // reject a reservation that is no longer Prepared.
                //
                // Do not fail an archive once export or transfer
                // metadata has been recorded.
                int affected = await db.MarketArchiveExports
                    .Where(x =>
                        x.Id == archiveId &&
                        x.Status == ArchiveStatus.Prepared &&
                        x.ExportedAtUtc == null &&
                        x.TransferredAtUtc == null &&
                        x.VerifiedAtUtc == null &&
                        x.CommittedAtUtc == null &&
                        x.ArchiveSha256 == null &&
                        x.ArchiveSizeBytes == null &&
                        x.ManifestSha256 == null &&
                        x.StorageLocation == null)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(
                                x => x.Status,
                                ArchiveStatus.Failed)
                            .SetProperty(
                                x => x.ErrorMessage,
                                errorMessage),
                        token);

                if (affected != 1)
                {
                    throw new InvalidOperationException(
                        "Archive not found or not an unused " +
                        "Prepared reservation. No changes made.");
                }

                _logger.LogWarning(
                    "Archive {ArchiveId} marked Failed. Reason={Reason}",
                    archiveId,
                    errorMessage);
                return affected;
            },
            ct);
    }
}
