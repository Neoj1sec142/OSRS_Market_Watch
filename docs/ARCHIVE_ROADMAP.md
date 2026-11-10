# OSRS Market Watch — Archive & Research Roadmap

**Project:** Nugget Capital Management LLC  
**Updated:** 2026-11-10  
**Current milestone:** Phase 4, Pass 2 — Incremental SQLite archive extraction  
**Production status:** Collector running on MacBook; archive implementation and tests remain Windows-only.  
**Non-negotiable:** Destructive retention remains **DISABLED** until permanent archives are independently verified and contiguous coverage authorizes cleanup.

> **Mission:** Collect once. Archive safely. Train repeatedly.

## 1. Architecture & Safety Contract

| Environment | Responsibilities |
| --- | --- |
| **MacBook M1 (production)** | .NET 10 MarketAPI LaunchDaemon, SQLite WAL database, five-minute Wiki market collection, bounded retries, cached item mappings. Remains the source of observations until data is durably archived. |
| **Windows PC (development / research)** | Isolated SQLite test copies, archive generation and validation, permanent archive storage (eventually external/independent media), Python ML research and reproducible datasets. |

### Rules that must always hold

1. Never delete source observations unless durable, verified archive coverage explicitly permits it.
2. Never advance committed watermarks on `Prepared`, `Exported`, `Transferred`, `Failed`, or merely locally verified files.
3. Never commit overlapping or discontinuous ranges; detect gaps and conflicting source histories.
4. Preserve original IDs, UTC timestamps, quote values, volumes, and necessary reference metadata.
5. Treat source-database GUIDs as lineage identifiers, **not** proof against restored/forked databases.
6. Treat aggregates as mutable: late upserts, revisions, and historical backfills require a defined policy.
7. Produce versioned immutable SQLite shards with manifests and checksums; verify the **destination** before commitment.
8. Never equate a full working backup with an incremental archive shard.
9. Keep ingestion, export, transfer, retention, and ML training independently testable.
10. Fail closed on crashes, integrity mismatches, missing ranges, or unavailable archive destinations.

### Archive identities & range conventions

| Stream | Identity | Half-open / exclusive boundary semantics | First-pass finalization delay |
| --- | --- | --- | --- |
| Snapshots | Source GUID + snapshot ID | `(StartIdExclusive, EndIdInclusive]` | 48 hours |
| Five-minute aggregates | Source GUID + item ID + interval + bucket UTC | `[StartUtcInclusive, EndUtcExclusive)` | 6 hours, aligned to complete buckets |
| Hourly aggregates | Source GUID + item ID + interval + bucket UTC | `[StartUtcInclusive, EndUtcExclusive)` | 24 hours, aligned to complete buckets |

Snapshot IDs are SQLite `INTEGER PRIMARY KEY AUTOINCREMENT`. Aggregates are upserted using `(MarketItemId, Interval, BucketStartUtc)`; hourly interval value is `60`. SQLite UTC timestamps are stored as TEXT with seven fractional digits. Initial finalization delays are **provisional**, not proof against late changes.

Archive lifecycle: `Prepared → Exported → Transferred → Verified → Committed`; `Failed` is a recoverable terminal outcome for a particular attempt. Cleanup progress is separate from archive status.

---

## 2. Completed Foundations

### Phase 0 — Retention guard
**Status:** Safety guard deployed; production verification incomplete.

- [x] Review retention service, worker, options, maintenance, and deletion risks.
- [x] Implement fail-closed destructive-retention guard.
- [x] Deploy collector/guard changes to MacBook; verify startup and successful ingestion.
- [ ] Verify scheduled worker skip logs and absence of destructive SQL in production.
- [ ] Confirm effective Mac retention configuration and `auto_vacuum` setting.
- [ ] Make and restore-test an independent production backup.

**Current setting:** `MarketStorage.EnableDestructiveRetention = false`. Retention windows below are configured but **not active deletion rules**.

### Phase 1 — Network resilience
**Status:** Deployed; fault injection outstanding.

- [x] Bounded HTTP retry with backoff/jitter for transient DNS, timeout, 408, 429, and 5xx failures.
- [x] Do not retry permanent HTTP errors; use bounded timeouts.
- [x] Cache mapping refresh state and reuse persisted item metadata if refresh fails.
- [x] Add collection failure/recovery logging; confirm successful Windows and Mac ingestion.
- [ ] Test 503→success, 429, DNS failure, timeout exhaustion, 403, cancellation, metadata failures with/without cached items, concurrent manual/scheduled ingestion.
- [ ] Evaluate persisted failure telemetry.

