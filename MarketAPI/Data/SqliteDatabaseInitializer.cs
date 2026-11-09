
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MarketAPI.Data;

public static class SqliteDatabaseInitializer
{
    public static async Task InitializeAsync(
        IServiceProvider services,
        string connectionString,
        CancellationToken ct = default)
    {
        var builder = new SqliteConnectionStringBuilder(
            connectionString);

        var databasePath = Path.GetFullPath(builder.DataSource);
        var directory = Path.GetDirectoryName(databasePath)!;

        Directory.CreateDirectory(directory);

        // Use an absolute path everywhere so EF and
        // direct SQLite connections open the same file.
        builder.DataSource = databasePath;

        var normalizedConnectionString = builder.ToString();

        await using (var connection =
            new SqliteConnection(normalizedConnectionString))
        {
            await connection.OpenAsync(ct);

            // Configure incremental vacuum only for a
            // database that does not yet have user tables.
            await using var countCommand = connection.CreateCommand();

            countCommand.CommandText = """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table'
                  AND name NOT LIKE 'sqlite_%';
                """;

            var tableCount = Convert.ToInt64(
                await countCommand.ExecuteScalarAsync(ct));

            if (tableCount == 0)
            {
                await ExecuteAsync(
                    connection,
                    "PRAGMA auto_vacuum = INCREMENTAL;",
                    ct);
            }

            // Persistent database-level setting.
            await using var walCommand = connection.CreateCommand();
            walCommand.CommandText = "PRAGMA journal_mode = WAL;";

            var journalMode = Convert.ToString(
                await walCommand.ExecuteScalarAsync(ct));

            if (!string.Equals(
                journalMode, "wal",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Failed to enable WAL. Mode: {journalMode}");
            }
        }

        using var scope = services.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<MarketDbContext>();

        await db.Database.MigrateAsync(ct);

        await db.Database.ExecuteSqlRawAsync(
            "PRAGMA optimize;", ct);
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
