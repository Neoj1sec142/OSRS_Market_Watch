# OSRS Market Watch — Archive, Research & Operations Roadmap

**Project:** Nugget Capital Management LLC  
**Updated:** 2026-11-11  
**Current state:** Phase 4 Passes 1–4 completed and committed on Windows; Phase 4 Pass 5 proposed but **not yet tested or committed**. Phase 6 Python research can begin in parallel using read-only copies.  
**Production:** MacBook M1 .NET 10 collection service is running on its existing build; archive code and tests remain Windows-only.  
**Safety invariant:** `MarketStorage.EnableDestructiveRetention = false`. **No archive status or local checksum permits deleting source data.**

> **Mission: Collect once. Archive safely. Train repeatedly.**

## 1. Executive status — as actually verified

| Area | Status | Evidence / remaining limitation |
| --- | --- | --- |
| Five-minute Wiki collection on Mac | **Operational** | LaunchDaemon running (PID 4912 at inspection); no recorded process exit; recent eight successful batches five minutes apart on Nov 11. |
| Source database & available disk | **Healthy at inspection** | Database approximately 326 MiB, WAL approximately 9.6 MiB, 96 GiB free; these are point-in-time measurements. |
| Production networking | **Operational at inspection** | Wiki requests in logs returned 200; retry/failure injection remains incomplete. |
| HTTP health endpoint | **Not configured at `/health`** | `GET /health` returned 404; this does **not** indicate collection failure. Add a read-only endpoint later. |
| Power behavior | **Suitable on AC, lid open** | AC `sleep 0`, display sleep 60 minutes; battery sleep 1 minute. Lid-close behavior and outages remain risks. |
| Independent backup | **Created and copied off-device** | Nov 11 consistent SQLite `.backup`, integrity `ok`, no FK violations; Mac/Windows SHA-256 matched. **Restore drill not yet performed.** |
| Destructive retention | **Disabled** | Fail-closed guard deployed; independently verify effective production config and worker skip logs. |
| Archive ledger & planning | **Windows development only** | Source GUID, ranges, lifecycle, validation and failure of unused prepared plans. Cross-process planning and contiguous watermark hardening pending. |
| Working backup, extraction, publication | **Phase 4 Passes 1–4 passed** | Candidate row comparison, manifests, SHA-256, interrupted publication recovery and cross-process extraction/publication tested. |
| Archive transfer / commitment | **Not started** | No durable destination receipt, `Transferred`, `Verified`, or `Committed` archive; cleanup unauthorized. |
| Python archive pipeline | **Scaffold proposed; not verified in repo** | `archive_pipeline.py` and Pass 5 test harness were provided but no successful execution has been reported. |
| Angular GUI | **Not started** | Wait for stable read-only research and operational API contracts. |

**Verified off-device backup (2026-11-11):** `market_pre_vacation_20261111.db`, approximately 326 MiB. SHA-256: `00c27a069004fb04ef8772648819ad03ded6c487807b5f315ee24ff2581cb410`. The Mac and Windows hashes matched. This is a *point-in-time* copy, not an ongoing backup policy.

## 2. Architecture and non-negotiable safety contract

| Component | Location | Responsibility |
| --- | --- | --- |
| MarketAPI / collector | MacBook M1, production | Read OSRS Wiki market data every five minutes; insert snapshots, upsert aggregates, and retain history. |
| SQLite source (`market.db`, WAL) | MacBook M1 | Canonical active observation history until independently archived. |
| Development MarketAPI / archive services | Windows | Plan and test extraction/publication against **disposable, consistent SQLite copies**. |
| Immutable archive shards | Local Windows test artifacts; future durable destination | Versioned SQLite shards with embedded range metadata, external manifest, checksums and source lineage. |
| Python research pipeline | Windows | Verify copies, normalize source records, construct reproducible features/labels, run chronological evaluation. |
| Operations & research API | Future .NET backend | Serve safe collector status, archival coverage, market summaries, model results and data freshness. |
| Angular GUI | Future | Read-only operational dashboard and research exploration; not the storage/ML authority. |

### Safety rules