### Phase 2 — Archive data contract & ledger schema
**Status:** Implemented on Windows development database; production deployment deferred.

- [x] Review market schema and bulk ingestion behavior.
- [x] Define independent snapshot, five-minute, and hourly archival streams.
- [x] Define archive identities, source GUID, statuses, and initial ranges.
- [x] Implement `ArchiveStatus`, `ArchiveStream`, `MarketArchiveSource`, `MarketArchiveExport`, and `MarketArchiveStreamRange`.
- [x] Add EF Core mappings/DbSets, generate `AddMarketArchiveLedger`, and apply migration to Windows development database.
- [x] Verify archive tables using disposable development databases.
- [ ] Finalize archive manifest schema, archive schema versioning, value-level verification, and transfer receipts.
- [ ] Define restore/fork policy, aggregate revisions/backfills, and cross-process reservations.

### Phase 3 — Planning, validation & basic recovery
**Status:** Implemented and manually validated for eligible aggregates; production hardening outstanding.

**Implemented**
- [x] `MarketArchivePlanningService` with persistent source identity, preliminary committed-watermark discovery, finalization delay checks, and `Prepared` range persistence.
- [x] Pending-reservation rejection within one application instance.
- [x] Development endpoints for status, preparation, archive details, prepared-range validation, coverage inspection, and failure of unused `Prepared` reservations.
- [x] `MarketArchiveLedgerService` for read-only **structural** committed-range inspection and controlled prepared-plan failure.
- [x] Preserve failed attempts and allow a new reservation without advancing committed progress.

**Verified on disposable Windows databases**
- [x] Source identity survives API restart in the same database.
- [x] Aggregate cutoff alignment fix; new plans validate with source counts and boundaries.
- [x] Initial pending request returns conflict; prepared plan persists.
- [x] Five-minute range: 12,067 rows; hourly range: 7,970 rows; both validate for the tested fixture.
- [x] `Prepared` ranges do not count as committed coverage; `SafeToAuthorizeCleanup` stays false.
- [x] Failure of an unused prepared plan and preparation/validation of an equivalent replacement using the same source GUID.

**Still required before commitment or cleanup**
- [ ] Verify repeated failure transition returns HTTP 409.
- [ ] Test snapshot eligibility, ID-order edge cases, and snapshot range continuity.
- [ ] Test deliberate committed gaps, overlaps, malformed boundaries, and missing first ranges.
- [ ] Replace **MAX committed endpoint** logic with verified **contiguous** committed coverage in the planner.
- [ ] Make reservations atomic across processes (SQLite transaction/constraints, not only `SemaphoreSlim`).
- [ ] Handle late aggregate corrections and historical backfills without silently skipping changes.
- [ ] Detect restored/forked source database histories and snapshot ID reuse hazards.
- [ ] Validate legal lifecycle transitions and crash recovery from every intermediate state.
- [ ] Add automated integration tests covering planner, ledger, concurrency, and failure paths.

**Known limitation:** `AllInitializedStreamsContiguous = true` when zero streams are initialized is a vacuous structural result. It **never** authorizes cleanup. Current `SafeToAuthorizeCleanup` remains hard-coded `false`.

---

## 3. Current Work — Phase 4: Incremental SQLite Exporter

**Status:** **Pass 1 verified**; **Pass 2 not implemented**.

### Pass 1 — Consistent working backup **COMPLETE (Windows test)**

- [x] Review `DatabaseWriteCoordinator`, hosting/bootstrap, SQLite connection configuration, and package versions.
- [x] Confirm WAL journal mode, `auto_vacuum = 2` (incremental), and `foreign_keys = 1` on Windows test source.
- [x] Implement development-only `MarketArchiveBackupService` and `POST /api/dev/archive/working-backup`.
- [x] Create a consistent full SQLite working copy via the provider backup API (not raw `File.Copy`).
- [x] Verify `PRAGMA integrity_check = ok` and no `foreign_key_check` violations.
- [x] Independently verify the working file SHA-256 using PowerShell.
- [x] Keep backup output isolated from the source DB and outside Git.

**Verified fixture — 2026-11-10:**

