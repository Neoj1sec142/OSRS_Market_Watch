
using System.Security.Cryptography;
using MarketAPI.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MarketAPI.Services;

public sealed record ArchiveWorkingBackupResult(
    string FilePath,
    long FileSizeBytes,
    string Sha256,
    long ItemCount,
    long SnapshotCount,
    long AggregateCount,
    long BatchCount,
    string IntegrityResult,
    bool ForeignKeysValid);

public sealed class MarketArchiveBackupService
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly DatabaseWriteCoordinator _coordinator;
    private readonly ILogger<MarketArchiveBackupService> _logger;

    public MarketArchiveBackupService(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        DatabaseWriteCoordinator coordinator,
        ILogger<MarketArchiveBackupService> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _coordinator = coordinator;
        _logger = logger;
    }

    public Task<ArchiveWorkingBackupResult> CreateWorkingBackupAsync(
        CancellationToken ct = default)
    {
        if (!_environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Working backup prototype is development-only.");
        }

        return _coordinator.ExecuteAsync(
            token => Task.FromResult(CreateBackup(token)),
            ct);
    }

    private ArchiveWorkingBackupResult CreateBackup(
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var configuredConnection =
            _configuration.GetConnectionString("MarketDb")
            ?? throw new InvalidOperationException(
                "MarketDb connection string is missing.");

        var sourceBuilder =
            new SqliteConnectionStringBuilder(configuredConnection)
            {
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };

        var sourcePath = sourceBuilder.DataSource;

        if (string.IsNullOrWhiteSpace(sourcePath) ||
            sourcePath == ":memory:")
        {
            throw new InvalidOperationException(
                "A file-backed SQLite source is required.");
        }

        // The app's normal relative SQLite path is resolved
        // from the process working directory.
        sourcePath = Path.GetFullPath(sourcePath);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                "Source SQLite database does not exist.",
                sourcePath);
        }

        var outputDirectory = Path.Combine(
            _environment.ContentRootPath,
            "artifacts",
            "archive-work");

        Directory.CreateDirectory(outputDirectory);

        var fileStem =
            $"working_{DateTime.UtcNow:yyyyMMdd_HHmmss}_" +
            Guid.NewGuid().ToString("N");

        var temporaryPath = Path.Combine(
            outputDirectory,
            fileStem + ".partial.db");

        var finalPath = Path.Combine(
            outputDirectory,
            fileStem + ".db");

        if (Path.GetFullPath(temporaryPath) == sourcePath ||
            Path.GetFullPath(finalPath) == sourcePath)
        {
            throw new InvalidOperationException(
                "Backup destination cannot be the source database.");
        }

        try
        {
            var destinationBuilder =
                new SqliteConnectionStringBuilder
                {
                    DataSource = temporaryPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                    ForeignKeys = true
                };

            // SQLite online backup includes committed WAL data.
            // Do not copy the .db file directly with File.Copy.
            using (var source = new SqliteConnection(
                       sourceBuilder.ToString()))
            using (var destination = new SqliteConnection(
                       destinationBuilder.ToString()))
            {
                source.Open();
                destination.Open();

                ct.ThrowIfCancellationRequested();

                source.BackupDatabase(destination);
            }

            ct.ThrowIfCancellationRequested();

            long Count(SqliteConnection connection, string table)
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT COUNT(*) FROM \"{table}\";";

                return Convert.ToInt64(command.ExecuteScalar());
            }

            string integrity;
            bool foreignKeysValid;
            long itemCount;
            long snapshotCount;
            long aggregateCount;
            long batchCount;

            var verifyBuilder =
                new SqliteConnectionStringBuilder
                {
                    DataSource = temporaryPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                    ForeignKeys = true
                };

            using (var verify = new SqliteConnection(
                       verifyBuilder.ToString()))
            {
                verify.Open();

                using (var command = verify.CreateCommand())
                {
                    command.CommandText = "PRAGMA integrity_check;";
                    integrity = Convert.ToString(
                        command.ExecuteScalar()) ?? "unknown";
                }

                if (integrity != "ok")
                    throw new InvalidOperationException(
                        $"SQLite integrity check failed: {integrity}");

                using (var command = verify.CreateCommand())
                {
                    command.CommandText = "PRAGMA foreign_key_check;";

                    using var reader = command.ExecuteReader();
                    foreignKeysValid = !reader.Read();
                }

                if (!foreignKeysValid)
                    throw new InvalidOperationException(
                        "Working backup contains foreign key violations.");

                itemCount = Count(verify, "MarketItems");
                snapshotCount = Count(
                    verify, "MarketPriceSnapshots");
                aggregateCount = Count(
                    verify, "MarketPriceAggregates");
                batchCount = Count(
                    verify, "MarketCollectionBatches");
            }

            ct.ThrowIfCancellationRequested();

            // Move only after the working copy passes validation.
            File.Move(temporaryPath, finalPath);

            using var file = File.OpenRead(finalPath);
            var sha256 = Convert.ToHexString(
                SHA256.HashData(file)).ToLowerInvariant();

            var size = new FileInfo(finalPath).Length;

            _logger.LogInformation(
                "Working archive source backup created at {Path}. " +
                "Bytes={Bytes}, SHA256={Sha256}",
                finalPath,
                size,
                sha256);

            return new ArchiveWorkingBackupResult(
                finalPath,
                size,
                sha256,
                itemCount,
                snapshotCount,
                aggregateCount,
                batchCount,
                integrity,
                foreignKeysValid);
        }
        catch
        {
            // A failed working copy is never treated as complete.
            foreach (var path in new[]
            {
                temporaryPath,
                finalPath
            })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }

            throw;
        }
    }
}
