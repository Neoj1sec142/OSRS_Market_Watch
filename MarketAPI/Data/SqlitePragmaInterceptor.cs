
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MarketAPI.Data;

public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas = """
        PRAGMA synchronous = NORMAL;
        PRAGMA cache_size = -32768;
        PRAGMA temp_store = MEMORY;
        PRAGMA wal_autocheckpoint = 1000;
        """;

    public override void ConnectionOpened(
        DbConnection connection,
        ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(
            connection, eventData, cancellationToken);
    }

    private static void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    private static async Task ApplyAsync(
        DbConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(ct);
    }
}
