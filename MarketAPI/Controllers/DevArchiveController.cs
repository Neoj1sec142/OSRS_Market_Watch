
using MarketAPI.Data;
using MarketAPI.Enums;
using MarketAPI.Models;
using MarketAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketAPI.Controllers;
public sealed record FailArchiveRequest(string Reason);
[ApiController]
[Route("api/dev/archive")]
public sealed class DevArchiveController : ControllerBase
{
    private readonly MarketArchivePlanningService _planner;
    private readonly MarketArchiveLedgerService _ledger;
    private readonly IDbContextFactory<MarketDbContext> _factory;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DevArchiveController> _logger;

    public DevArchiveController(
        MarketArchivePlanningService planner,
        MarketArchiveLedgerService ledger,
        IDbContextFactory<MarketDbContext> factory,
        IWebHostEnvironment environment,
        ILogger<DevArchiveController> logger)
    {
        _planner = planner;
        _ledger = ledger;
        _factory = factory;
        _environment = environment;
        _logger = logger;
    }

    // GET /api/dev/archive/status
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(
        CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
            return NotFound();

        await using var db =
            await _factory.CreateDbContextAsync(ct);

        var source = await db.MarketArchiveSources
            .AsNoTracking()
            .SingleOrDefaultAsync(ct);

        var archives = await db.MarketArchiveExports
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new
            {
                x.Id,
                x.ArchiveFileName,
                x.Status,
                x.CreatedAtUtc,
                x.CommittedAtUtc,
                RangeCount = x.Ranges.Count
            })
            .Take(25)
            .ToListAsync(ct);

        var committedRanges =
            await db.MarketArchiveStreamRanges
                .AsNoTracking()
                .Where(x =>
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

        return Ok(new
        {
            SourceId = source?.SourceId,
            SourceCreatedAtUtc = source?.CreatedAtUtc,
            TotalArchives = await db.MarketArchiveExports
                .CountAsync(ct),
            Archives = archives,
            CommittedRanges = committedRanges
        });
    }


    [HttpGet("coverage")]
    public async Task<IActionResult> GetCoverage(
        CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
            return NotFound();

        var report = await _ledger.InspectCoverageAsync(ct);

        return Ok(report);
    }


