using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MarketAPI.Data;
using MarketAPI.Enums;
using MarketAPI.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MarketAPI.Services;

public sealed record ArchivePublicationResult(
    Guid ArchiveId,
    Guid SourceId,
    string ArchivePath,
    string ManifestPath,
    long ArchiveSizeBytes,
    string ArchiveSha256,
    string ManifestSha256,
    string Status,
    bool RecoveredPublishedFile,
    string? VerificationBackupPath);

/// <summary>
/// Phase 4 Pass 3: development-only local publication.
/// This service NEVER transfers, verifies remote storage, commits ranges,
/// or permits retention. Source aggregates can still change after publication.
/// </summary>
public sealed class MarketArchivePublicationService
{
    private const string CandidateFormat = "market-archive-candidate-v1";
    private const string ManifestFormat = "nugget-capital-local-export-v1";

    private readonly IDbContextFactory<MarketDbContext> _factory;
    private readonly MarketArchiveBackupService _backups;
    private readonly DatabaseWriteCoordinator _writes;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<MarketArchivePublicationService> _logger;

    public MarketArchivePublicationService(
        IDbContextFactory<MarketDbContext> factory,
        MarketArchiveBackupService backups,
        DatabaseWriteCoordinator writes,
        IWebHostEnvironment environment,
        ILogger<MarketArchivePublicationService> logger)
    {
        _factory = factory;
        _backups = backups;
        _writes = writes;
        _environment = environment;
        _logger = logger;
    }

    public async Task<ArchivePublicationResult> PublishAsync(
        Guid archiveId,
        bool simulateInterruptionAfterMove = false,
        CancellationToken ct = default)
    {
        if (!_environment.IsDevelopment())
            throw new InvalidOperationException("Local archive publication is development-only.");
        if (archiveId == Guid.Empty)
            throw new ArgumentException("Archive ID is required.", nameof(archiveId));

        var candidatesDir = Path.Combine(
            _environment.ContentRootPath, "artifacts", "archive-candidates");
        var exportsDir = Path.Combine(
            _environment.ContentRootPath, "artifacts", "archive-exports");
        Directory.CreateDirectory(exportsDir);

        // FileShare.None serializes publication attempts for the same archive
        // across local processes. OS releases the handle if the process dies.
        // Keep the empty lock file: deleting it could create a lock-file race.
        var lockPath = Path.Combine(exportsDir, $".{archiveId:N}.publish.lock");
        await using var publicationLock = new FileStream(
            lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        ct.ThrowIfCancellationRequested();
        MarketArchiveExport archive;
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            archive = await db.MarketArchiveExports
                .AsNoTracking()
                .Include(x => x.Ranges)
                .SingleOrDefaultAsync(x => x.Id == archiveId, ct)
                ?? throw new InvalidOperationException("Archive reservation not found.");
            var source = await db.MarketArchiveSources
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == 1, ct);
            if (source is null || source.SourceId != archive.SourceId)
                throw new InvalidOperationException("Archive source identity mismatch.");
        }

        ValidatePlan(archive);
        if (archive.Status != ArchiveStatus.Prepared &&
            archive.Status != ArchiveStatus.Exported)
            throw new InvalidOperationException(
                $"Cannot publish archive in {archive.Status} state.");

        var candidatePath = Path.Combine(
            candidatesDir, $"market_{archiveId:N}.candidate.db");
        var archivePath = Path.Combine(exportsDir, archive.ArchiveFileName);
        var manifestPath = archivePath + ".manifest.json";
        string? verificationBackupPath = null;
        bool recovered = false;

        if (archive.Status == ArchiveStatus.Exported)
        {
            if (simulateInterruptionAfterMove)
                throw new InvalidOperationException("Cannot simulate interruption for an Exported archive.");
            var (size, hash) = VerifyArtifact(archivePath, archive, null);
            var expectedManifest = BuildManifest(archive, size, hash);
            VerifyManifest(manifestPath, expectedManifest);
            var manifestHash = HashBytes(expectedManifest);
            if (archive.ArchiveSizeBytes != size ||
                !StringComparer.OrdinalIgnoreCase.Equals(archive.ArchiveSha256, hash) ||
                !StringComparer.OrdinalIgnoreCase.Equals(archive.ManifestSha256, manifestHash) ||
                archive.ExportedAtUtc is null)
                throw new InvalidOperationException("Exported ledger receipt does not match the published files.");
            return Result(archive, archivePath, manifestPath, size, hash,
                manifestHash, false, null);
        }

