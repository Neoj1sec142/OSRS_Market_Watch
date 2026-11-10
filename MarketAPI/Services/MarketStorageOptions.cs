
using MarketAPI.Data;
using MarketAPI.Enums;
using MarketAPI.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace MarketAPI.Services;


public sealed class MarketStorageOptions
{
    // Phase 0 protection:
    // Historical record deletion is prohibited until
    // verified, archive-aware cleanup is implemented.
    public bool EnableDestructiveRetention { get; set; } = false;

    public int SnapshotRetentionDays { get; set; } = 14;
    public int FiveMinuteRetentionDays { get; set; } = 90;
    public int HourlyRetentionDays { get; set; } = 730;
    public int BatchRetentionDays { get; set; } = 30;

    public int InsertBatchSize { get; set; } = 500;
    public int DeleteBatchSize { get; set; } = 5000;
}


public sealed class MarketRetentionService
{
    private readonly IDbContextFactory<MarketDbContext> _factory;
    private readonly DatabaseWriteCoordinator _coordinator;
    private readonly MarketStorageOptions _options;
    private readonly ILogger<MarketRetentionService> _logger;

    private const int MaxPasses = 50;

    private static readonly TimeSpan MaxDuration =
        TimeSpan.FromSeconds(20);

    public MarketRetentionService(
        IDbContextFactory<MarketDbContext> factory,
        DatabaseWriteCoordinator coordinator,
        IOptions<MarketStorageOptions> options,
        ILogger<MarketRetentionService> logger)
    {
        _factory = factory;
        _coordinator = coordinator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Phase 0:
        // Never delete historical data without verified
        // archive coverage and a committed archive ledger.
        //
        // Archive-aware deletion is NOT implemented yet.
        if (_options.EnableDestructiveRetention)
        {
            throw new InvalidOperationException(
                "Destructive retention is prohibited until " +
                "archive coverage verification and committed " +
                "archive watermarks are implemented.");
        }

        _logger.LogDebug(
            "Market retention cleanup skipped. " +
            "Archive-first protection is active.");

        return;

        // var timer = Stopwatch.StartNew();
        // var now = DateTime.UtcNow;

        // int batchSize = Math.Clamp(
        //     _options.DeleteBatchSize, 100, 10000);

        // var snapshotCutoff = now.AddDays(
        //     -_options.SnapshotRetentionDays);

        // var fiveMinuteCutoff = now.AddDays(
        //     -_options.FiveMinuteRetentionDays);

        // var hourlyCutoff = now.AddDays(
        //     -_options.HourlyRetentionDays);

        // var batchCutoff = now.AddDays(
        //     -_options.BatchRetentionDays);

        // int totalDeleted = 0;
        // int passes = 0;

        // while (passes < MaxPasses &&
        //        timer.Elapsed < MaxDuration)
        // {
        //     ct.ThrowIfCancellationRequested();

        //     int deleted = await _coordinator.ExecuteAsync(
        //         async token =>
        //         {
        //             await using var db = await _factory
        //                 .CreateDbContextAsync(token);

        //             int snapshots = await db.Database
        //                 .ExecuteSqlInterpolatedAsync($"""
        //                     DELETE FROM MarketPriceSnapshots
        //                     WHERE Id IN (
        //                         SELECT Id
        //                         FROM MarketPriceSnapshots
        //                         WHERE CollectedAtUtc < {snapshotCutoff}
        //                         ORDER BY CollectedAtUtc
        //                         LIMIT {batchSize}
        //                     );
        //                     """, token);

        //             int fiveMinute = await db.Database
        //                 .ExecuteSqlInterpolatedAsync($"""
        //                     DELETE FROM MarketPriceAggregates
        //                     WHERE Id IN (
        //                         SELECT Id
        //                         FROM MarketPriceAggregates
        //                         WHERE Interval =
        //                             {(int)AggregateInterval.FiveMinutes}
        //                         AND BucketStartUtc < {fiveMinuteCutoff}
        //                         ORDER BY BucketStartUtc
        //                         LIMIT {batchSize}
        //                     );
        //                     """, token);

        //             int hourly = await db.Database
        //                 .ExecuteSqlInterpolatedAsync($"""
        //                     DELETE FROM MarketPriceAggregates
        //                     WHERE Id IN (
        //                         SELECT Id
        //                         FROM MarketPriceAggregates
        //                         WHERE Interval =
        //                             {(int)AggregateInterval.OneHour}
        //                         AND BucketStartUtc < {hourlyCutoff}
        //                         ORDER BY BucketStartUtc
        //                         LIMIT {batchSize}
        //                     );
        //                     """, token);

        //             int batches = await db.Database
        //                 .ExecuteSqlInterpolatedAsync($"""
        //                     DELETE FROM MarketCollectionBatches
        //                     WHERE Id IN (
        //                         SELECT b.Id
        //                         FROM MarketCollectionBatches b
        //                         WHERE b.StartedAtUtc < {batchCutoff}
        //                         AND b.Status !=
        //                             {(int)CollectionStatus.Running}
        //                         AND NOT EXISTS (
        //                             SELECT 1
        //                             FROM MarketPriceSnapshots s
        //                             WHERE s.CollectionBatchId = b.Id
        //                         )
        //                         ORDER BY b.StartedAtUtc
        //                         LIMIT {batchSize}
        //                     );
        //                     """, token);

        //             return snapshots + fiveMinute +
        //                    hourly + batches;
        //         },
        //         ct);

        //     passes++;
        //     totalDeleted += deleted;

        //     if (deleted == 0)
        //         break;

        //     // Release the writer gate between passes.
        //     await Task.Delay(100, ct);
        // }

        // timer.Stop();

        // _logger.LogInformation(
        //     "Retention completed | " +
        //     "Deleted={Deleted} | " +
        //     "Passes={Passes} | " +
        //     "Duration={DurationMs}ms",
        //     totalDeleted,
        //     passes,
        //     timer.ElapsedMilliseconds);
    }
}