1. Never delete source observations without independently verified, durable, continuous **committed** archive coverage and a separate retention authorization/cursor.
2. `Prepared → Exported → Transferred → Verified → Committed` is a strict lifecycle. `Failed` preserves an audit trail. `Exported` means local publication **only**, not destination verification.
3. Snapshot ranges use `(StartIdExclusive, EndIdInclusive]`; five-minute and hourly ranges use `[StartUtcInclusive, EndUtcExclusive)`. Range continuity does **not** imply every numeric ID exists.
4. Preserve source IDs, exact stored quote values, UTC text timestamps, volumes, items and referenced batches. SQLite timestamp representation and decimal storage must be validated, not reformatted silently.
5. Source GUID is a lineage label, not sufficient proof against restored/forked histories or reused snapshot IDs.
6. Aggregates are mutable/upserted. Finalization windows (snapshots 48 h, five-minute 6 h, hourly 24 h) are **provisional delays**, not proof against late corrections/backfills.
7. All transfer/commit/retention operations must fail closed on checksum mismatch, source mismatch, range gap, overlap, destination loss or interrupted lifecycle transition.
8. Keep production collection isolated from research workloads. No experimental jobs or archive migrations on Mac before reviewed production readiness.
9. Never count the same observation twice when a dataset combines source history and archive shards; intervals 5 and 60 are different resolutions of market activity, not additive observations.
10. No automatic trading or investment decisions from preliminary research outputs; evaluate tax, buy limits, liquidity, stale prices and execution constraints separately.

### Current stream contract

| Stream | Identity / boundaries | Provisional archive delay |
| --- | --- | --- |
| Snapshots | Source GUID + snapshot `Id`, `(startId, endId]` | 48 hours |
| Five-minute aggregates | Source GUID + item ID + `Interval = 5` + UTC bucket, `[startUtc, endUtc)` | 6 hours and full bucket alignment |
| Hourly aggregates | Source GUID + item ID + `Interval = 60` + UTC bucket, `[startUtc, endUtc)` | 24 hours and full bucket alignment |

## 3. Completed and partially completed work

### Phase 0 — Production retention guard

**Status: Implemented and deployed; audit remaining.**

- [x] Review retention service and deletion hazards.
- [x] Implement fail-closed `MarketStorage.EnableDestructiveRetention = false` guard.
- [x] Deploy collector/guard build and verify continued ingestion on Mac.
- [x] Create consistent Mac backup, run SQLite integrity/FK checks, and copy it to Windows with matching SHA-256.
- [ ] Restore-test the independent backup to a **new disposable path**; validate schema and representative queries.
- [ ] Inspect live production retention-worker skip logs and effective configuration; confirm no destructive SQL has run.
- [ ] Confirm production `auto_vacuum`, WAL/checkpoint behavior and free-space telemetry.

**Configured but inactive retention windows:** snapshots 14 days; 5-minute aggregates 90 days; hourly aggregates 730 days; collection batches 30 days. Retention worker every 15 minutes; deletion batch size 5,000. **These are not currently active deletion rules.**

### Phase 1 — Collector network resilience

**Status: Deployed and observed working; fault matrix outstanding.**

- [x] Bounded transient retries/backoff/jitter for timeouts, DNS, 408, 429 and 5xx; avoid retrying permanent errors.
- [x] Reuse cached/persisted item metadata if Wiki mapping refresh fails.
- [x] Success/failure/recovery logging and successful Mac collections.
- [x] Observe latest eight Nov 11 collection batches at expected five-minute cadence (last logged: batch 497, 4,675 items, 1,641 snapshots, 4,660 aggregate writes, about 1.98 seconds).
- [ ] Run controlled test cases: 503→success, 429, DNS/timeout exhaustion, 403, cancellation, mapping failure and concurrent requests — **off production**.
- [ ] Add durable collector heartbeat/failure-streak telemetry and optional external alerting.
- [ ] Test a sustained outage and restart behavior without changing current vacation deployment.

### Phase 2 — Archive contract, EF schema and identity

**Status: Implemented on Windows; production migration intentionally deferred.**

- [x] Define `MarketArchiveSource`, `MarketArchiveExport`, `MarketArchiveStreamRange`, statuses and streams.
- [x] Add EF Core mappings and `AddMarketArchiveLedger` migration on Windows.
- [x] Preserve source GUID and reserve snapshot-ID / aggregate-UTC intervals.
- [x] Define embedded archive metadata and deterministic external manifest in exporter tests.
- [ ] Finalize versioning and backward compatibility for schema and manifest.
- [ ] Add explicit source restore/fork detection and monotonic lineage attestations.
- [ ] Define and test late aggregate revision/backfill handling.

