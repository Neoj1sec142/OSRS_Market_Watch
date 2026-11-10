
using MarketAPI.Data;
using MarketAPI.Enums;
using MarketAPI.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Diagnostics;

namespace MarketAPI.Services;

public sealed record MarketIngestionResult(
    long CollectionBatchId,
    int ItemsMapped,
    int SnapshotsInserted,
    int AggregatesAffected,
    DateTime CompletedAtUtc
);

public sealed class MarketIngestionService
{
    private const string MetadataCacheKey =
        "market:metadata:refresh-state";

    private static readonly TimeSpan MetadataRefreshInterval =
        TimeSpan.FromHours(24);

    private static readonly TimeSpan MetadataFailureCooldown =
        TimeSpan.FromMinutes(30);
    private readonly OsrsWikiClient _wiki;
    private readonly MarketDbContext _db;
    private readonly SqliteMarketBulkWriter _writer;
    private readonly DatabaseWriteCoordinator _coordinator;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MarketIngestionService> _logger;

    public MarketIngestionService(
        OsrsWikiClient wiki,
        MarketDbContext db,
        SqliteMarketBulkWriter writer,
        DatabaseWriteCoordinator coordinator,
        IMemoryCache cache,
        ILogger<MarketIngestionService> logger)
    {
        _wiki = wiki;
        _db = db;
        _writer = writer;
        _coordinator = coordinator;
        _cache = cache;
        _logger = logger;
    }