| Metric | Result |
| --- | ---: |
| Market items | 4,675 |
| Snapshots | 35,746 |
| Aggregates | 29,964 |
| Collection batches | 23 |
| Working backup size | 13,410,304 bytes |
| Integrity / foreign keys / SHA-256 | Passed / passed / matched |

Working backup SHA-256: `80ec5dcbba8a2d5763113999ac3a0715cf1f76f8fa107c22ecab1a52f58b23f4`.

**Scope limit:** This is a **full working snapshot**, not an incremental archive shard. No archive lifecycle status was advanced. `BackupDatabase()` is synchronous and may interfere with concurrent writes; production scheduling/performance still needs evaluation.
### Pass 2 — Incremental Extraction

- [ ] Implement MarketArchiveExtractionService.
- [ ] Register extraction service and development endpoint.
- [ ] Extract reserved snapshot ID ranges.
- [ ] Extract finalized five-minute aggregate ranges.
- [ ] Extract finalized hourly aggregate ranges.
- [ ] Include only referenced market item metadata.
- [ ] Include referenced collection batch metadata.
- [ ] Preserve source IDs and stored field values.
- [ ] Embed archive identity and range metadata.
- [ ] Verify exported counts against prepared ranges.
- [ ] Verify exported rows against frozen source values.
- [ ] Verify SQLite integrity and foreign keys.
- [ ] Verify candidate SHA-256 independently.
- [ ] Reject duplicate candidate extraction.
- [ ] Confirm archive ledger remains Prepared.
- [ ] Confirm destructive retention remains disabled.
### Pass 2 — Incremental archive extraction **NEXT**

- [ ] Review exact EF models/mappings and current planner/ledger range fields.
- [ ] Select an existing `Prepared` reservation and validate it against the frozen working copy.
- [ ] Extract only the planned snapshot ID and aggregate UTC ranges.
- [ ] Copy required `MarketItems` and `MarketCollectionBatches` reference rows.
- [ ] Preserve original IDs, timestamps, quantities, prices, and source lineage.
- [ ] Create a versioned SQLite archive schema with an embedded manifest.
- [ ] Verify row counts, identities, interval boundaries, and value-level source equality.
- [ ] Verify archive SQLite integrity and foreign keys.
- [ ] Compute final-file SHA-256 and publish from a temporary file without overwriting an existing shard.
- [ ] Leave the export **uncommitted** and do not enable retention.
- [ ] Test creation, rejection, failure/retry, and cleanup on a disposable Windows database.

### Pass 3 — Export lifecycle / reliability **PLANNED**

- [ ] Implement guarded `Prepared → Exported` transition with exact file size, hash, manifest hash, and recorded source ranges.
- [ ] Prevent mismatched backup/plan source identities and stale snapshot races.
- [ ] Define restart recovery, orphaned temporary files, re-exports, naming collisions, and duplicate prevention.
- [ ] Measure backup duration and collector impact; define production-safe execution limits.
- [ ] Add automated exporter integration tests and failure injection.

**Filename convention:** current ledger uses `market_<YYYYMMDD>_<archive-guid-N>.db`; dates are descriptive, while manifest, source identity, and archive ID are authoritative.

---

## 4. Future Phases

### Phase 5 — Transfer & permanent storage
**Status:** Not started.

- [ ] Define configurable destination on Windows/external drive and independent recovery copy.
- [ ] Transfer only complete, validated shards; retry interrupted transfers.
- [ ] Recalculate destination SHA-256; validate SQLite integrity, schema, manifest, and row counts.
- [ ] Record durable transfer/verification receipts and storage location.
- [ ] Implement strictly validated `Exported → Transferred → Verified → Committed` transitions.
- [ ] Advance watermarks only after continuous, verified, durable coverage.
- [ ] Preserve immutable archive artifacts and manifests.

### Phase 6 — Python ML datasets
**Status:** Archive-based pipeline not started; existing dataset-inspection utilities are separate groundwork.

- [ ] Read versioned archives and verify manifests/hashes.
- [ ] Implement archive-aware `inspect`, `generate`, and `verify` CLI operations.
- [ ] Produce reproducible Parquet datasets with lineage, code/config versions, and checksums.
- [ ] Support multiple archives, overlapping feature windows, and future-label windows.
- [ ] Prevent look-ahead bias and training-row duplication.
- [ ] Use chronological train/validation/test partitions.
- [ ] Regenerate datasets without relying on the original live DB.

