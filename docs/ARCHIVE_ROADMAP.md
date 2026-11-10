
# OSRS Market Watch — Development Roadmap

**Project:** Nugget Capital Management LLC  
**Updated:** 2026-11-10  
**Current Phase:** Phase 2 — Incremental Archive Contract  
**Primary Goal:** Build a reliable, long-running OSRS market data platform with permanent historical archives, reproducible ML datasets, and bounded MacBook storage.

---

## Core Architecture

### MacBook M1 — Production Collector
- .NET 10 MarketAPI running as a LaunchDaemon.
- SQLite database with WAL enabled.
- Five-minute market ingestion.
- HTTP retry handling and cached item mapping.
- Archive-first retention protection.
- Temporary working data and incremental export source.

### Windows PC — Research Platform
- Python ML pipeline.
- Permanent historical SQLite archives.
- Dataset inspection, generation, and verification.
- Model training, validation, and artifact management.
- Optional external drive for archival storage.

### Architectural Rules

1. NEVER delete data that has not been safely archived.
2. NEVER commit overlapping historical export ranges.
3. NEVER advance archival watermarks before verifying the transferred archive.
4. Preserve original source observations for future dataset regeneration.
5. Keep the live Mac database reasonably small.
6. Treat generated training datasets as replaceable artifacts.
7. Preserve immutable archive files and their manifests.
8. Keep ingestion, archival, and model training independently testable.
9. Support recovery from interrupted exports and transfers.
10. All destructive operations must fail closed.

---

# Phase 0 — Retention Safety

**Status:** Implemented; final verification pending.

## Completed

- [x] Review MarketRetentionService.
- [x] Review MarketRetentionWorker.
- [x] Review MarketStorageOptions.
- [x] Review current retention configuration.
- [x] Identify unsafe age-only historical deletion.
- [x] Design fail-closed retention protection.
- [x] Implement retention safety guard.
- [x] Deploy updated application to MacBook.
- [x] Verify application startup after deployment.
- [x] Verify SQLite connectivity.
- [x] Review SqliteMaintenanceService implementation.

## Remaining

- [ ] Confirm retention skip message after scheduled worker execution.
- [ ] Verify no destructive retention SQL executes.
- [ ] Confirm SQLite auto_vacuum configuration on Mac.
- [ ] Verify production effective retention configuration.
- [ ] Copy a verified backup to independent storage.
- [ ] Perform an actual backup restore/read test.

## Current Protection

MarketStorage.EnableDestructiveRetention = false

The retention service must reject attempts to enable
destructive cleanup until archive-aware verification exists.

## Acceptance Criteria

- Collector continues operating normally.
- Unarchived market data cannot be automatically deleted.
- A verified database backup can be restored successfully.

---

# Phase 1 — Network Resilience

**Status:** Deployed; fault-injection testing pending.

## Completed

- [x] Add bounded HTTP retries.
- [x] Add exponential backoff and jitter.
- [x] Handle transient DNS and network failures.
- [x] Handle HTTP 408, 429, and 5xx responses.
- [x] Avoid retrying permanent HTTP errors.
- [x] Reduce HTTP request timeout.
- [x] Cache item mapping refresh state.
- [x] Reuse persisted item IDs between mapping refreshes.
- [x] Continue price ingestion when metadata refresh fails and existing metadata is available.
- [x] Add collector attempt and recovery logging.
- [x] Test consecutive successful Windows ingestions.
- [x] Confirm mapping is not fetched every collection.
- [x] Deploy updated osx-arm64 application.
- [x] Confirm successful Mac collection after deployment.

## Remaining

- [ ] Test HTTP 503 followed by success.
- [ ] Test HTTP 429 and retry behavior.
- [ ] Test DNS resolution failures.
- [ ] Test HTTP timeout exhaustion.
- [ ] Test HTTP 403 without retry.
- [ ] Test application cancellation.
- [ ] Test metadata failure with an empty database.
- [ ] Test metadata failure with previously stored items.
- [ ] Test simultaneous scheduled/manual ingestion behavior.
- [ ] Consider persistent collection failure telemetry.

## Acceptance Criteria

Transient failures recover automatically without corrupting
or duplicating market data.

---

# Phase 2 — Incremental Archive Contract

**Status:** IN PROGRESS

## Completed Analysis