    public async Task<MarketIngestionResult> IngestAsync(
        CancellationToken ct = default)
    {
        
        var totalTimer = Stopwatch.StartNew();
        var fetchTimer = Stopwatch.StartNew();

        // Load known item IDs directly from SQLite.
        // Price collection can use these even when /mapping fails.
        var knownItemIds = await _db.MarketItems
            .AsNoTracking()
            .Select(x => x.Id)
            .ToHashSetAsync(ct);

        List<WikiItemDto>? items = null;

        // Refresh mapping on first startup, after cache expiration,
        // or when the database has no item metadata.
        bool refreshMetadata =
            knownItemIds.Count == 0 ||
            !_cache.TryGetValue(MetadataCacheKey, out _);

        if (refreshMetadata)
        {
            try
            {
                items = await _wiki.GetItemsAsync(ct);

                if (items.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Wiki mapping response was empty.");
                }

                _logger.LogInformation(
                    "Fetched {Count} Wiki item definitions.",
                    items.Count);
            }
            catch (Exception ex)
                when (!ct.IsCancellationRequested &&
                    knownItemIds.Count > 0)
            {
                // Metadata refresh failure is nonfatal when we
                // already have item definitions in SQLite.
                _logger.LogWarning(
                    ex,
                    "Wiki metadata refresh failed. " +
                    "Continuing with {Count} existing items.",
                    knownItemIds.Count);

                // Try mapping again after a short cooldown.
                // Do not keep requesting it every five minutes
                // while the source is unavailable.
                _cache.Set(
                    MetadataCacheKey,
                    true,
                    MetadataFailureCooldown);
            }
        }

        // Market price requests remain mandatory.
        var latest = await _wiki.GetLatestAsync(ct);
        var fiveMinute = await _wiki.GetFiveMinuteAsync(ct);
        var hourly = await _wiki.GetHourlyAsync(ct);

        fetchTimer.Stop();

        if (latest.Data.Count == 0 ||
            fiveMinute.Data.Count == 0 ||
            hourly.Data.Count == 0)
        {
            throw new InvalidOperationException(
                "One or more market price datasets were empty.");
        }

        // Combine existing item IDs with newly fetched definitions.
        var itemIds = knownItemIds;

        if (items != null)
        {
            itemIds.UnionWith(items.Select(x => x.Id));
        }

        if (itemIds.Count == 0)
        {
            throw new InvalidOperationException(
                "No known market item IDs are available.");
        }


        // Prevent our own ingestion/retention jobs from
        // attempting simultaneous writes in this process.
        return await _coordinator.ExecuteAsync(
            async token =>
            {
                var collectedAtUtc = DateTime.UtcNow;
                var metadataTimer = Stopwatch.StartNew();

                if (items != null)
                {
                    await SynchronizeItemsAsync(
                        items, collectedAtUtc, token);

                    // Set this only after metadata was successfully
                    // synchronized with SQLite.
                    _cache.Set(
                        MetadataCacheKey,
                        true,
                        MetadataRefreshInterval);
                }
                else
                {
                    _logger.LogDebug(
                        "Metadata synchronization skipped; " +
                        "using {Count} existing item IDs.",
                        knownItemIds.Count);
                }

                metadataTimer.Stop();

                var snapshots = BuildSnapshots(
                    latest, itemIds, collectedAtUtc);

                var aggregates = BuildAggregates(
                    fiveMinute,
                    AggregateInterval.FiveMinutes,
                    itemIds,
                    collectedAtUtc);

                aggregates.AddRange(BuildAggregates(
                    hourly,
                    AggregateInterval.OneHour,
                    itemIds,
                    collectedAtUtc));

                var batch = new MarketCollectionBatch
                {
                    StartedAtUtc = collectedAtUtc,
                    Source = "OSRSWiki",
                    Status = CollectionStatus.Running
                };

                _db.MarketCollectionBatches.Add(batch);
                await _db.SaveChangesAsync(token);

                try
                {
                    foreach (var snapshot in snapshots)
                        snapshot.CollectionBatchId = batch.Id;
                    var snapshotTimer = Stopwatch.StartNew();
                    int inserted = await _writer
                        .InsertSnapshotsAsync(snapshots, token);
                    snapshotTimer.Stop();
                    var aggregateTimer = Stopwatch.StartNew();
                    int affected = await _writer
                        .UpsertAggregatesAsync(aggregates, token);
                    aggregateTimer.Stop();
                    batch.Status = CollectionStatus.Completed;
                    batch.CompletedAtUtc = DateTime.UtcNow;
                    batch.ItemsCollected = inserted;

                    await _db.SaveChangesAsync(token);

                    _logger.LogInformation(
                        "GE ingestion {BatchId}: " +
                        "{Items} items, {Snapshots} snapshots, " +
                        "{Aggregates} aggregate writes.",
                        batch.Id,
                        itemIds.Count,
                        inserted,
                        affected);

                    totalTimer.Stop();

                    _logger.LogInformation(
                        "GE ingestion performance | " +
                        "Fetch={FetchMs}ms | " +
                        "Metadata={MetadataMs}ms | " +
                        "Snapshots={SnapshotMs}ms | " +
                        "Aggregates={AggregateMs}ms | " +
                        "Total={TotalMs}ms | " +
                        "Snapshot candidates={Candidates} | " +
                        "Snapshots inserted={Inserted}",
                        fetchTimer.ElapsedMilliseconds,
                        metadataTimer.ElapsedMilliseconds,
                        snapshotTimer.ElapsedMilliseconds,
                        aggregateTimer.ElapsedMilliseconds,
                        totalTimer.ElapsedMilliseconds,
                        snapshots.Count,
                        inserted);

                    return new MarketIngestionResult(
                        batch.Id,
                        itemIds.Count,
                        inserted,
                        affected,
                        batch.CompletedAtUtc.Value);
                }
                catch (Exception ex)
                {
                    batch.Status = CollectionStatus.Failed;
                    batch.CompletedAtUtc = DateTime.UtcNow;
                    batch.ErrorMessage = ex.Message.Length > 4000
                        ? ex.Message[..4000]
                        : ex.Message;

                    try
                    {
                        await _db.SaveChangesAsync(
                            CancellationToken.None);
                    }
                    catch (Exception saveEx)
                    {
                        _logger.LogError(saveEx,
                            "Could not mark batch {BatchId} failed.",
                            batch.Id);
                    }

                    throw;
                }
            },
            ct);
    }

