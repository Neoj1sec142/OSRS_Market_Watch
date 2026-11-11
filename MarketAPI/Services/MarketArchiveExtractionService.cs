
using System.Globalization;
using System.Security.Cryptography;
using MarketAPI.Enums;
using Microsoft.Data.Sqlite;

namespace MarketAPI.Services;

// An export-only prototype. It NEVER changes the live archive ledger.
public sealed record ArchiveCandidateResult(
    Guid ArchiveId,
    Guid SourceId,
    string CandidatePath,
    string WorkingBackupPath,
    long FileSizeBytes,
    string Sha256,
    long ItemCount,
    long BatchCount,
    long SnapshotCount,
    long FiveMinuteCount,
    long HourlyCount,
    string IntegrityResult,
    bool ForeignKeysValid,
    bool RowValuesMatch,
    string Status);

public sealed class MarketArchiveExtractionService
{
    private sealed record Range(
        ArchiveStream Stream,
        long? StartId,
        long? EndId,
        DateTime? StartUtc,
        DateTime? EndUtc,
        long ExpectedCount);

    private readonly MarketArchiveBackupService _backups;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<MarketArchiveExtractionService> _logger;

    public MarketArchiveExtractionService(
        MarketArchiveBackupService backups,
        IWebHostEnvironment environment,
        ILogger<MarketArchiveExtractionService> logger)
    {
        _backups = backups;
        _environment = environment;
        _logger = logger;
    }