- [x] Review MarketDbContext.
- [x] Review MarketPriceSnapshot model.
- [x] Review MarketPriceAggregate model.
- [x] Review MarketCollectionBatch model.
- [x] Review SqliteMarketBulkWriter.
- [x] Review SqliteMaintenanceService.
- [x] Confirm snapshot append-only insertion behavior.
- [x] Confirm snapshot INTEGER PRIMARY KEY AUTOINCREMENT.
- [x] Confirm snapshot unique index on CollectionBatchId and MarketItemId.
- [x] Confirm aggregate upsert behavior.
- [x] Confirm aggregate unique identity:
      MarketItemId + Interval + BucketStartUtc.
- [x] Confirm SQLite UTC timestamp text format.
- [x] Confirm hourly buckets use hour-aligned UTC timestamps.
- [x] Define independent archival streams.
- [x] Define initial archive lifecycle states.
- [x] Design initial archive ledger entities and EF mappings.

## Implementation Tasks

- [ ] Create ArchiveStatus enum.
- [ ] Create ArchiveStream enum.
- [ ] Create MarketArchiveSource model.
- [ ] Create MarketArchiveExport model.
- [ ] Create MarketArchiveStreamRange model.
- [ ] Register archive DbSets in MarketDbContext.
- [ ] Add EF Core archive mappings.
- [ ] Generate AddMarketArchiveLedger migration.
- [ ] Review migration for unintended schema changes.
- [ ] Apply migration to Windows development database.
- [ ] Verify archive tables and indexes.

## Archive Streams

### Snapshot Stream

Identity:
    SourceDatabaseId + SnapshotId

Range:
    (StartIdExclusive, EndIdInclusive]

Rules:
- Archive previously uncommitted snapshot IDs only.
- Preserve original snapshot IDs.
- Preserve collection batch references.
- Preserve original source timestamps.
- Use durable source identity to detect restored/rebuilt databases.
- Never assume timestamp order is equivalent to ID order.
- Prevent ID reuse or sequence reset after cleanup.

### Five-Minute Aggregate Stream

Identity:
    SourceDatabaseId + MarketItemId
    + Interval + BucketStartUtc

Range:
    [StartUtcInclusive, EndUtcExclusive)

Rules:
- Archive finalized time buckets.
- Do not archive actively updating buckets.
- Use configurable finalization delay.
- Handle late corrections explicitly.
- Avoid overlapping committed bucket ranges.

### Hourly Aggregate Stream

Identity:
    SourceDatabaseId + MarketItemId
    + Interval + BucketStartUtc

Range:
    [StartUtcInclusive, EndUtcExclusive)

Rules:
- Archive finalized hourly buckets.
- Preserve prices and buy/sell volumes.
- Use independent hourly archive watermark.
- Support historical corrections without silently duplicating effective observations.

## Remaining Design Decisions

- [ ] Define source database identity initialization.
- [ ] Define initial watermarks for all three streams.
- [ ] Define snapshot archive eligibility cutoff.
- [ ] Define five-minute finalization delay.
- [ ] Define hourly finalization delay.
- [ ] Define late aggregate correction policy.
- [ ] Define handling for historical backfills.
- [ ] Define safe behavior after restoring an old live database.
- [ ] Define archive manifest schema.
- [ ] Define archive schema versioning.
- [ ] Define exact record-count verification rules.
- [ ] Define checksum verification requirements.
- [ ] Define how concurrent archive planning is prevented.
- [ ] Define crash recovery and pending export handling.

## Archive Lifecycle

Prepared
    |
    v
Exported
    |
    v
Transferred
    |
    v
Verified
    |
    v
Committed

Failed exports must never authorize cleanup.

Cleanup progress must be tracked separately from archive status.

## Acceptance Criteria

Archive identities, range boundaries, lifecycle transitions,
and failure recovery rules are explicitly defined and tested.

---


# Phase 3 — Archive Ledger & Planning Service

**Status:** IN PROGRESS

## Completed — Initial Implementation

- [x] Define first-pass planning rules for three data streams.
- [x] Implement MarketArchivePlanningService.
- [x] Register archive planner in dependency injection.
- [x] Implement persistent source database identity initialization.
- [x] Implement initial committed stream watermark discovery.
- [x] Implement initial next-range planning.
- [x] Implement preliminary stream finalization delays.
- [x] Store prepared archive ranges.
- [x] Add initial planning logs and diagnostics.
- [x] Prevent duplicate pending plans within one application instance.

## Completed — Validation & Testing