### Phase 3 — Planning, validation and ledger inspection

**Status: Functional in Windows development; not yet safe for committed coverage.**

- [x] Persist `Prepared` plans and source identity across API restarts.
- [x] Reject pending plans in a single API process and reject incorrect prepared-range/source validation.
- [x] Implement read-only structural coverage inspection and failure of unused `Prepared` plans.
- [x] Verify aggregate range preparation: 12,067 five-minute rows + 7,970 hourly rows in the test fixture.
- [x] Verify a failed unused plan can be replaced without advancing committed history.
- [x] Verify `safeToAuthorizeCleanup = false` in development tests.
- [ ] Replace MAX-based committed endpoint selection with a validated **contiguous stream cursor**.
- [ ] Make plan creation atomic across processes; prevent overlapping pending reservations.
- [ ] Detect gaps, overlaps, malformed boundaries, missing first anchors and conflicting committed ranges.
- [ ] Test repeated fail transitions and lifecycle state guards.
- [ ] Address aggregate correction/backfill and source restore/fork hazards before commit or deletion.

### Phase 4 — Incremental SQLite exporter

**Status: Passes 1–4 fully tested on Windows and committed. Pass 5 pending.**

#### Pass 1 — Consistent working backup — COMPLETE

- [x] Freeze a consistent SQLite copy via `BackupDatabase()` instead of copying a live `.db` file.
- [x] Check integrity, foreign keys, data counts and independent SHA-256.
- [x] Keep the working copy out of Git and separate from the source.
- [ ] Benchmark backup latency and collection impact for future production execution.

**Fixture evidence:** 4,675 items; 35,746 snapshots; 29,964 aggregates; 23 collection batches; 13,410,304-byte working copy. This **full backup is not an incremental archive**.

#### Pass 2 — Selective extraction — COMPLETE

- [x] Implement and register `MarketArchiveExtractionService` and development endpoint.
- [x] Extract only reserved snapshot-ID and finalized aggregate bucket ranges.
- [x] Include referenced `MarketItems` and `MarketCollectionBatches` only.
- [x] Preserve original row values, IDs and lineage; embed manifest and stream ranges.
- [x] Check source equality, counts, SQLite integrity, foreign keys and SHA-256.
- [x] Reject existing candidate/partial files; keep ledger in `Prepared` and retention disabled.

**First aggregate-only fixture:** 3,566 referenced items; 12,067 five-minute aggregates; 7,970 hourly aggregates; 20,037 aggregate rows; zero eligible snapshots on that source at the time. The snapshot path was **subsequently tested in Pass 4**.

#### Pass 3 — Local publication and crash recovery — COMPLETE

- [x] Implement `MarketArchivePublicationService` and development publish endpoint.
- [x] Revalidate against a fresh frozen source before publication.
- [x] Publish without overwriting; generate deterministic external JSON manifest.
- [x] Record file size, archive SHA-256, manifest SHA-256 and `Exported` receipt.
- [x] Simulate interruption after archive move, then recover and reconcile the ledger.
- [x] Verify idempotent publication retry and that later states are unset.
- [x] Verify retention remains unauthorized.

**Evidence:** published archive `9376725e-9e5c-4363-8b24-3bd083c58725`, 3,080,192 bytes, 20,037 aggregate observations; status `Exported` only. Not transferred or committed.

#### Pass 4 — Hardening and snapshots — COMPLETE (6/6 tests)

- [x] Add local cross-process per-archive filesystem locks for extraction and publication.
- [x] Preserve published candidate after a later extraction error; reject overwrite.
- [x] Verify 35,746 snapshots, 18 referenced batches, and exact row equality to disposable source.
- [x] Reject structurally valid but altered candidate contents.
- [x] Reject altered published SQLite data and modified external manifests.
- [x] Simulate and recover interruptions before and after manifest rename.
- [x] Race two development API instances; verify extraction and publication return one success/one conflict, with valid final receipt.
- [x] Confirm no lifecycle commitment and no cleanup authorization.
- [x] Commit successful Pass 4 implementation.

**Limits:** File locks protect paths shared by the API processes, **not** independent hosts or planning across processes. Tests used simulated interruptions and disposable Windows databases, not power-loss testing on the Mac.

#### Pass 5 — Planner/ledger safety — NEXT BACKEND WORK; NOT VERIFIED