    public async Task<ArchiveCandidateResult> CreateCandidateAsync(
        Guid archiveId,
        CancellationToken ct = default)
    {
        if (!_environment.IsDevelopment())
            throw new InvalidOperationException("Candidate extraction is development-only.");

        if (archiveId == Guid.Empty)
            throw new ArgumentException("Archive ID is required.", nameof(archiveId));

        var outputDirectory = Path.Combine(
            _environment.ContentRootPath, "artifacts", "archive-candidates");
        Directory.CreateDirectory(outputDirectory);

        var stem = $"market_{archiveId:N}";
        var partialPath = Path.Combine(outputDirectory, stem + ".partial.db");
        var candidatePath = Path.Combine(outputDirectory, stem + ".candidate.db");
        var lockPath = Path.Combine(outputDirectory, $".{archiveId:N}.extract.lock");

        // FileShare.None provides a cross-process, per-archive extraction lock.
        // Do not remove lock files: unlinking a live lock creates a second inode
        // and can permit simultaneous writers on some filesystems.
        await using var extractionLock = new FileStream(
            lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        if (File.Exists(partialPath) || File.Exists(candidatePath))
            throw new IOException(
                "A candidate or partial file already exists for this archive. " +
                "Inspect it rather than overwrite it.");

        // Freeze the plan and source *after* acquiring the extraction lock.
        var working = await _backups.CreateWorkingBackupAsync(ct);
        ct.ThrowIfCancellationRequested();

        try
        {
            var result = Extract(
                working.FilePath, partialPath, archiveId, ct);

            ct.ThrowIfCancellationRequested();
            File.Move(partialPath, candidatePath);

            using var hashStream = File.OpenRead(candidatePath);
            var sha256 = Convert.ToHexString(SHA256.HashData(hashStream))
                .ToLowerInvariant();
            var size = new FileInfo(candidatePath).Length;

            _logger.LogInformation(
                "UNCOMMITTED archive candidate {ArchiveId} at {Path} ({Bytes} bytes).",
                archiveId, candidatePath, size);

            return new ArchiveCandidateResult(
                archiveId, result.SourceId, candidatePath, working.FilePath,
                size, sha256, result.ItemCount, result.BatchCount,
                result.SnapshotCount, result.FiveMinuteCount, result.HourlyCount,
                "ok", true, true, "CandidateOnly_NotExportedOrCommitted");
        }
        catch
        {
            // Remove ONLY the temporary file we created. If the rename
            // succeeded before a later exception, preserve the candidate for
            // inspection/recovery; never silently delete a published artifact.
            foreach (var path in new[]
            {
                partialPath,
                partialPath + "-wal",
                partialPath + "-shm"
            })
            {
                if (File.Exists(path)) File.Delete(path);
            }
            throw;
        }
    }

    private sealed record ExtractionCounts(
        Guid SourceId, long ItemCount, long BatchCount,
        long SnapshotCount, long FiveMinuteCount, long HourlyCount);

    private static ExtractionCounts Extract(
        string workingPath, string destinationPath, Guid archiveId, CancellationToken ct)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        db.Open();

        // The working backup is an isolated, immutable-for-this-operation copy.
        // No SQL statements below modify the attached src database.
        Execute(db, "ATTACH DATABASE $path AS src;", ("$path", workingPath));

        Guid sourceId;
        DateTime createdAtUtc;
        List<Range> ranges;

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT e.SourceId, e.CreatedAtUtc, e.SchemaVersion
                FROM src.MarketArchiveExports e
                JOIN src.MarketArchiveSources s ON s.Id = 1
                WHERE lower(e.Id) = lower($id)
                  AND e.Status = 0
                  AND lower(e.SourceId) = lower(s.SourceId);
                """;
            cmd.Parameters.AddWithValue("$id", archiveId.ToString("D"));
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException(
                    "The working snapshot does not contain this Prepared archive and matching source identity.");
            sourceId = Guid.Parse(reader.GetString(0));
            createdAtUtc = reader.GetDateTime(1);
            if (reader.GetInt32(2) != 1)
                throw new InvalidOperationException("Unsupported archive schema version.");
        }

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT Stream, StartIdExclusive, EndIdInclusive,
                       StartUtcInclusive, EndUtcExclusive, RecordCount
                FROM src.MarketArchiveStreamRanges
                WHERE lower(ArchiveExportId) = lower($id)
                ORDER BY Stream;
                """;
            cmd.Parameters.AddWithValue("$id", archiveId.ToString("D"));
            ranges = new List<Range>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ranges.Add(new Range(
                    (ArchiveStream)reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                    reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                    reader.GetInt64(5)));
            }
        }
        if (ranges.Count is < 1 or > 3 ||
            ranges.Select(x => x.Stream).Distinct().Count() != ranges.Count)
            throw new InvalidOperationException("Invalid number of archive stream ranges.");

        foreach (var range in ranges)
        {
            ct.ThrowIfCancellationRequested();
            ValidatePlanRange(db, range, createdAtUtc);
        }

        // A candidate has its own compact schema, independent from application tables.
        // Preserve raw SQLite column values through INSERT ... SELECT, not C# conversions.
        Execute(db, """
            CREATE TABLE MarketItems (
                Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Examine TEXT, Icon TEXT,
                Members INTEGER NOT NULL, BuyLimit INTEGER, HighAlchValue INTEGER,
                LowAlchValue INTEGER, IsActive INTEGER NOT NULL,
                IsTaxExempt INTEGER NOT NULL, LastMetadataUpdateUtc TEXT NOT NULL);
            CREATE TABLE MarketCollectionBatches (
                Id INTEGER PRIMARY KEY, StartedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT, Source TEXT NOT NULL, Status INTEGER NOT NULL,
                ItemsCollected INTEGER NOT NULL, ErrorMessage TEXT);
            CREATE TABLE MarketPriceSnapshots (
                Id INTEGER PRIMARY KEY, MarketItemId INTEGER NOT NULL,
                CollectionBatchId INTEGER NOT NULL, InstantBuyPrice INTEGER,
                InstantSellPrice INTEGER, InstantBuyTimeUtc TEXT,
                InstantSellTimeUtc TEXT, CollectedAtUtc TEXT NOT NULL,
                FOREIGN KEY (MarketItemId) REFERENCES MarketItems(Id),
                FOREIGN KEY (CollectionBatchId) REFERENCES MarketCollectionBatches(Id),
                UNIQUE (CollectionBatchId, MarketItemId));
            CREATE TABLE MarketPriceAggregates (
                Id INTEGER PRIMARY KEY, MarketItemId INTEGER NOT NULL,
                Interval INTEGER NOT NULL, BucketStartUtc TEXT NOT NULL,
                AverageInstantBuyPrice TEXT, AverageInstantSellPrice TEXT,
                InstantBuyVolume INTEGER NOT NULL, InstantSellVolume INTEGER NOT NULL,
                CollectedAtUtc TEXT NOT NULL,
                FOREIGN KEY (MarketItemId) REFERENCES MarketItems(Id),
                UNIQUE (MarketItemId, Interval, BucketStartUtc));
            CREATE TABLE ArchiveManifest (
                ArchiveId TEXT PRIMARY KEY, SourceId TEXT NOT NULL,
                SchemaVersion INTEGER NOT NULL, PreparedAtUtc TEXT NOT NULL,
                Format TEXT NOT NULL);
            CREATE TABLE ArchiveRanges (
                Stream INTEGER PRIMARY KEY, StartIdExclusive INTEGER,
                EndIdInclusive INTEGER, StartUtcInclusive TEXT,
                EndUtcExclusive TEXT, RecordCount INTEGER NOT NULL);
            """);

        // Build contents within one SQLite transaction. Source is already frozen.
        using (var transaction = db.BeginTransaction())
        {
            // Facts are copied before their referenced metadata; defer FK checks
            // until the transaction commits with all referenced rows present.
            Execute(db, "PRAGMA defer_foreign_keys = ON;", transaction);

            Execute(db, """
                INSERT INTO ArchiveManifest
                    (ArchiveId, SourceId, SchemaVersion, PreparedAtUtc, Format)
                VALUES ($archive, $source, 1, $created, 'market-archive-candidate-v1');
                """, transaction,
                ("$archive", archiveId.ToString("D")),
                ("$source", sourceId.ToString("D")),
                ("$created", DbTime(createdAtUtc)));

            foreach (var range in ranges)
            {
                Execute(db, """
                    INSERT INTO ArchiveRanges
                        (Stream, StartIdExclusive, EndIdInclusive, StartUtcInclusive,
                         EndUtcExclusive, RecordCount)
                    VALUES ($stream, $startId, $endId, $startUtc, $endUtc, $count);
                    """, transaction,
                    ("$stream", (int)range.Stream),
                    ("$startId", range.StartId is null ? (object)DBNull.Value : range.StartId.Value),
                    ("$endId", range.EndId is null ? (object)DBNull.Value : range.EndId.Value),
                    ("$startUtc", range.StartUtc is null ? (object)DBNull.Value : DbTime(range.StartUtc.Value)),
                    ("$endUtc", range.EndUtc is null ? (object)DBNull.Value : DbTime(range.EndUtc.Value)),
                    ("$count", range.ExpectedCount));
            }

            var snaps = ranges.SingleOrDefault(x => x.Stream == ArchiveStream.Snapshots);
            if (snaps is not null)
                Execute(db, """
                    INSERT INTO MarketPriceSnapshots
                        (Id, MarketItemId, CollectionBatchId, InstantBuyPrice,
                         InstantSellPrice, InstantBuyTimeUtc, InstantSellTimeUtc,
                         CollectedAtUtc)
                    SELECT Id, MarketItemId, CollectionBatchId, InstantBuyPrice,
                           InstantSellPrice, InstantBuyTimeUtc, InstantSellTimeUtc,
                           CollectedAtUtc
                    FROM src.MarketPriceSnapshots
                    WHERE Id > $start AND Id <= $end;
                    """, transaction,
                    ("$start", snaps.StartId!.Value), ("$end", snaps.EndId!.Value));

            foreach (var stream in new[] {
                ArchiveStream.FiveMinuteAggregates, ArchiveStream.HourlyAggregates })
            {
                var range = ranges.SingleOrDefault(x => x.Stream == stream);
                if (range is null) continue;
                var interval = stream == ArchiveStream.FiveMinuteAggregates
                    ? (int)AggregateInterval.FiveMinutes
                    : (int)AggregateInterval.OneHour;
                Execute(db, """
                    INSERT INTO MarketPriceAggregates
                        (Id, MarketItemId, Interval, BucketStartUtc,
                         AverageInstantBuyPrice, AverageInstantSellPrice,
                         InstantBuyVolume, InstantSellVolume, CollectedAtUtc)
                    SELECT Id, MarketItemId, Interval, BucketStartUtc,
                           AverageInstantBuyPrice, AverageInstantSellPrice,
                           InstantBuyVolume, InstantSellVolume, CollectedAtUtc
                    FROM src.MarketPriceAggregates
                    WHERE Interval = $interval
                      AND BucketStartUtc >= $start AND BucketStartUtc < $end;
                    """, transaction,
                    ("$interval", interval),
                    ("$start", DbTime(range.StartUtc!.Value)),
                    ("$end", DbTime(range.EndUtc!.Value)));
            }

            Execute(db, """
                INSERT INTO MarketCollectionBatches
                    (Id, StartedAtUtc, CompletedAtUtc, Source, Status,
                     ItemsCollected, ErrorMessage)
                SELECT b.Id, b.StartedAtUtc, b.CompletedAtUtc, b.Source,
                       b.Status, b.ItemsCollected, b.ErrorMessage
                FROM src.MarketCollectionBatches b
                WHERE b.Id IN (SELECT CollectionBatchId FROM main.MarketPriceSnapshots);
                """, transaction);

            Execute(db, """
                INSERT INTO MarketItems
                    (Id, Name, Examine, Icon, Members, BuyLimit, HighAlchValue,
                     LowAlchValue, IsActive, IsTaxExempt, LastMetadataUpdateUtc)
                SELECT i.Id, i.Name, i.Examine, i.Icon, i.Members, i.BuyLimit,
                       i.HighAlchValue, i.LowAlchValue, i.IsActive,
                       i.IsTaxExempt, i.LastMetadataUpdateUtc
                FROM src.MarketItems i
                WHERE i.Id IN (
                    SELECT MarketItemId FROM main.MarketPriceSnapshots
                    UNION
                    SELECT MarketItemId FROM main.MarketPriceAggregates);
                """, transaction);

            transaction.Commit();
        }

        ct.ThrowIfCancellationRequested();

        var snapCount = ScalarLong(db, "SELECT COUNT(*) FROM MarketPriceSnapshots;");
        var fiveCount = ScalarLong(db, "SELECT COUNT(*) FROM MarketPriceAggregates WHERE Interval = 5;");
        var hourCount = ScalarLong(db, "SELECT COUNT(*) FROM MarketPriceAggregates WHERE Interval = 60;");
        if (snapCount != (ranges.SingleOrDefault(x => x.Stream == ArchiveStream.Snapshots)?.ExpectedCount ?? 0) ||
            fiveCount != (ranges.SingleOrDefault(x => x.Stream == ArchiveStream.FiveMinuteAggregates)?.ExpectedCount ?? 0) ||
            hourCount != (ranges.SingleOrDefault(x => x.Stream == ArchiveStream.HourlyAggregates)?.ExpectedCount ?? 0))
            throw new InvalidOperationException("Candidate row counts differ from the prepared plan.");

        // EXCEPT in both directions checks every stored column, including nullable values.
        VerifyRows(db, "MarketPriceSnapshots",
            "Id, MarketItemId, CollectionBatchId, InstantBuyPrice, InstantSellPrice, InstantBuyTimeUtc, InstantSellTimeUtc, CollectedAtUtc",
            "Id IN (SELECT Id FROM main.MarketPriceSnapshots)");
        VerifyRows(db, "MarketPriceAggregates",
            "Id, MarketItemId, Interval, BucketStartUtc, AverageInstantBuyPrice, AverageInstantSellPrice, InstantBuyVolume, InstantSellVolume, CollectedAtUtc",
            "Id IN (SELECT Id FROM main.MarketPriceAggregates)");
        VerifyRows(db, "MarketItems",
            "Id, Name, Examine, Icon, Members, BuyLimit, HighAlchValue, LowAlchValue, IsActive, IsTaxExempt, LastMetadataUpdateUtc",
            "Id IN (SELECT Id FROM main.MarketItems)");
        VerifyRows(db, "MarketCollectionBatches",
            "Id, StartedAtUtc, CompletedAtUtc, Source, Status, ItemsCollected, ErrorMessage",
            "Id IN (SELECT Id FROM main.MarketCollectionBatches)");

        if (ScalarLong(db, "SELECT COUNT(*) FROM pragma_foreign_key_check;") != 0)
            throw new InvalidOperationException("Candidate foreign key check failed.");

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals(Convert.ToString(cmd.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Candidate SQLite integrity check failed.");
        }

        return new ExtractionCounts(sourceId,
            ScalarLong(db, "SELECT COUNT(*) FROM MarketItems;"),
            ScalarLong(db, "SELECT COUNT(*) FROM MarketCollectionBatches;"),
            snapCount, fiveCount, hourCount);
    }

    private static void ValidatePlanRange(
        SqliteConnection db, Range range, DateTime createdAtUtc)
    {
        if (range.ExpectedCount <= 0)
            throw new InvalidOperationException("Empty archive range is invalid.");

        if (range.Stream == ArchiveStream.Snapshots)
        {
            if (range.StartId is null || range.EndId is null ||
                range.StartId < 0 || range.StartId >= range.EndId ||
                range.StartUtc is not null || range.EndUtc is not null)
                throw new InvalidOperationException("Malformed snapshot range.");

            var actual = ScalarLong(db, """
                SELECT COUNT(*) FROM src.MarketPriceSnapshots
                WHERE Id > $start AND Id <= $end;
                """, ("$start", range.StartId.Value), ("$end", range.EndId.Value));
            var tooNew = ScalarLong(db, """
                SELECT COUNT(*) FROM src.MarketPriceSnapshots
                WHERE Id > $start AND Id <= $end AND CollectedAtUtc >= $cutoff;
                """, ("$start", range.StartId.Value), ("$end", range.EndId.Value),
                ("$cutoff", DbTime(createdAtUtc.AddHours(-48))));
            if (actual != range.ExpectedCount || tooNew != 0)
                throw new InvalidOperationException("Snapshot plan count or eligibility validation failed.");
            return;
        }

        var (interval, bucketMinutes, delayHours) = range.Stream switch
        {
            ArchiveStream.FiveMinuteAggregates => ((int)AggregateInterval.FiveMinutes, 5, 6),
            ArchiveStream.HourlyAggregates => ((int)AggregateInterval.OneHour, 60, 24),
            _ => throw new InvalidOperationException("Unknown archive stream.")
        };
        if (range.StartId is not null || range.EndId is not null ||
            range.StartUtc is null || range.EndUtc is null ||
            range.StartUtc >= range.EndUtc)
            throw new InvalidOperationException("Malformed aggregate range.");

        var duration = TimeSpan.FromMinutes(bucketMinutes);
        var cutoff = createdAtUtc.AddHours(-delayHours);
        var alignedCutoff = new DateTime(
            cutoff.Ticks - cutoff.Ticks % duration.Ticks, DateTimeKind.Utc);
        if (range.StartUtc.Value.Ticks % duration.Ticks != 0 ||
            range.EndUtc.Value.Ticks % duration.Ticks != 0 ||
            range.EndUtc > alignedCutoff)
            throw new InvalidOperationException("Aggregate bucket bounds are not finalized or aligned.");

        var count = ScalarLong(db, """
            SELECT COUNT(*) FROM src.MarketPriceAggregates
            WHERE Interval = $interval
              AND BucketStartUtc >= $start AND BucketStartUtc < $end;
            """, ("$interval", interval),
            ("$start", DbTime(range.StartUtc.Value)),
            ("$end", DbTime(range.EndUtc.Value)));
        if (count != range.ExpectedCount)
            throw new InvalidOperationException("Aggregate plan count no longer matches the frozen source.");
    }

    // Confirms no source value differs from the copied archive row.
    // Count + identity matching above ensures there are no missing or extra rows.
    private static void VerifyRows(
        SqliteConnection db, string table, string columns, string sourceFilter)
    {
        var missing = ScalarLong(db, $"""
            SELECT COUNT(*) FROM (
                SELECT {columns} FROM src.{table} WHERE {sourceFilter}
                EXCEPT SELECT {columns} FROM main.{table}
            );
            """);
        var extra = ScalarLong(db, $"""
            SELECT COUNT(*) FROM (
                SELECT {columns} FROM main.{table}
                EXCEPT SELECT {columns} FROM src.{table} WHERE {sourceFilter}
            );
            """);
        if (missing != 0 || extra != 0)
            throw new InvalidOperationException($"Row values diverged in {table}.");
    }

    private static string DbTime(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    private static long ScalarLong(
        SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(
        SqliteConnection db, string sql,
        params (string Name, object Value)[] parameters) =>
        Execute(db, sql, null, parameters);

    private static void Execute(
        SqliteConnection db, string sql, SqliteTransaction? transaction,
        params (string Name, object Value)[] parameters)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