        // Prepared state: no export receipt fields may already be set.
        if (archive.ExportedAtUtc is not null || archive.TransferredAtUtc is not null ||
            archive.VerifiedAtUtc is not null || archive.CommittedAtUtc is not null ||
            archive.ArchiveSha256 is not null || archive.ManifestSha256 is not null ||
            archive.ArchiveSizeBytes is not null || archive.StorageLocation is not null)
            throw new InvalidOperationException("Prepared reservation has unexpected export metadata.");

        bool finalExists = File.Exists(archivePath);
        bool candidateExists = File.Exists(candidatePath);
        if (finalExists && candidateExists)
            throw new InvalidOperationException(
                "Both candidate and final files exist. Manual reconciliation required.");
        if (!finalExists && !candidateExists)
            throw new InvalidOperationException(
                "Neither a candidate nor a recoverable published file exists.");

        // Re-read the live database as a consistent, independent snapshot.
        // Fail closed if rows were revised/backfilled since candidate extraction.
        // This is deliberately expensive but safe for our Windows test fixture.
        var backup = await _backups.CreateWorkingBackupAsync(ct);
        verificationBackupPath = backup.FilePath;

        var inputPath = finalExists ? archivePath : candidatePath;
        VerifyArtifact(inputPath, archive, backup.FilePath);

        if (finalExists)
        {
            recovered = true; // May have crashed after rename but before ledger UPDATE.
        }
        else
        {
            // Same project artifact tree / filesystem. Never overwrite a final file.
            File.Move(candidatePath, archivePath);
            if (simulateInterruptionAfterMove)
                throw new InvalidOperationException(
                    "SIMULATED interruption: final archive moved; ledger still Prepared. Retry publication to recover.");
        }

        ct.ThrowIfCancellationRequested();
        var (archiveSize, archiveHash) = VerifyArtifact(
            archivePath, archive, backup.FilePath);
        var manifestBytes = BuildManifest(archive, archiveSize, archiveHash);
        var manifestSha = HashBytes(manifestBytes);

        // Manifest is a separate sidecar: it contains the DB hash and therefore
        // cannot be embedded into the hashed DB without self-reference.
        PublishManifest(manifestPath, manifestBytes);