- [x] Verify source identity persists across application restarts.
- [x] Add development-only planning interface.
- [x] Test duplicate/pending archive rejection.
- [x] Test initial watermark discovery with no committed ranges.
- [x] Test prepared archive persistence.
- [x] Add preliminary source-range validation endpoint.
- [x] Validate prepared aggregate ranges against source counts.
- [x] Validate freshly prepared aggregate ranges.
- [x] Verify corrected aggregate finalization boundaries.
- [x] Verify archive preparation on isolated development database.
- [x] Verify Prepared archives do not advance committed progress.
- [x] Test controlled failure of a Prepared archive.
- [x] Verify failed reservations preserve their original ranges.
- [x] Test creating a replacement plan after failure.
- [x] Verify failed reservations do not advance watermarks.
- [x] Validate replacement archive ranges.

- [ ] Test repeated failure transition returns HTTP 409.
- [ ] Test committed-range gaps and overlaps.
- [ ] Enforce cross-process archive reservation safety.
- [ ] Define late aggregate correction policy.
- [ ] Detect restored or divergent source database histories.
- [ ] Add automated integration tests.

## Pending — Production Hardening
- [ ] Replace MAX-based watermark selection with
      contiguous committed coverage validation.
- [ ] Validate every snapshot in a prepared ID range
      satisfies archival eligibility.
- [ ] Enforce cross-process archive reservation safety.
- [ ] Finalize late aggregate correction handling.
- [ ] Define historical backfill handling.
- [ ] Prevent overlapping export plans across processes.
- [ ] Implement archive state transition validation.
- [ ] Implement failed export recovery.
- [ ] Implement archive verification receipt model.
- [ ] Implement committed watermark calculation
      with contiguous coverage verification.
- [ ] Protect against restored database identity conflicts.
- [ ] Validate archive range continuity and completeness.

## Safety Requirements
- [ ] Verify archive preparation is idempotent across
      concurrent application processes.
- Prepared archives must not advance committed watermarks.
- Failed archives must not authorize cleanup.
- Archive ranges must never overlap committed history.
- Source identity must remain stable across normal deployments.
- Historical backfills must not silently escape archival coverage.
- Only verified, committed archives may authorize future cleanup.
- Destructive retention remains disabled.

## Acceptance Criteria

- The service creates a persistent source identity.
- Eligible archive ranges are calculated correctly.
- Planning is repeatable and safely rejects pending conflicts.
- All planned ranges are persisted atomically.
- No market observations are modified during planning.
- Failed or incomplete plans do not advance committed progress.
- The planner is tested against a disposable database copy.
- No destructive cleanup is enabled.


---

# Phase 4 — Incremental SQLite Archive Exporter

**Status:** NOT STARTED

## Tasks

- [ ] Implement consistent SQLite backup preparation.
- [ ] Implement incremental snapshot extraction.
- [ ] Implement finalized five-minute aggregate extraction.
- [ ] Implement finalized hourly aggregate extraction.
- [ ] Include referenced MarketCollectionBatches.
- [ ] Include required MarketItems metadata.
- [ ] Preserve original source values and identifiers.
- [ ] Create versioned archive SQLite schema.
- [ ] Generate unique archive ID and filename.
- [ ] Write archive manifest.
- [ ] Calculate source/export row counts.
- [ ] Verify SQLite integrity.
- [ ] Calculate SHA-256 checksum.
- [ ] Finalize temporary export atomically.
- [ ] Record archive export completion.
- [ ] Handle interrupted exports safely.
- [ ] Test duplicate prevention.

## Proposed Filename Format

market_<YYYYMMDD>_<SEQUENCE>.db

Example:
market_20261110_000001.db

File dates are descriptive.
Archive IDs and manifests are authoritative.

## Acceptance Criteria

Each export produces one verified, immutable SQLite archive
containing only eligible, previously uncommitted observations.

---

# Phase 5 — Transfer & Permanent Archive Storage

**Status:** NOT STARTED

## Tasks

- [ ] Define configurable Windows archive destination.
- [ ] Support external drive destination.
- [ ] Implement secure transfer workflow.
- [ ] Transfer only completed archive files.
- [ ] Verify destination file checksum.
- [ ] Run destination SQLite integrity check.
- [ ] Record verified destination location.
- [ ] Preserve immutable archives.
- [ ] Maintain independent recovery copy.
- [ ] Implement transfer retry/recovery.
- [ ] Prevent premature removal of temporary source files.
- [ ] Commit archival ranges only after durable verification.

## Proposed Windows Structure

NuggetCapital/
    archives/
        2026/
            market_20261110_000001.db
            market_20261117_000002.db
    manifests/
    datasets/
    models/
    reports/

## Acceptance Criteria

Archives remain accessible after Mac database cleanup
and pass integrity and checksum verification.

---

# Phase 6 — Python Training Dataset Pipeline

**Status:** NOT STARTED

## Proposed CLI