    [HttpPost("{id:guid}/fail")]
    public async Task<IActionResult> FailPreparedArchive(
        Guid id,
        [FromBody] FailArchiveRequest request,
        CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new
            {
                Message = "A failure reason is required."
            });
        }

        try
        {
            await _ledger.FailPreparedAsync(
                id,
                request.Reason,
                ct);

            return Ok(new
            {
                ArchiveId = id,
                Status = ArchiveStatus.Failed.ToString(),
                Message = "Prepared reservation marked Failed."
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new
            {
                Message = ex.Message
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new
            {
                Message = ex.Message
            });
        }
    }



    // GET /api/dev/archive/{id}
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetArchive(
        Guid id,
        CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
            return NotFound();

        await using var db =
            await _factory.CreateDbContextAsync(ct);

        var archive = await db.MarketArchiveExports
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id,
                x.SourceId,
                x.ArchiveFileName,
                x.SchemaVersion,
                x.Status,
                x.CreatedAtUtc,
                x.ExportedAtUtc,
                x.TransferredAtUtc,
                x.VerifiedAtUtc,
                x.CommittedAtUtc,
                x.ArchiveSha256,
                x.ArchiveSizeBytes,
                x.ManifestSha256,
                x.StorageLocation,
                x.ErrorMessage,
                Ranges = x.Ranges.Select(r => new
                {
                    r.Stream,
                    r.StartIdExclusive,
                    r.EndIdInclusive,
                    r.StartUtcInclusive,
                    r.EndUtcExclusive,
                    r.RecordCount
                }).ToList()
            })
            .SingleOrDefaultAsync(ct);

        if (archive == null)
            return NotFound();

        return Ok(archive);
    }


    [HttpGet("{id:guid}/validate")]
    public async Task<IActionResult> ValidatePlan(
        Guid id,
        CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
            return NotFound();

        await using var db =
            await _factory.CreateDbContextAsync(ct);

        var archive = await db.MarketArchiveExports
            .AsNoTracking()
            .Include(x => x.Ranges)
            .SingleOrDefaultAsync(x => x.Id == id, ct);

        if (archive == null)
            return NotFound();

        var sourceId = await db.MarketArchiveSources
            .AsNoTracking()
            .Where(x => x.Id == 1)
            .Select(x => (Guid?)x.SourceId)
            .SingleOrDefaultAsync(ct);

        bool sourceValid =
            sourceId.HasValue &&
            sourceId.Value == archive.SourceId;

        bool statusValid =
            archive.Status == ArchiveStatus.Prepared;

        var results = new List<object>();
        bool allValid = sourceValid && statusValid;

        foreach (var range in archive.Ranges
            .OrderBy(x => x.Stream))
        {
            long actualCount = 0;
            long ineligibleCount = 0;
            bool boundariesValid = false;
            bool overlapsExisting = false;

            if (range.Stream == ArchiveStream.Snapshots &&
                range.StartIdExclusive.HasValue &&
                range.EndIdInclusive.HasValue)
            {
                long start = range.StartIdExclusive.Value;
                long end = range.EndIdInclusive.Value;

                boundariesValid =
                    start >= 0 &&
                    end > start &&
                    !range.StartUtcInclusive.HasValue &&
                    !range.EndUtcExclusive.HasValue;

                if (boundariesValid)
                {
                    var cutoff = archive.CreatedAtUtc
                        .Subtract(TimeSpan.FromHours(48));

                    var query = db.MarketPriceSnapshots
                        .AsNoTracking()
                        .Where(x => x.Id > start && x.Id <= end);

                    actualCount = await query.LongCountAsync(ct);

                    ineligibleCount = await query
                        .LongCountAsync(
                            x => x.CollectedAtUtc >= cutoff,
                            ct);

                    overlapsExisting =
                        await db.MarketArchiveStreamRanges
                            .AsNoTracking()
                            .AnyAsync(x =>
                                x.ArchiveExportId != id &&
                                x.ArchiveExport.SourceId ==
                                    archive.SourceId &&
                                x.ArchiveExport.Status !=
                                    ArchiveStatus.Failed &&
                                x.Stream == ArchiveStream.Snapshots &&
                                x.StartIdExclusive < end &&
                                x.EndIdInclusive > start,
                                ct);
                }
            }
            else if (range.Stream ==
                        ArchiveStream.FiveMinuteAggregates ||
                    range.Stream ==
                        ArchiveStream.HourlyAggregates)
            {
                bool fiveMinute =
                    range.Stream ==
                        ArchiveStream.FiveMinuteAggregates;

                var interval = fiveMinute
                    ? AggregateInterval.FiveMinutes
                    : AggregateInterval.OneHour;

                var duration = fiveMinute
                    ? TimeSpan.FromMinutes(5)
                    : TimeSpan.FromHours(1);

                var delay = fiveMinute
                    ? TimeSpan.FromHours(6)
                    : TimeSpan.FromHours(24);

                if (range.StartUtcInclusive.HasValue &&
                    range.EndUtcExclusive.HasValue)
                {
                    var start = range.StartUtcInclusive.Value;
                    var end = range.EndUtcExclusive.Value;

                    var cutoff = archive.CreatedAtUtc
                        .Subtract(delay);

                    var boundaryTicks =
                        cutoff.Ticks -
                        cutoff.Ticks % duration.Ticks;

                    var eligibleEnd = new DateTime(
                        boundaryTicks, DateTimeKind.Utc);

                    boundariesValid =
                        start < end &&
                        start.Ticks % duration.Ticks == 0 &&
                        end.Ticks % duration.Ticks == 0 &&
                        end <= eligibleEnd &&
                        !range.StartIdExclusive.HasValue &&
                        !range.EndIdInclusive.HasValue;

                    if (boundariesValid)
                    {
                        actualCount =
                            await db.MarketPriceAggregates
                                .AsNoTracking()
                                .LongCountAsync(x =>
                                    x.Interval == interval &&
                                    x.BucketStartUtc >= start &&
                                    x.BucketStartUtc < end,
                                    ct);

                        overlapsExisting =
                            await db.MarketArchiveStreamRanges
                                .AsNoTracking()
                                .AnyAsync(x =>
                                    x.ArchiveExportId != id &&
                                    x.ArchiveExport.SourceId ==
                                        archive.SourceId &&
                                    x.ArchiveExport.Status !=
                                        ArchiveStatus.Failed &&
                                    x.Stream == range.Stream &&
                                    x.StartUtcInclusive < end &&
                                    x.EndUtcExclusive > start,
                                    ct);
                    }
                }
            }

            bool countMatches =
                boundariesValid &&
                actualCount > 0 &&
                actualCount == range.RecordCount;

            bool valid =
                boundariesValid &&
                countMatches &&
                ineligibleCount == 0 &&
                !overlapsExisting;

            allValid &= valid;

            results.Add(new
            {
                Stream = range.Stream.ToString(),
                range.RecordCount,
                ActualCount = actualCount,
                IneligibleCount = ineligibleCount,
                BoundariesValid = boundariesValid,
                CountMatches = countMatches,
                OverlapsExisting = overlapsExisting,
                Valid = valid
            });
        }

        if (archive.Ranges.Count == 0 ||
            archive.Ranges.Select(x => x.Stream)
                .Distinct().Count() != archive.Ranges.Count)
        {
            allValid = false;
        }

        return Ok(new
        {
            archive.Id,
            archive.SourceId,
            Status = archive.Status.ToString(),
            SourceValid = sourceValid,
            StatusValid = statusValid,
            Valid = allValid,
            Ranges = results,
            Note = "Preliminary source-range validation only. " +
                "Does not verify exported file contents, " +
                "historical corrections, or complete " +
                "committed archive coverage."
        });
    }


    // POST /api/dev/archive/prepare
    [HttpPost("prepare")]
    public async Task<IActionResult> Prepare(
        CancellationToken ct)
    {
        if (!_environment.IsDevelopment())
            return NotFound();

        try
        {
            var result = await _planner.PrepareNextAsync(ct);

            if (result == null)
            {
                return Ok(new
                {
                    Message =
                        "No historical data eligible for archival."
                });
            }

            return CreatedAtAction(
                nameof(GetArchive),
                new { id = result.ArchiveId },
                new
                {
                    result.ArchiveId,
                    result.SourceId,
                    result.ArchiveFileName,
                    Status = ArchiveStatus.Prepared.ToString(),
                    Ranges = result.Ranges.Select(r => new
                    {
                        r.Stream,
                        r.StartIdExclusive,
                        r.EndIdInclusive,
                        r.StartUtcInclusive,
                        r.EndUtcExclusive,
                        r.RecordCount
                    })
                });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Archive preparation rejected.");

            return Conflict(new
            {
                Message = ex.Message
            });
        }
    }
}