- [ ] Add and inspect a filtered unique index enforcing one unfinished archive per source (`Prepared` through `Verified`).
- [ ] Apply the migration **only** to a disposable Windows DB and test concurrent preparation.
- [ ] Implement validated contiguous coverage per stream; remove reliance on MAX committed endpoints as proof of continuity.
- [ ] Reject committed gaps, overlaps, malformed boundaries, invalid first snapshot anchor and misaligned aggregate boundaries.
- [ ] Define clear semantics for first aggregate coverage anchor and empty streams.
- [ ] Reject duplicate pending reservations at the database layer across two processes.
- [ ] Test concurrent prepare, duplicate inserts, simulated corrupt ledger and restart behavior.
- [ ] Keep `safeToAuthorizeCleanup = false` regardless of guard outcome.
- [ ] Review code, migrations and tests; commit only after successful Windows results.

**Implementation note:** A continuity *guard* that checks existing rows is helpful but is not equivalent to deriving a safe contiguous cursor. Verify the planner's actual start bounds and commit semantics. Earlier Pass 5 code and scripts were **proposed**, not shown as installed/tested.

#### Pass 6 — Source and mutable-history correctness — PENDING, BLOCKS COMMIT

- [ ] Detect source database restores/forks, snapshot ID reuse and archive source-lineage conflicts.
- [ ] Choose a correction strategy for aggregate upserts/backfills: revision shards, immutable cutoffs plus compensating revisions, or another auditable mechanism.
- [ ] Prevent late observations from being silently skipped by previously committed high-water marks.
- [ ] Define immutable archive identity, retry/reconciliation semantics and end-to-end lifecycle invariants.
- [ ] Add fault-injection/regression tests before enabling transfer-to-commit transitions.

## 4. Planned phases and release gates

### Phase 5 — Durable destination, transfer and commitment

**Status: Not started; gated by Passes 5–6 and disaster tests.**

- [ ] Select a durable destination and at least one independent recovery copy; define permissions and credentials.
- [ ] Transfer only complete, immutable exports; use no-overwrite and resumable/recoverable transport.
- [ ] Verify archive SHA-256, manifest SHA-256, SQLite integrity, schema, counts and row/range metadata **at the destination**.
- [ ] Record transfer receipt and verified storage location; enforce legal `Exported → Transferred → Verified → Committed` transitions.
- [ ] Reconcile crash at each stage; prevent duplicate or conflicting commitments.
- [ ] Advance committed watermarks **only** after verified contiguous source coverage, including mutable-history policy.
- [ ] Restore at least one archived range from the destination without access to the live source database.
- [ ] Keep destructive retention disabled until a separate Phase 7 authorization review.

### Phase 6 — Python data/ML research pipeline

**Status: Existing source-inspection groundwork; archive CLI scaffold previously supplied but not reported tested. This can start NOW, read-only, while archive safety work continues.**

#### 6A — Verified data access and a first reproducible dataset — NEXT PRIMARY DEVELOPMENT FOCUS

- [ ] Confirm `scripts/archive_pipeline.py` is present; run `inspect` and `verify` against the Pass 3 `Exported` file and its known manifest SHA-256.
- [ ] Run the reader against a **disposable copy** of the verified Mac backup (or isolated Windows DB); never mutate production.
- [ ] Establish canonical schema for item, timestamp, high/low prices, volumes, interval, source ID, collection batch and data freshness.
- [ ] Normalize timestamps explicitly to UTC; preserve raw source values and distinguish snapshot and aggregate semantics.
- [ ] Generate a sample single-item dataset (e.g., item `4151`), plus multi-item datasets, with source checksums and a generation manifest.
- [ ] Add dataset uniqueness checks for archive overlap, stale timestamps, missing buckets and resolution mixing.
- [ ] Compare pipeline output to independent SQL counts and source queries.
- [ ] Introduce Parquet output (with explicit pinned dependencies) after raw verification; CSV is an acceptable first milestone.

#### 6B — Feature engineering and deterministic labels

- [ ] Choose forecasting targets (e.g., future price movement, executable net margin, liquidity) and initial horizons such as 30 minutes/2 hours; verify what Wiki data can and cannot observe.
- [ ] Build lagged price/volume changes, spread, volatility, activity, staleness and time-of-day features.
- [ ] Model the economic meaning of instant buy vs instant sell; handle GE tax, buy limits and execution assumptions before claiming profit.
- [ ] Materialize labels strictly from future windows, never from the input feature window.
- [ ] Enforce point-in-time availability, warmup periods and embargo/purge between temporal splits where needed.
- [ ] Version feature definitions, training contract, item universe, data cutoffs and generated manifests.