// public sealed class MarketRetentionService
// {
//     private readonly IDbContextFactory<MarketDbContext> _factory;
//     private readonly MarketStorageOptions _options;
//     private readonly ILogger<MarketRetentionService> _logger;

//     public MarketRetentionService(
//         IDbContextFactory<MarketDbContext> factory,
//         IOptions<MarketStorageOptions> options,
//         ILogger<MarketRetentionService> logger)
//     {
//         _factory = factory;
//         _options = options.Value;
//         _logger = logger;
//     }

//     public async Task RunAsync(
//         CancellationToken ct = default)
//     {
//         var now = DateTime.UtcNow;
//         int size = Math.Clamp(
//             _options.DeleteBatchSize, 100, 10000);

//         var snapshotCutoff = now.AddDays(
//             -_options.SnapshotRetentionDays);

//         var fiveMinuteCutoff = now.AddDays(
//             -_options.FiveMinuteRetentionDays);

//         var hourlyCutoff = now.AddDays(
//             -_options.HourlyRetentionDays);

//         var batchCutoff = now.AddDays(
//             -_options.BatchRetentionDays);

//         await using var db = await _factory
//             .CreateDbContextAsync(ct);

//         // Delete a bounded batch per invocation.
//         // Repeated maintenance runs make further progress.
//         int snapshotsDeleted = await db.Database
//             .ExecuteSqlInterpolatedAsync($"""
//                 DELETE FROM MarketPriceSnapshots
//                 WHERE Id IN (
//                     SELECT Id
//                     FROM MarketPriceSnapshots
//                     WHERE CollectedAtUtc < {snapshotCutoff}
//                     ORDER BY CollectedAtUtc
//                     LIMIT {size}
//                 );
//                 """, ct);

//         int fiveMinuteDeleted = await db.Database
//             .ExecuteSqlInterpolatedAsync($"""
//                 DELETE FROM MarketPriceAggregates
//                 WHERE Id IN (
//                     SELECT Id
//                     FROM MarketPriceAggregates
//                     WHERE Interval = {(int)AggregateInterval.FiveMinutes}
//                       AND BucketStartUtc < {fiveMinuteCutoff}
//                     ORDER BY BucketStartUtc
//                     LIMIT {size}
//                 );
//                 """, ct);

//         int hourlyDeleted = await db.Database
//             .ExecuteSqlInterpolatedAsync($"""
//                 DELETE FROM MarketPriceAggregates
//                 WHERE Id IN (
//                     SELECT Id
//                     FROM MarketPriceAggregates
//                     WHERE Interval = {(int)AggregateInterval.OneHour}
//                       AND BucketStartUtc < {hourlyCutoff}
//                     ORDER BY BucketStartUtc
//                     LIMIT {size}
//                 );
//                 """, ct);

//         // Only remove batches once all associated
//         // snapshots have been removed.
//         int batchesDeleted = await db.Database
//             .ExecuteSqlInterpolatedAsync($"""
//                 DELETE FROM MarketCollectionBatches
//                 WHERE Id IN (
//                     SELECT b.Id
//                     FROM MarketCollectionBatches b
//                     WHERE b.StartedAtUtc < {batchCutoff}
//                       AND b.Status != {(int)CollectionStatus.Running}
//                       AND NOT EXISTS (
//                           SELECT 1
//                           FROM MarketPriceSnapshots s
//                           WHERE s.CollectionBatchId = b.Id
//                       )
//                     ORDER BY b.StartedAtUtc
//                     LIMIT {size}
//                 );
//                 """, ct);

//         _logger.LogInformation(
//             "Retention: {Snapshots} snapshots, " +
//             "{FiveMinute} 5m aggregates, " +
//             "{Hourly} hourly aggregates, " +
//             "{Batches} batches deleted.",
//             snapshotsDeleted,
//             fiveMinuteDeleted,
//             hourlyDeleted,
//             batchesDeleted);
//     }
// }