scripts/export_training_data.py

### Inspect

python scripts/export_training_data.py inspect
    --archive <path-to-archive>

### Generate

python scripts/export_training_data.py generate
    --archive <path-to-archive>
    --output <destination>
    --profile market-v1

### Verify

python scripts/export_training_data.py verify
    --dataset <dataset-directory>

## Tasks

- [ ] Implement archive schema reader.
- [ ] Implement archive manifest validation.
- [ ] Implement inspect command.
- [ ] Implement generate command.
- [ ] Implement verify command.
- [ ] Support configurable output directory.
- [ ] Support Parquet datasets.
- [ ] Preserve source archive lineage.
- [ ] Version training feature definitions.
- [ ] Version target/label definitions.
- [ ] Avoid look-ahead bias and target leakage.
- [ ] Support cross-archive feature windows.
- [ ] Support cross-archive future labels.
- [ ] Implement chronological train/validation/test splits.
- [ ] Support regenerating one archive's dataset.
- [ ] Support regenerating all historical datasets.
- [ ] Verify effective training rows are not duplicated.
- [ ] Record generation code/version and parameters.
- [ ] Generate dataset manifest and checksums.

## Acceptance Criteria

Historical ML datasets can be regenerated from permanent
archives without accessing the original live database.

---

# Phase 7 — Archive-Aware Retention

**Status:** NOT STARTED — DESTRUCTIVE CLEANUP DISABLED

## Tasks

- [ ] Replace temporary retention safety guard with verified archive checks.
- [ ] Implement per-stream cleanup eligibility.
- [ ] Require committed archive coverage before deletion.
- [ ] Enforce configured live-data retention windows.
- [ ] Preserve enough recent data for analytics.
- [ ] Protect incomplete/future training labels.
- [ ] Preserve latest snapshot deduplication state per item.
- [ ] Protect collection batches still referenced by snapshots.
- [ ] Use bounded deletion batches.
- [ ] Preserve DatabaseWriteCoordinator synchronization.
- [ ] Track cleanup progress independently.
- [ ] Implement retention dry-run mode.
- [ ] Implement disk-space warning thresholds.
- [ ] Review WAL checkpoint scheduling.
- [ ] Review incremental vacuum behavior.
- [ ] Measure storage usage before and after cleanup.
- [ ] Test interrupted deletion recovery.

## Critical Deduplication Requirement

Current ingestion compares a new snapshot with the most
recent snapshot still present in the live database.

Deleting historical snapshots may remove that baseline.

Before enabling cleanup, persist each item's latest
observation state independently of retained history.

This prevents unchanged quotes from being reinserted
after archival cleanup.

## Acceptance Criteria

Only verified, permanently archived historical records
are deleted, without breaking ingestion or analytics.

---

# Phase 8 — Failure & Recovery Testing

**Status:** NOT STARTED

## Tests

- [ ] Network outage during collection.
- [ ] HTTP retry exhaustion.
- [ ] Collector shutdown during ingestion.
- [ ] Archive creation interrupted.
- [ ] Archive transfer interrupted.
- [ ] Checksum mismatch.
- [ ] Corrupt destination archive.
- [ ] Duplicate archive request.
- [ ] Overlapping export plan.
- [ ] Missing historical range.
- [ ] Late aggregate correction.
- [ ] Database restore from old backup.
- [ ] Snapshot ID reuse prevention.
- [ ] Archive manifest mismatch.
- [ ] Cleanup attempted without archive.
- [ ] Cleanup attempted after failed transfer.
- [ ] Cleanup interrupted halfway through.
- [ ] Low disk-space condition.
- [ ] Regeneration from multiple archive databases.
- [ ] Cross-archive feature/label boundaries.

## Acceptance Criteria

Failures do not silently lose observations, commit
invalid archives, or allow unsafe deletion.

---

# Phase 9 — Operational Monitoring & Dashboard

**Status:** NOT STARTED

## Tasks

- [ ] Expose collector health through API.
- [ ] Expose last successful collection.
- [ ] Expose consecutive failures.
- [ ] Expose current database size.
- [ ] Expose disk-space health.
- [ ] Expose archive history.
- [ ] Expose committed watermarks.
- [ ] Expose pending/failed exports.
- [ ] Expose last verified transfer.
- [ ] Expose cleanup eligibility.
- [ ] Add Angular archive operations dashboard.
- [ ] Add manual archive preparation control.
- [ ] Add archive verification status.
- [ ] Add retention dry-run visibility.

## Acceptance Criteria

The entire data lifecycle can be inspected and operated
without manually querying SQLite or parsing logs.