    private async Task SynchronizeItemsAsync(
        List<WikiItemDto> items,
        DateTime now,
        CancellationToken ct)
    {
        var existing = await _db.MarketItems
            .ToDictionaryAsync(x => x.Id, ct);

        int added = 0;
        int updated = 0;

        foreach (var source in items)
        {
            var name = Trim(source.Name, 200) ?? string.Empty;
            var examine = Trim(source.Examine, 2000);
            var icon = Trim(source.Icon, 500);

            if (!existing.TryGetValue(source.Id, out var item))
            {
                _db.MarketItems.Add(new MarketItem
                {
                    Id = source.Id,
                    Name = name,
                    Examine = examine,
                    Icon = icon,
                    Members = source.Members,
                    BuyLimit = source.BuyLimit,
                    HighAlchValue = source.HighAlchValue,
                    LowAlchValue = source.LowAlchValue,
                    IsActive = true,
                    LastMetadataUpdateUtc = now
                });

                added++;
                continue;
            }

            bool changed =
                item.Name != name ||
                item.Examine != examine ||
                item.Icon != icon ||
                item.Members != source.Members ||
                item.BuyLimit != source.BuyLimit ||
                item.HighAlchValue != source.HighAlchValue ||
                item.LowAlchValue != source.LowAlchValue;

            if (!changed)
                continue;

            item.Name = name;
            item.Examine = examine;
            item.Icon = icon;
            item.Members = source.Members;
            item.BuyLimit = source.BuyLimit;
            item.HighAlchValue = source.HighAlchValue;
            item.LowAlchValue = source.LowAlchValue;
            item.LastMetadataUpdateUtc = now;

            updated++;
        }

        if (added > 0 || updated > 0)
            await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Metadata synchronization: {Added} added, " +
            "{Updated} updated, {Unchanged} unchanged.",
            added,
            updated,
            items.Count - added - updated);
    }

    private static List<MarketPriceSnapshot> BuildSnapshots(
        WikiLatestResponse response,
        HashSet<int> itemIds,
        DateTime now)
    {
        var results = new List<MarketPriceSnapshot>(
            response.Data.Count);

        foreach (var entry in response.Data)
        {
            if (!int.TryParse(entry.Key, out int itemId) ||
                !itemIds.Contains(itemId))
                continue;

            var price = entry.Value;

            results.Add(new MarketPriceSnapshot
            {
                MarketItemId = itemId,
                InstantBuyPrice = price.High,
                InstantSellPrice = price.Low,
                InstantBuyTimeUtc = FromUnix(price.HighTime),
                InstantSellTimeUtc = FromUnix(price.LowTime),
                CollectedAtUtc = now
            });
        }

        return results;
    }

    private static List<MarketPriceAggregate> BuildAggregates(
        WikiAggregateResponse response,
        AggregateInterval interval,
        HashSet<int> itemIds,
        DateTime now)
    {
        var results = new List<MarketPriceAggregate>(
            response.Data.Count);

        var bucketStartUtc = DateTimeOffset
            .FromUnixTimeSeconds(response.Timestamp)
            .UtcDateTime;

        foreach (var entry in response.Data)
        {
            if (!int.TryParse(entry.Key, out int itemId) ||
                !itemIds.Contains(itemId))
                continue;

            var price = entry.Value;

            results.Add(new MarketPriceAggregate
            {
                MarketItemId = itemId,
                Interval = interval,
                BucketStartUtc = bucketStartUtc,

                AverageInstantBuyPrice =
                    price.AverageHighPrice,

                AverageInstantSellPrice =
                    price.AverageLowPrice,

                InstantBuyVolume = price.HighPriceVolume,
                InstantSellVolume = price.LowPriceVolume,

                CollectedAtUtc = now
            });
        }

        return results;
    }

    private static DateTime? FromUnix(long? seconds)
    {
        if (!seconds.HasValue)
            return null;

        return DateTimeOffset
            .FromUnixTimeSeconds(seconds.Value)
            .UtcDateTime;
    }

    private static string? Trim(string? value, int length)
    {
        if (value == null)
            return null;

        return value.Length <= length
            ? value
            : value[..length];
    }
}
