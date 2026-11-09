
using MarketAPI.Models;
using Microsoft.Data.Sqlite;
using System.Data;

namespace MarketAPI.Services;

public sealed class SqliteMarketBulkWriter
{
    private readonly string _connectionString;

    public SqliteMarketBulkWriter(IConfiguration config)
    {
        _connectionString =
            config.GetConnectionString("MarketDb")
            ?? throw new InvalidOperationException(
                "Missing MarketDb connection string.");
    }

    public async Task<int> InsertSnapshotsAsync(
        IReadOnlyCollection<MarketPriceSnapshot> snapshots,
        CancellationToken ct = default)
    {
        if (snapshots.Count == 0)
            return 0;

        await using var connection = await OpenAsync(ct);
        await using var transaction =
            (SqliteTransaction)await connection
                .BeginTransactionAsync(ct);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO MarketPriceSnapshots
            (
                MarketItemId,
                CollectionBatchId,
                InstantBuyPrice,
                InstantSellPrice,
                InstantBuyTimeUtc,
                InstantSellTimeUtc,
                CollectedAtUtc
            )
            SELECT
                $item,
                $batch,
                $buy,
                $sell,
                $buyTime,
                $sellTime,
                $collected
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM
                (
                    SELECT
                        InstantBuyPrice,
                        InstantSellPrice,
                        InstantBuyTimeUtc,
                        InstantSellTimeUtc
                    FROM MarketPriceSnapshots
                    WHERE MarketItemId = $item
                    ORDER BY CollectedAtUtc DESC, Id DESC
                    LIMIT 1
                ) AS previous
                WHERE
                    previous.InstantBuyPrice IS $buy
                    AND previous.InstantSellPrice IS $sell
                    AND previous.InstantBuyTimeUtc IS $buyTime
                    AND previous.InstantSellTimeUtc IS $sellTime
            )
            ON CONFLICT(CollectionBatchId, MarketItemId)
            DO NOTHING;
            """;

        var item = command.Parameters.Add("$item", SqliteType.Integer);
        var batch = command.Parameters.Add("$batch", SqliteType.Integer);
        var buy = command.Parameters.Add("$buy", SqliteType.Integer);
        var sell = command.Parameters.Add("$sell", SqliteType.Integer);
        var buyTime = command.Parameters.Add("$buyTime", SqliteType.Text);
        var sellTime = command.Parameters.Add("$sellTime", SqliteType.Text);
        var collected = command.Parameters.Add(
            "$collected", SqliteType.Text);

        await command.PrepareAsync(ct);

        int inserted = 0;

        foreach (var row in snapshots)
        {
            ct.ThrowIfCancellationRequested();

            item.Value = row.MarketItemId;
            batch.Value = row.CollectionBatchId;

            buy.Value = DbValue(row.InstantBuyPrice);
            sell.Value = DbValue(row.InstantSellPrice);

            buyTime.Value = DateValue(row.InstantBuyTimeUtc);
            sellTime.Value = DateValue(row.InstantSellTimeUtc);
            collected.Value = DateValue(row.CollectedAtUtc);

            inserted += await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return inserted;
    }

    public async Task<int> UpsertAggregatesAsync(
        IReadOnlyCollection<MarketPriceAggregate> aggregates,
        CancellationToken ct = default)
    {
        if (aggregates.Count == 0)
            return 0;

        await using var connection = await OpenAsync(ct);
        await using var transaction =
            (SqliteTransaction)await connection
                .BeginTransactionAsync(ct);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO MarketPriceAggregates
            (
                MarketItemId,
                Interval,
                BucketStartUtc,
                AverageInstantBuyPrice,
                AverageInstantSellPrice,
                InstantBuyVolume,
                InstantSellVolume,
                CollectedAtUtc
            )
            VALUES
            (
                $item,
                $interval,
                $bucket,
                $buy,
                $sell,
                $buyVolume,
                $sellVolume,
                $collected
            )
            ON CONFLICT(
                MarketItemId,
                Interval,
                BucketStartUtc
            )
            DO UPDATE SET
                AverageInstantBuyPrice =
                    excluded.AverageInstantBuyPrice,
                AverageInstantSellPrice =
                    excluded.AverageInstantSellPrice,
                InstantBuyVolume =
                    excluded.InstantBuyVolume,
                InstantSellVolume =
                    excluded.InstantSellVolume,
                CollectedAtUtc =
                    excluded.CollectedAtUtc
            WHERE excluded.CollectedAtUtc >
                  MarketPriceAggregates.CollectedAtUtc;
            """;

        var item = command.Parameters.Add("$item", SqliteType.Integer);
        var interval = command.Parameters.Add(
            "$interval", SqliteType.Integer);
        var bucket = command.Parameters.Add(
            "$bucket", SqliteType.Text);

        var buy = command.Parameters.Add("$buy", SqliteType.Text);
        var sell = command.Parameters.Add("$sell", SqliteType.Text);

        var buyVolume = command.Parameters.Add(
            "$buyVolume", SqliteType.Integer);
        var sellVolume = command.Parameters.Add(
            "$sellVolume", SqliteType.Integer);

        var collected = command.Parameters.Add(
            "$collected", SqliteType.Text);

        await command.PrepareAsync(ct);

        int affected = 0;

        foreach (var row in aggregates)
        {
            ct.ThrowIfCancellationRequested();

            item.Value = row.MarketItemId;
            interval.Value = (int)row.Interval;

            bucket.Value = DateValue(row.BucketStartUtc);

            buy.Value = DbValue(row.AverageInstantBuyPrice);
            sell.Value = DbValue(row.AverageInstantSellPrice);

            buyVolume.Value = row.InstantBuyVolume;
            sellVolume.Value = row.InstantSellVolume;

            collected.Value = DateValue(row.CollectedAtUtc);

            affected += await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return affected;
    }

    private async Task<SqliteConnection> OpenAsync(
        CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);

        try
        {
            await connection.OpenAsync(ct);

            await using var command = connection.CreateCommand();

            command.CommandText = """
                PRAGMA synchronous = NORMAL;
                PRAGMA cache_size = -32768;
                PRAGMA temp_store = MEMORY;
                PRAGMA wal_autocheckpoint = 1000;
                """;

            await command.ExecuteNonQueryAsync(ct);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static object DbValue(object? value)
        => value ?? DBNull.Value;

    private static object DateValue(DateTime? value)
        => value.HasValue
            ? value.Value.ToUniversalTime()
                .ToString("yyyy-MM-dd HH:mm:ss.fffffff",
                    System.Globalization.CultureInfo.InvariantCulture)
            : DBNull.Value;
}