#### 6C — Baselines, training and evaluation

- [ ] Establish no-trade, persistence and simple spread/liquidity heuristics as baselines.
- [ ] Create chronological train/validation/test and walk-forward evaluation; never random-shuffle future observations into training.
- [ ] Measure predictive accuracy, calibration, slippage/tax-adjusted hypothetical returns, drawdowns, turnover and coverage.
- [ ] Reject strategies whose performance depends on stale prices, look-ahead, survivorship bias or unrealistic instant fills.
- [ ] Train an initial simple supervised model only after the baseline and leakage tests pass.
- [ ] Package reproducible predictions with model version, as-of time, input lineage and confidence.

#### 6D — Scaling and repeatability

- [ ] Read multiple verified archive shards plus the active-data copy without duplicate rows.
- [ ] Reconstruct a dataset solely from archives after source retention begins.
- [ ] Add incremental feature generation, model registry/run history and deterministic regeneration tests.
- [ ] Define minimum data-history coverage and model freshness before presenting actionable rankings.

### Phase 7 — Archive-aware retention (DESTRUCTIVE; NOT STARTED)

- [ ] Keep deletion feature gated separately from `Committed` and disabled by default.
- [ ] Require destination verification, contiguous per-stream commitment, source-lineage safety and validated cleanup cursor.
- [ ] Preserve the latest per-item snapshot dedup baseline independently from old snapshots.
- [ ] Protect referenced batches, model windows, corrections and backfills.
- [ ] Implement dry-run SQL, audit trail, bounded deletion, recovery after partial cleanup and disk/WAL monitoring.
- [ ] Prove safe behavior on missing archives, checksum failures, revoked destination and interrupted deletes.
- [ ] Conduct separate human review before enabling any production deletion.

### Phase 8 — Reliability, restore and disaster recovery

- [ ] Test network outage, hung requests, power loss, process restarts and interrupted ingestion.
- [ ] Restore the off-device Mac backup to an isolated database and run representative queries.
- [ ] Exercise publisher crashes, orphan files, concurrent requests and malformed/corrupt archives.
- [ ] Test restored/forked source, late revised aggregates and discontinuous ledger history.
- [ ] Test failed transfer, unavailable destination, low disk space and complete archive-only dataset regeneration.
- [ ] Write and rehearse an operator recovery runbook with rollback and no-delete defaults.

### Phase 9 — Read-only backend operations and Angular GUI

**Status: Not started; start thin backend contracts after Phase 6A data contract is real.**

- [ ] Add a minimal `GET /health` or equivalent liveness endpoint and a **separate** freshness/readiness status, without exposing the Mac's API publicly.
- [ ] Expose last success, age of newest batch, failure streak, process uptime, disk/DB/WAL sizes and collection cadence.
- [ ] Expose read-only archive coverage, export state and verified destinations; clearly distinguish `Exported` from `Committed`.
- [ ] Define item price-series, feature-summary, backtest and prediction API contracts using small paginated requests.
- [ ] Implement Angular overview: collector freshness, item watchlist, price/volume charts, archive status and research results.
- [ ] Add request validation, access control, rate limiting and no-PHI/no-secret diagnostics before exposing external access.
- [ ] Keep retention toggles, direct trading, and destructive archive buttons out of the first GUI.

## 5. Recommended execution sequence