---

# Confirmed Technical Decisions

## Production Environment

- Runtime: .NET 10
- Database: SQLite / EF Core 10
- Platform: macOS ARM64, MacBook M1
- Collection interval: 5 minutes
- API: localhost-only Kestrel
- Process manager: macOS LaunchDaemon

## Current Database Behavior

- Snapshot primary key: INTEGER PRIMARY KEY AUTOINCREMENT
- Snapshot deduplication: latest retained item observation
- Snapshot uniqueness: CollectionBatchId + MarketItemId
- Aggregate uniqueness: MarketItemId + Interval + BucketStartUtc
- Aggregate behavior: UPSERT
- Timestamp storage: SQLite TEXT, UTC, 7 fractional digits
- Hourly interval value: 60
- Collection batches: referenced by snapshot foreign keys

## Current Retention Configuration

- Snapshots: 14 days
- Five-minute aggregates: 90 days
- Hourly aggregates: 730 days
- Collection batches: 30 days
- Delete batch size: 5000
- Worker schedule: 15 minutes

NOTE: Destructive retention is currently disabled.
These retention windows are not active deletion rules
until archive-aware cleanup is implemented.

## Archive Strategy

- Immutable SQLite archive shards.
- Original raw observations permanently preserved.
- One committed archival representation per source record.
- Explicit handling for late aggregate corrections.
- Independent archive cursors for each stream.
- Versioned manifest and SHA-256 verification.
- Windows/external storage for permanent archives.
- Python generates replaceable training datasets.
- Safe cleanup only after verified durable transfer.

---

# Current Development Checkpoint

**Date:** 2026-11-10

## Verified

- [x] Mac LaunchDaemon running.
- [x] MarketAPI health endpoint responding.
- [x] Production SQLite connected.
- [x] Successful post-deployment ingestion (batch 257).
- [x] Updated collector telemetry deployed.
- [x] Normal metadata caching verified.
- [x] Snapshot AUTOINCREMENT verified.
- [x] UTC timestamp storage verified.
- [x] Aggregate identity and upsert behavior reviewed.

## Still Pending

- [ ] Complete retention safety verification.
- [ ] Complete fault-injection tests.
- [ ] Implement archive ledger entities/migration.
- [ ] Define aggregate finalization and correction policy.
- [ ] Implement archive planning service.
- [ ] Implement export-only incremental archive prototype.

---

# Immediate Next Steps

1. Implement archive ledger entities and EF Core mappings.
2. Generate and inspect AddMarketArchiveLedger migration.
3. Apply migration to Windows development SQLite.
4. Verify schema and constraints.
5. Define initial source identity and archival watermarks.
6. Implement Archive Planning Service.
7. Test range selection without creating archives.
8. Proceed to incremental SQLite exporter.

**Important:** Do not enable destructive retention yet.

---

# Decision Log

## 2026-11-10 — Archive-first architecture
Decided to preserve permanent immutable SQLite archive
shards and regenerate ML datasets from historical sources.

## 2026-11-10 — Retention protection
Age-based cleanup disabled until verified archive
coverage can authorize deletion.

## 2026-11-10 — Network hardening
Added bounded HTTP retries, metadata refresh caching,
and collector failure/recovery telemetry.

## 2026-11-10 — Incremental identities
Snapshots use source identity plus AUTOINCREMENT ID.
Aggregates use source identity, item ID, interval,
and source bucket start timestamp.

## 2026-11-10 — Archive verification
Committed archival progress requires successful export,
destination transfer, integrity verification, and
independent recovery protection.

## 2026-11-10 — Historical regeneration
Permanent raw archives are the source of truth.
Training datasets and ML artifacts may be regenerated
with newer algorithms and feature versions.

## 2026-11-10 — Archive Range Validation

Aggregate archive eligibility requires the complete
source bucket to fall outside the finalization delay.

Prepared archives can be checked using a read-only
validation endpoint.

Range validation does not authorize archive commitment,
data export verification, or destructive retention.


---

# Development Rule

For every future development pass:

1. Identify exact files being modified.
2. Show BEFORE and AFTER for incremental changes.
3. Provide full replacement methods when appropriate.
4. Identify full-class replacements by filename.
5. Verify Windows build and functionality.
6. Test failures before Mac deployment.
7. Update relevant roadmap checkboxes.
8. Commit tested milestones to Git.
9. Never enable destructive cleanup without verified
   archive coverage and recovery testing.

---

# Nugget Capital Management LLC

**Collect once. Archive safely. Train repeatedly.**

MEOW.