### Phase 7 — Archive-aware retention
**Status:** **Not started — destructive cleanup disabled.**

- [ ] Gate every deletion on verified contiguous committed coverage and a separate cleanup cursor.
- [ ] Preserve latest per-item snapshot state independently of retained snapshots (dedup baseline).
- [ ] Respect live-data retention windows and incomplete feature/label windows.
- [ ] Protect referenced batches; delete in bounded, recoverable batches.
- [ ] Add dry-run, audit output, disk-space warnings, and WAL/vacuum monitoring.
- [ ] Test restart halfway through deletion, missing archives, and failed transfers.

Configured windows (**inactive while guard is on**): snapshots 14 days; five-minute aggregates 90 days; hourly aggregates 730 days; collection batches 30 days; worker every 15 minutes; deletion batch size 5,000.

### Phase 8 — Failure & disaster recovery tests
**Status:** Not started as a comprehensive suite; isolated manual tests are listed above.

- [ ] Network outage / canceled collection / ingestion interruption.
- [ ] Export crash, orphan file, checksum mismatch, corrupt archive, manifest mismatch.
- [ ] Duplicate request, concurrent export, range gap/overlap, late correction/backfill.
- [ ] Database restore/fork and snapshot ID sequence conflicts.
- [ ] Failed destination transfer, deletion attempted without coverage, deletion interruption.
- [ ] Low disk space and multi-archive ML regeneration.

### Phase 9 — Monitoring & Angular operations
**Status:** Not started as a unified dashboard; some collector health/logging already exists.

- [ ] Expose DB and disk size, last successful collection, failures, and collector health.
- [ ] Expose archive plans, statuses, verified destinations, committed coverage, and cleanup eligibility.
- [ ] Add manual archive management, verification status, and retention dry-run UI.
- [ ] Show meaningful warnings for uninitialized streams, stale data, and interrupted operations.

---

## 5. Immediate Next Steps

1. **Gather source definitions for Phase 4 Pass 2:** `MarketDbContext`, four market entities (`MarketItem`, `MarketCollectionBatch`, `MarketPriceSnapshot`, `MarketPriceAggregate`), `MarketArchivePlanningService`, `MarketArchiveExport`, and `MarketArchiveStreamRange`.
2. Implement extraction from a validated frozen source snapshot into a **small, versioned SQLite shard**.
3. Validate exact rows, foreign keys, manifest, boundaries, checksum, and safe atomic publication.
4. Test exclusively on disposable Windows SQLite data; clean test artifacts afterward.
5. Complete Phase 3 concurrency/continuity/backfill hardening **before** granting archive commitment or cleanup authority.

**Environment guardrails:** Mac collector unchanged; Windows test uses isolated database path and development-only endpoints; working files live in `MarketAPI/artifacts/archive-work/` and belong in `.gitignore`.

## 6. Development Workflow

For each pass:

1. Name precise files/methods.
2. Show **BEFORE → AFTER** for incremental edits; provide complete methods/classes for replacements.
3. Build on Windows and test against an isolated database.
4. Run happy-path and failure-path verification.
5. Update only the relevant roadmap sections and checkboxes.
6. Review `git diff --check`, stage only intended source/docs/migrations, and commit tested checkpoints.
7. **Never** deploy unfinished archive features or enable destructive retention on Mac.

## 7. Decision Log

| Date | Decision |
| --- | --- |
| 2026-11-10 | Archive-first retention: no deletion until independently verified durable coverage. |
| 2026-11-10 | Separate snapshot-ID and UTC aggregate-bucket streams; preserve original source identities. |
| 2026-11-10 | Aggregate buckets must be **complete** before crossing the eligibility cutoff. |
| 2026-11-10 | `Prepared` plans are reservations, not committed history; failed attempts preserve audit records. |
| 2026-11-10 | Structural ledger continuity alone cannot authorize cleanup. |
| 2026-11-10 | Full SQLite working backups are verified temporary extraction inputs, **not** final immutable archives. |
| 2026-11-10 | Source restore/fork detection, late corrections, cross-process serialization, and contiguous committed coverage are mandatory before cleanup. |

---

**Nugget Capital Management LLC — Collect once. Archive safely. Train repeatedly.**  
**MEOW.**