| Order | Work | Why now | Exit criterion |
| --- | --- | --- | --- |
| **P0 — Before vacation** | Freeze Mac production build; keep AC/lid-open setup; verify latest successful batch and independent backup. | Protect uninterrupted ingestion. | Recent successful collection, matching off-device backup hash, no unexpected sleep. |
| **P1 — First development sprint** | **Phase 6A read-only Python pipeline** on the existing export + isolated backup. | Turns stored data into usable research now without any production migration. | One verified, reproducible dataset with exact counts, UTC semantics and checksums. |
| **P1 — Parallel safety sprint** | Phase 4 Pass 5 planner lock/continuity plus tests, Windows only. | Essential before ever committing archive watermarks; can coexist with read-only research. | Two-process and malformed-ledger suite passed; no cleanup authorization. |
| **P2** | Phase 6B features/labels and Phase 6C simple baselines. | Prove the data can answer useful market questions before building a UI. | Leak-free walk-forward baseline report for a handful of items. |
| **P2** | Phase 4 Pass 6 source-fork/late-correction semantics. | Required for long-term immutable history and safe commitment. | Deterministic policy and passing injected correction/restore tests. |
| **P3** | Phase 9 thin read-only API + minimal Angular GUI. | Build UI around actual features, datasets and diagnostics rather than placeholders. | Item charts + data freshness + basic research results from stable endpoints. |
| **P3** | Phase 5 permanent transfer/verification. | Makes source storage sustainable; needs safety invariants first. | Destination verified; legal state transitions; independent restore. |
| **P4 — Last** | Phase 7 retention enablement. | Highest irreversible risk; not needed to start ML. | Explicit review, full coverage and recovery tests; feature remains fail-closed until then. |

### Immediate next coding checkpoint (recommended)

1. **Do not deploy anything new to the Mac before vacation.** Keep collecting and confirm the next few batches after moving it.
2. In the Windows repo, check `scripts/archive_pipeline.py` exists and is not a stale or partial download. If missing, implement a small `inspect`/`verify` module first.
3. Verify the known Pass 3 published archive by manifest and file SHA. Remember that it contains aggregates only; use a disposable source copy for snapshot-rich research.
4. Generate a reproducible dataset for item `4151`, inspect its row count and chronological windows, and record the pipeline version/hash.
5. Draft the canonical feature/label contract **before** adding a model or GUI.
6. Separately implement and run the Pass 5 migration/continuity tests on a new Windows disposable database; do not touch production.
7. Ship a first read-only research result (price/spread/volume history + baseline forecast), then build a thin API and Angular chart around it.

**Decision:** **Pipeline first, minimal backend hardening in parallel, GUI after real data contracts.** We do **not** need to finish destructive retention to begin research. We **do** need to complete backend safety before archive commitment or deletion.

## 6. Development workflow and release gates

1. Name exact file and method; use BEFORE → AFTER for incremental changes and complete code for new/replacement files.
2. Build on Windows and test on disposable database copies; never overwrite `MarketAPI/Data/market.db` or `archive_planner_test.db` without explicit, reviewed intent.
3. Require integrity, foreign-key, identity, exact-range, checksum, idempotence and failure-path tests for archival changes.
4. Record what has been **proposed**, **implemented**, **tested**, **committed**, and **deployed** as separate states.
5. Include data provenance, timestamp semantics, leakage checks and reproducibility for pipeline changes.
6. Keep generated databases, manifests, backups, datasets and lock files outside Git. Review `git status`, `git diff --check`, staging and migrations before each commit.
7. Do not put experimental archive code, migrations, exporter scheduling, retention, or training jobs on the Mac during vacation.
8. Establish production backup cadence/monitoring and a restore drill before any future destructive migration.

## 7. Decision log and unresolved risks

| Date | Decision / observation |
| --- | --- |
| 2026-11-10 | Archive first; no deletion before destination verification and continuous committed coverage. |
| 2026-11-10 | Use separate snapshot-ID and aggregate-UTC streams, preserve raw SQLite values and lineage. |
| 2026-11-10 | Frozen working backup is an extraction input, **not** an archive shard. |
| 2026-11-11 | Pass 3 publication recovered an interrupted file move and reconciled `Prepared → Exported`. |
| 2026-11-11 | Pass 4 passed all six scenarios, including 35,746 snapshots/18 batches and two-process races. |
| 2026-11-11 | Mac LaunchDaemon showed successful five-minute collection and 96 GiB available; `/health` returned 404 and should be added later. |
| 2026-11-11 | Verified SQLite backup copied off-device with matching SHA-256; **restore test pending**. |
| 2026-11-11 | Keep Mac production frozen for vacation; begin Windows **read-only pipeline** while completing archive planner safety. |

**Key unresolved risks:** no automatic alerting; no restore drill; possible sleep if lid closes or AC fails; source restore/fork; aggregate revisions/backfills; unverified committed continuity; no permanent transfer destination; no archive-only end-to-end ML regeneration; research signal profitability not established.

---

**Nugget Capital Management LLC — Collect once. Archive safely. Train repeatedly.**

**MEOW.**