        // A conditional single-row update is the only ledger state mutation.
        // The filesystem rename and SQLite commit cannot be one transaction;
        // retries reconcile the final DB and manifest when status remains Prepared.
        var exportedAt = DateTime.UtcNow;
        int affected = await _writes.ExecuteAsync(async token =>
        {
            await using var db = await _factory.CreateDbContextAsync(token);
            return await db.MarketArchiveExports
                .Where(x => x.Id == archive.Id &&
                            x.SourceId == archive.SourceId &&
                            x.ArchiveFileName == archive.ArchiveFileName &&
                            x.SchemaVersion == archive.SchemaVersion &&
                            x.Status == ArchiveStatus.Prepared &&
                            x.ExportedAtUtc == null && x.TransferredAtUtc == null &&
                            x.VerifiedAtUtc == null && x.CommittedAtUtc == null &&
                            x.ArchiveSha256 == null && x.ArchiveSizeBytes == null &&
                            x.ManifestSha256 == null && x.StorageLocation == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, ArchiveStatus.Exported)
                    .SetProperty(x => x.ExportedAtUtc, exportedAt)
                    .SetProperty(x => x.ArchiveSha256, archiveHash)
                    .SetProperty(x => x.ArchiveSizeBytes, archiveSize)
                    .SetProperty(x => x.ManifestSha256, manifestSha), token);
        }, ct);
        if (affected != 1)
            throw new InvalidOperationException(
                "Archive state changed during publication. Files preserved for manual inspection.");

        _logger.LogInformation(
            "Locally Exported archive {ArchiveId}, sha256={Sha256}, recovered={Recovered}. " +
            "NOT transferred, verified at destination, or committed.",
            archive.Id, archiveHash, recovered);
        return Result(archive, archivePath, manifestPath,
            archiveSize, archiveHash, manifestSha, recovered, verificationBackupPath);
    }

    private static ArchivePublicationResult Result(
        MarketArchiveExport archive, string archivePath, string manifestPath,
        long size, string sha, string manifestSha, bool recovered, string? backupPath) =>
        new(archive.Id, archive.SourceId, archivePath, manifestPath,
            size, sha, manifestSha, "Exported_NotTransferredOrCommitted", recovered, backupPath);

    private static void ValidatePlan(MarketArchiveExport archive)
    {
        if (archive.SchemaVersion != 1 ||
            archive.Ranges.Count is < 1 or > 3 ||
            archive.Ranges.Select(x => x.Stream).Distinct().Count() != archive.Ranges.Count)
            throw new InvalidOperationException("Unsupported archive plan or duplicate/missing ranges.");
        if (string.IsNullOrWhiteSpace(archive.ArchiveFileName) ||
            Path.GetFileName(archive.ArchiveFileName) != archive.ArchiveFileName ||
            !archive.ArchiveFileName.EndsWith($"_{archive.Id:N}.db", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid archive output filename.");
        foreach (var r in archive.Ranges)
        {
            if (r.RecordCount <= 0)
                throw new InvalidOperationException("Range has no observations.");
            if (r.Stream == ArchiveStream.Snapshots)
            {
                if (!r.StartIdExclusive.HasValue || !r.EndIdInclusive.HasValue ||
                    r.StartIdExclusive.Value < 0 ||
                    r.EndIdInclusive.Value <= r.StartIdExclusive.Value ||
                    r.StartUtcInclusive.HasValue || r.EndUtcExclusive.HasValue)
                    throw new InvalidOperationException("Malformed snapshot range.");
            }
            else
            {
                var duration = r.Stream switch
                {
                    ArchiveStream.FiveMinuteAggregates => TimeSpan.FromMinutes(5),
                    ArchiveStream.HourlyAggregates => TimeSpan.FromHours(1),
                    _ => throw new InvalidOperationException("Unknown archive stream.")
                };
                if (r.StartIdExclusive.HasValue || r.EndIdInclusive.HasValue ||
                    !r.StartUtcInclusive.HasValue || !r.EndUtcExclusive.HasValue ||
                    r.StartUtcInclusive >= r.EndUtcExclusive ||
                    r.StartUtcInclusive.Value.Ticks % duration.Ticks != 0 ||
                    r.EndUtcExclusive.Value.Ticks % duration.Ticks != 0)
                    throw new InvalidOperationException("Malformed aggregate range.");
            }
        }
    }

    private static (long Size, string Sha256) VerifyArtifact(
        string path, MarketArchiveExport archive, string? frozenSourcePath)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"Missing archive file: {path}");
        if (File.Exists(path + "-wal") || File.Exists(path + "-shm"))
            throw new InvalidOperationException("Archive has WAL/SHM sidecar files; cannot hash an unstable SQLite file.");

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly,
            Pooling = false, ForeignKeys = true
        }.ToString()))
        {
            connection.Open();
            if (Scalar(connection, "SELECT COUNT(*) FROM ArchiveManifest;") != 1)
                throw new InvalidOperationException("Candidate has an invalid embedded manifest count.");

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT ArchiveId, SourceId, SchemaVersion, PreparedAtUtc, Format FROM ArchiveManifest;";
                using var reader = command.ExecuteReader();
                if (!reader.Read() || Guid.Parse(reader.GetString(0)) != archive.Id ||
                    Guid.Parse(reader.GetString(1)) != archive.SourceId ||
                    reader.GetInt32(2) != archive.SchemaVersion ||
                    reader.GetDateTime(3).Ticks != archive.CreatedAtUtc.Ticks ||
                    reader.GetString(4) != CandidateFormat)
                    throw new InvalidOperationException("Candidate embedded identity/version does not match the ledger.");
            }

            if (Scalar(connection, "SELECT COUNT(*) FROM ArchiveRanges;") != archive.Ranges.Count)
                throw new InvalidOperationException("Candidate range count does not match ledger.");
            foreach (var r in archive.Ranges)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    SELECT StartIdExclusive, EndIdInclusive, StartUtcInclusive,
                           EndUtcExclusive, RecordCount
                    FROM ArchiveRanges WHERE Stream = $stream;
                    """;
                cmd.Parameters.AddWithValue("$stream", (int)r.Stream);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read() ||
                    ReadLong(reader, 0) != r.StartIdExclusive ||
                    ReadLong(reader, 1) != r.EndIdInclusive ||
                    ReadDate(reader, 2) != r.StartUtcInclusive ||
                    ReadDate(reader, 3) != r.EndUtcExclusive ||
                    reader.GetInt64(4) != r.RecordCount || reader.Read())
                    throw new InvalidOperationException($"Candidate range {r.Stream} differs from ledger.");
            }

            var snaps = archive.Ranges.SingleOrDefault(x => x.Stream == ArchiveStream.Snapshots);
            var five = archive.Ranges.SingleOrDefault(x => x.Stream == ArchiveStream.FiveMinuteAggregates);
            var hour = archive.Ranges.SingleOrDefault(x => x.Stream == ArchiveStream.HourlyAggregates);
            if (Scalar(connection, "SELECT COUNT(*) FROM MarketPriceSnapshots;") != (snaps?.RecordCount ?? 0) ||
                Scalar(connection, "SELECT COUNT(*) FROM MarketPriceAggregates WHERE Interval = 5;") != (five?.RecordCount ?? 0) ||
                Scalar(connection, "SELECT COUNT(*) FROM MarketPriceAggregates WHERE Interval = 60;") != (hour?.RecordCount ?? 0) ||
                Scalar(connection, "SELECT COUNT(*) FROM MarketPriceAggregates WHERE Interval NOT IN (5, 60);") != 0)
                throw new InvalidOperationException("Archive observation counts differ from reserved ranges.");
            if (snaps != null)
            {
                if (Scalar(connection,
                    $"SELECT COUNT(*) FROM MarketPriceSnapshots WHERE NOT (Id > {snaps.StartIdExclusive} AND Id <= {snaps.EndIdInclusive});") != 0)
                    throw new InvalidOperationException("Snapshot outside reserved ID range.");
                var snapshotCutoff = DbTime(archive.CreatedAtUtc.AddHours(-48));
                if (Scalar(connection,
                    $"SELECT COUNT(*) FROM MarketPriceSnapshots WHERE CollectedAtUtc >= '{snapshotCutoff}';") != 0)
                    throw new InvalidOperationException("Snapshot was not eligible at preparation time.");
            }
            foreach (var tuple in new[] { (Range: five, Interval: 5), (Range: hour, Interval: 60) })
            {
                if (tuple.Range is null) continue;
                var r = tuple.Range;
                var delayHours = tuple.Interval == 5 ? 6 : 24;
                var duration = TimeSpan.FromMinutes(tuple.Interval);
                var cutoff = archive.CreatedAtUtc.AddHours(-delayHours);
                var alignedTicks = cutoff.Ticks - cutoff.Ticks % duration.Ticks;
                if (r.EndUtcExclusive!.Value.Ticks > alignedTicks)
                    throw new InvalidOperationException("Aggregate bucket was not finalized at preparation time.");
                if (Scalar(connection,
                    $"SELECT COUNT(*) FROM MarketPriceAggregates WHERE Interval = {tuple.Interval} " +
                    $"AND NOT (BucketStartUtc >= '{DbTime(r.StartUtcInclusive!.Value)}' " +
                    $"AND BucketStartUtc < '{DbTime(r.EndUtcExclusive!.Value)}');") != 0)
                    throw new InvalidOperationException("Aggregate outside reserved UTC buckets.");
            }
            if (Scalar(connection, "SELECT COUNT(*) FROM MarketItems WHERE Id NOT IN (SELECT MarketItemId FROM MarketPriceSnapshots UNION SELECT MarketItemId FROM MarketPriceAggregates);") != 0 ||
                Scalar(connection, "SELECT COUNT(*) FROM MarketCollectionBatches WHERE Id NOT IN (SELECT CollectionBatchId FROM MarketPriceSnapshots);") != 0)
                throw new InvalidOperationException("Archive contains unreferenced metadata.");
            if (Scalar(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;") != 0)
                throw new InvalidOperationException("Archive foreign-key verification failed.");
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA integrity_check;";
                if (!string.Equals(Convert.ToString(cmd.ExecuteScalar()), "ok", StringComparison.Ordinal))
                    throw new InvalidOperationException("Archive integrity_check failed.");
            }

            if (frozenSourcePath is not null)
                CompareWithFrozenSource(connection, archive, frozenSourcePath);
        }

        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return (new FileInfo(path).Length, hash);
    }

    private static void CompareWithFrozenSource(
        SqliteConnection candidate, MarketArchiveExport archive, string backupPath)
    {
        using (var command = candidate.CreateCommand())
        {
            command.CommandText = "ATTACH DATABASE $file AS src;";
            command.Parameters.AddWithValue("$file", backupPath);
            command.ExecuteNonQuery();
        }
        try
        {
            using (var command = candidate.CreateCommand())
            {
                command.CommandText = """
                    SELECT COUNT(*)
                    FROM src.MarketArchiveExports e
                    JOIN src.MarketArchiveSources s ON s.Id = 1
                    WHERE lower(e.Id) = lower($archive)
                      AND lower(e.SourceId) = lower($source)
                      AND lower(s.SourceId) = lower($source)
                      AND e.Status = 0 AND e.SchemaVersion = 1;
                    """;
                command.Parameters.AddWithValue("$archive", archive.Id.ToString("D"));
                command.Parameters.AddWithValue("$source", archive.SourceId.ToString("D"));
                if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                    throw new InvalidOperationException("Prepared plan/source not present in the frozen database.");
            }
            var snaps = archive.Ranges.SingleOrDefault(x => x.Stream == ArchiveStream.Snapshots);
            var five = archive.Ranges.SingleOrDefault(x => x.Stream == ArchiveStream.FiveMinuteAggregates);
            var hour = archive.Ranges.SingleOrDefault(x => x.Stream == ArchiveStream.HourlyAggregates);
            if (Scalar(candidate, "SELECT COUNT(*) FROM src.MarketArchiveStreamRanges WHERE lower(ArchiveExportId) = lower('" + archive.Id.ToString("D") + "');") != archive.Ranges.Count)
                throw new InvalidOperationException("Frozen source has a different number of archive ranges.");
            foreach (var range in archive.Ranges)
            {
                using var cmd = candidate.CreateCommand();
                cmd.CommandText = """
                    SELECT StartIdExclusive, EndIdInclusive, StartUtcInclusive,
                           EndUtcExclusive, RecordCount
                    FROM src.MarketArchiveStreamRanges
                    WHERE lower(ArchiveExportId) = lower($archive)
                      AND Stream = $stream;
                    """;
                cmd.Parameters.AddWithValue("$archive", archive.Id.ToString("D"));
                cmd.Parameters.AddWithValue("$stream", (int)range.Stream);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read() ||
                    ReadLong(reader, 0) != range.StartIdExclusive ||
                    ReadLong(reader, 1) != range.EndIdInclusive ||
                    ReadDate(reader, 2) != range.StartUtcInclusive ||
                    ReadDate(reader, 3) != range.EndUtcExclusive ||
                    reader.GetInt64(4) != range.RecordCount || reader.Read())
                    throw new InvalidOperationException("Frozen source archive plan changed since the ledger read.");
            }
            var snapshotFilter = snaps is null ? "0" :
                $"Id > {snaps.StartIdExclusive} AND Id <= {snaps.EndIdInclusive}";
            string AggregateFilter(MarketArchiveStreamRange? range, int interval) =>
                range is null ? "0" :
                $"(Interval = {interval} AND BucketStartUtc >= '{DbTime(range.StartUtcInclusive!.Value)}' " +
                $"AND BucketStartUtc < '{DbTime(range.EndUtcExclusive!.Value)}')";
            var aggregateFilter = AggregateFilter(five, 5) + " OR " + AggregateFilter(hour, 60);
            CompareExact(candidate, "MarketPriceSnapshots",
                "Id, MarketItemId, CollectionBatchId, InstantBuyPrice, InstantSellPrice, InstantBuyTimeUtc, InstantSellTimeUtc, CollectedAtUtc",
                snapshotFilter);
            CompareExact(candidate, "MarketPriceAggregates",
                "Id, MarketItemId, Interval, BucketStartUtc, AverageInstantBuyPrice, AverageInstantSellPrice, InstantBuyVolume, InstantSellVolume, CollectedAtUtc",
                aggregateFilter);
            CompareExact(candidate, "MarketItems",
                "Id, Name, Examine, Icon, Members, BuyLimit, HighAlchValue, LowAlchValue, IsActive, IsTaxExempt, LastMetadataUpdateUtc",
                "Id IN (SELECT MarketItemId FROM main.MarketPriceSnapshots UNION SELECT MarketItemId FROM main.MarketPriceAggregates)");
            CompareExact(candidate, "MarketCollectionBatches",
                "Id, StartedAtUtc, CompletedAtUtc, Source, Status, ItemsCollected, ErrorMessage",
                "Id IN (SELECT CollectionBatchId FROM main.MarketPriceSnapshots)");
        }
        finally
        {
            using var command = candidate.CreateCommand();
            command.CommandText = "DETACH DATABASE src;";
            command.ExecuteNonQuery();
        }
    }

    private static void CompareExact(SqliteConnection db, string table, string columns, string filter)
    {
        var missing = Scalar(db, $"""
            SELECT COUNT(*) FROM (
                SELECT {columns} FROM src.{table} WHERE {filter}
                EXCEPT SELECT {columns} FROM main.{table}
            );
            """);
        var extra = Scalar(db, $"""
            SELECT COUNT(*) FROM (
                SELECT {columns} FROM main.{table}
                EXCEPT SELECT {columns} FROM src.{table} WHERE {filter}
            );
            """);
        if (missing != 0 || extra != 0)
            throw new InvalidOperationException(
                $"Archive {table} rows differ from the current frozen source. Rebuild only through a reviewed recovery flow.");
    }

    private static byte[] BuildManifest(MarketArchiveExport archive, long size, string sha)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", ManifestFormat);
            writer.WriteNumber("manifestVersion", 1);
            writer.WriteString("archiveId", archive.Id.ToString("D"));
            writer.WriteString("sourceId", archive.SourceId.ToString("D"));
            writer.WriteNumber("schemaVersion", archive.SchemaVersion);
            writer.WriteString("archiveFileName", archive.ArchiveFileName);
            writer.WriteString("preparedAtUtc", UtcString(archive.CreatedAtUtc));
            writer.WriteNumber("archiveSizeBytes", size);
            writer.WriteString("archiveSha256", sha);
            writer.WriteStartArray("ranges");
            foreach (var range in archive.Ranges.OrderBy(x => x.Stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("stream", (int)range.Stream);
                if (range.StartIdExclusive.HasValue)
                    writer.WriteNumber("startIdExclusive", range.StartIdExclusive.Value);
                else writer.WriteNull("startIdExclusive");
                if (range.EndIdInclusive.HasValue)
                    writer.WriteNumber("endIdInclusive", range.EndIdInclusive.Value);
                else writer.WriteNull("endIdInclusive");
                if (range.StartUtcInclusive.HasValue)
                    writer.WriteString("startUtcInclusive", UtcString(range.StartUtcInclusive.Value));
                else writer.WriteNull("startUtcInclusive");
                if (range.EndUtcExclusive.HasValue)
                    writer.WriteString("endUtcExclusive", UtcString(range.EndUtcExclusive.Value));
                else writer.WriteNull("endUtcExclusive");
                writer.WriteNumber("recordCount", range.RecordCount);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }
        return memory.ToArray();
    }

    private static void PublishManifest(string path, byte[] expected)
    {
        if (File.Exists(path))
        {
            VerifyManifest(path, expected);
            return;
        }
        var partial = path + ".partial";
        if (File.Exists(partial))
        {
            if (!File.ReadAllBytes(partial).AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("Existing partial manifest differs from expected content.");
        }
        else
        {
            using var stream = new FileStream(
                partial, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(expected);
            stream.Flush(flushToDisk: true);
        }
        File.Move(partial, path); // no overwrite
        VerifyManifest(path, expected);
    }

    private static void VerifyManifest(string path, byte[] expected)
    {
        if (!File.Exists(path) ||
            !File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException("Published manifest missing or does not match the archive and ledger.");
    }

    private static string HashBytes(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static string DbTime(DateTime time) =>
        time.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    private static string UtcString(DateTime time) =>
        DateTime.SpecifyKind(time, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private static DateTime? ReadDate(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : reader.GetDateTime(column);

    private static long? ReadLong(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : reader.GetInt64(column);

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
