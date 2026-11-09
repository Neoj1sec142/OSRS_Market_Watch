
using Microsoft.Data.Sqlite;

namespace MarketAPI.Services;

public sealed class SqliteMaintenanceService
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteMaintenanceService> _logger;

    public SqliteMaintenanceService(
        IConfiguration configuration,
        ILogger<SqliteMaintenanceService> logger)
    {
        _connectionString =
            configuration.GetConnectionString("MarketDb")
            ?? throw new InvalidOperationException(
                "MarketDb connection string missing.");

        _logger = logger;
    }

    public async Task OptimizeAsync(
        CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(
            _connectionString);

        await connection.OpenAsync(ct);

        await ExecuteAsync(
            connection,
            "PRAGMA optimize;",
            ct);

        _logger.LogInformation(
            "SQLite query planner optimization completed.");
    }

    public async Task ReclaimFreePagesAsync(
        int pages = 1000,
        CancellationToken ct = default)
    {
        pages = Math.Clamp(pages, 1, 10000);

        await using var connection = new SqliteConnection(
            _connectionString);

        await connection.OpenAsync(ct);

        await using var mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA auto_vacuum;";

        var vacuumMode = Convert.ToInt32(
            await mode.ExecuteScalarAsync(ct));

        if (vacuumMode != 2)
        {
            _logger.LogWarning(
                "Incremental vacuum unavailable. " +
                "Current auto_vacuum mode: {Mode}",
                vacuumMode);

            return;
        }

        await ExecuteAsync(
            connection,
            $"PRAGMA incremental_vacuum({pages});",
            ct);

        _logger.LogInformation(
            "Incremental vacuum requested for up to {Pages} pages.",
            pages);
    }

    public async Task CheckpointAsync(
        CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(
            _connectionString);

        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";

        await using var reader = await command.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            long busy = reader.GetInt64(0);
            long logFrames = reader.GetInt64(1);
            long checkpointedFrames = reader.GetInt64(2);

            _logger.LogInformation(
                "WAL checkpoint: busy={Busy}, " +
                "log={LogFrames}, checkpointed={CheckpointedFrames}",
                busy, logFrames, checkpointedFrames);
        }
    }

    public async Task QuickCheckAsync(
        CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(
            _connectionString);

        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";

        var result = Convert.ToString(
            await command.ExecuteScalarAsync(ct));

        if (!string.Equals(
            result, "ok",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SQLite integrity check failed: {result}");
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
