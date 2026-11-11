
"""OSRS Market Watch — Phase 4 Pass 4, disposable Windows integration tests.

Run from anywhere:
    python scripts/archive_pass4_tests.py
    python scripts/archive_pass4_tests.py --cleanup

Requires a successful `dotnet build` of MarketAPI in Debug/net10.0.
Uses only the Python standard library. Does NOT edit Data/market.db or
Data/archive_planner_test.db. Leaves fixtures and artifacts for inspection
until explicit --cleanup; cleanup uses an inventory of created files.
"""
from __future__ import annotations

import argparse
import concurrent.futures
from contextlib import closing
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import sys
import threading
import time
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parents[1]
API_ROOT = ROOT / "MarketAPI"
DB_DIR = API_ROOT / "Data"
ARTIFACT_ROOT = API_ROOT / "artifacts"
RUN_DIR = ARTIFACT_ROOT / "archive-pass4"
REPORT = RUN_DIR / "pass4-run.json"
SOURCE = DB_DIR / "market.db"
DLL = API_ROOT / "bin" / "Debug" / "net10.0" / "MarketAPI.dll"
PORT1, PORT2 = 5067, 5068
CASES = ("snapshots", "candidate_corruption", "published_corruption",
         "manifest_corruption", "manifest_recovery", "cross_process")
ALLOWED_ARTIFACT_DIRS = ("archive-work", "archive-candidates", "archive-exports", "archive-pass4")
FIXTURES = {case: DB_DIR / f"archive_pass4_{case}.db" for case in CASES}


def ensure(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def file_hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def db_readonly(path: Path) -> sqlite3.Connection:
    return sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True)


def artifact_files() -> set[Path]:
    found: set[Path] = set()
    for name in ALLOWED_ARTIFACT_DIRS:
        folder = ARTIFACT_ROOT / name
        if folder.is_dir():
            found.update(p.resolve() for p in folder.rglob("*") if p.is_file())
    return found


def recreate_fixture(case: str, age_snapshots: bool = False) -> Path:
    target = FIXTURES[case]
    ensure(target.name == f"archive_pass4_{case}.db", "Unexpected fixture name")
    ensure(not target.exists(), f"Fixture already exists: {target}. Use --cleanup first.")
    ensure(SOURCE.is_file(), f"Missing source database: {SOURCE}")
    ensure(SOURCE.resolve() != target.resolve(), "Source equals destination")
    print(f"  Creating disposable fixture: {target.name}", flush=True)
    # sqlite backup API includes committed WAL rows; source remains read-only.
    with closing(db_readonly(SOURCE)) as src:
        with closing(sqlite3.connect(target)) as dest:
            src.backup(dest)
    with closing(sqlite3.connect(target)) as db:
        db.execute("PRAGMA foreign_keys = ON")
        tables = {r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        required = {"MarketItems", "MarketPriceSnapshots", "MarketPriceAggregates",
                    "MarketCollectionBatches", "MarketArchiveSources", "MarketArchiveExports",
                    "MarketArchiveStreamRanges"}
        ensure(required.issubset(tables), f"Source lacks archive schema: {required - tables}")
        ensure(db.execute("SELECT COUNT(*) FROM MarketPriceSnapshots").fetchone()[0] > 0,
               "No snapshots in source fixture")
        ensure(db.execute("SELECT COUNT(*) FROM MarketPriceAggregates").fetchone()[0] > 0,
               "No aggregates in source fixture")
        # Clear ONLY the copied ledger so each case starts with fresh identity.
        db.execute("DELETE FROM MarketArchiveExports")  # range FK cascades
        db.execute("DELETE FROM MarketArchiveSources")
        if age_snapshots:
            stamp = dt.datetime.now(dt.timezone.utc) - dt.timedelta(hours=96)
            formatted = stamp.strftime("%Y-%m-%d %H:%M:%S.") + f"{stamp.microsecond:06d}0"
            db.execute("UPDATE MarketPriceSnapshots SET CollectedAtUtc = ?", (formatted,))
        db.commit()
        ensure(db.execute("PRAGMA integrity_check").fetchone()[0] == "ok", "Fixture integrity failure")
        ensure(not db.execute("PRAGMA foreign_key_check").fetchone(), "Fixture foreign-key failure")
    return target


def unused_port(port: int) -> None:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.settimeout(0.7)
        ensure(s.connect_ex(("127.0.0.1", port)) != 0,
               f"Port {port} already occupied. Refusing to contact an unknown API instance.")


def request(url: str, method: str = "GET", timeout: int = 120) -> tuple[int, dict[str, Any]]:
    req = Request(url, method=method, headers={"Accept": "application/json"})
    try:
        with urlopen(req, timeout=timeout) as response:
            raw = response.read().decode("utf-8")
            return response.status, json.loads(raw) if raw else {}
    except HTTPError as error:
        raw = error.read().decode("utf-8", errors="replace")
        try:
            body = json.loads(raw)
        except ValueError:
            body = {"message": raw[:500]}
        return error.code, body


class ApiProcess:
    def __init__(self, case: str, db: Path, port: int):
        self.case = case
        self.db = db
        self.port = port
        self.base = f"http://127.0.0.1:{port}"
        self.process: subprocess.Popen | None = None
        self.log_handle = None
        self.log_path = RUN_DIR / f"{case}_{port}.log"

    def __enter__(self) -> "ApiProcess":
        unused_port(self.port)
        env = os.environ.copy()
        env.update({
            "ASPNETCORE_ENVIRONMENT": "Development",
            "DOTNET_ENVIRONMENT": "Development",
            "ASPNETCORE_URLS": self.base,
            "ConnectionStrings__MarketDb":
                f"Data Source={self.db.resolve()};Foreign Keys=True;Default Timeout=10;Pooling=True",
            "MarketCollector__Enabled": "false",
            "MarketStorage__EnableDestructiveRetention": "false",
        })
        self.log_handle = self.log_path.open("w", encoding="utf-8")
        self.process = subprocess.Popen(
            ["dotnet", str(DLL)], cwd=API_ROOT, env=env,
            stdout=self.log_handle, stderr=subprocess.STDOUT,
        )
        try:
            end = time.monotonic() + 35
            while time.monotonic() < end:
                if self.process.poll() is not None:
                    raise RuntimeError(f"API exited early. Read {self.log_path}")
                try:
                    status, _ = request(self.base + "/api/dev/archive/status", timeout=2)
                    if status == 200:
                        return self
                except (URLError, OSError, TimeoutError):
                    pass
                time.sleep(0.3)
            raise RuntimeError(f"API not ready at {self.base}. Read {self.log_path}")
        except BaseException:
            self.__exit__(None, None, None)
            raise

    def __exit__(self, exc_type, exc, tb):
        if self.process and self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=8)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)
        if self.log_handle:
            self.log_handle.close()

    def get(self, route: str) -> dict[str, Any]:
        code, body = request(self.base + route)
        ensure(code == 200, f"GET {route}: HTTP {code}, {body}")
        return body

    def post(self, route: str, expected: int | set[int] = 200) -> dict[str, Any]:
        code, body = request(self.base + route, method="POST")
        accepted = {expected} if isinstance(expected, int) else set(expected)
        ensure(code in accepted, f"POST {route}: expected {accepted}, got {code}, {body}")
        return body


def prepare(api: ApiProcess) -> dict[str, Any]:
    status = api.get("/api/dev/archive/status")
    ensure(status.get("totalArchives") == 0, "Fixture ledger not empty")
    result = api.post("/api/dev/archive/prepare", expected={200, 201})
    ensure(result.get("archiveId") and result.get("sourceId"), f"No prepared plan: {result}")
    ensure(api.get("/api/dev/archive/status")["sourceId"] == result["sourceId"],
           "Prepared source mismatch")
    return result


def extract(api: ApiProcess, plan: dict[str, Any]) -> dict[str, Any]:
    result = api.post(f"/api/dev/archive/{plan['archiveId']}/extract")
    ensure(result.get("rowValuesMatch") is True, "Row-value check not reported")
    path = Path(result["candidatePath"])
    ensure(path.is_file() and file_hash(path) == result["sha256"],
           "Candidate missing or SHA-256 mismatch")
    return result


def publish(api: ApiProcess, plan: dict[str, Any]) -> dict[str, Any]:
    result = api.post(f"/api/dev/archive/{plan['archiveId']}/publish")
    verify_published(result)
    return result


def verify_published(result: dict[str, Any]) -> None:
    archive = Path(result["archivePath"])
    manifest = Path(result["manifestPath"])
    ensure(archive.is_file() and manifest.is_file(), "Published DB/manifest missing")
    ensure(file_hash(archive) == result["archiveSha256"], "Published DB hash mismatch")
    ensure(file_hash(manifest) == result["manifestSha256"], "Manifest hash mismatch")
    ensure(archive.stat().st_size == result["archiveSizeBytes"], "Published size mismatch")
    body = json.loads(manifest.read_text(encoding="utf-8"))
    ensure(body["archiveSha256"] == result["archiveSha256"], "Manifest DB receipt mismatch")
    ensure(body["archiveId"] == result["archiveId"], "Manifest identity mismatch")


def verify_uncommitted(api: ApiProcess, archive_id: str, expected_status: int) -> None:
    ledger = api.get(f"/api/dev/archive/{archive_id}")
    ensure(ledger["status"] == expected_status,
           f"Unexpected ledger status: {ledger['status']} (wanted {expected_status})")
    ensure(ledger["transferredAtUtc"] is None and ledger["verifiedAtUtc"] is None
           and ledger["committedAtUtc"] is None and ledger["storageLocation"] is None,
           "Later lifecycle metadata populated")
    ensure(api.get("/api/dev/archive/coverage")["safeToAuthorizeCleanup"] is False,
           "UNSAFE: cleanup authorized")


def corrupt_market_value(path: Path) -> None:
    """Valid SQLite mutation that should fail exact source comparison or SHA-256."""
    ensure(path.exists(), f"Artifact missing: {path}")
    with closing(sqlite3.connect(path)) as db:
        db.execute("PRAGMA journal_mode = DELETE")
        cursor = db.execute("""
            UPDATE MarketPriceAggregates
            SET InstantBuyVolume = InstantBuyVolume + 1
            WHERE Id = (SELECT MIN(Id) FROM MarketPriceAggregates)
        """)
        ensure(cursor.rowcount == 1, "No aggregate row available to corrupt")
        db.commit()
        ensure(db.execute("PRAGMA integrity_check").fetchone()[0] == "ok",
               "Value tamper caused structural corruption; wanted structurally valid mutation")
    ensure(not path.with_name(path.name + "-wal").exists(), "Unexpected SQLite WAL sidecar")


def independently_verify_snapshot_candidate(path: Path, fixture: Path, snapshot_range: dict) -> None:
    start = snapshot_range["startIdExclusive"]
    end = snapshot_range["endIdInclusive"]
    with closing(db_readonly(path)) as db:
        db.execute("ATTACH DATABASE ? AS src", (fixture.resolve().as_uri() + "?mode=ro",))
        actual = db.execute("SELECT COUNT(*) FROM main.MarketPriceSnapshots").fetchone()[0]
        ensure(actual == snapshot_range["recordCount"] and actual > 0,
               f"Snapshot count mismatch: {actual}")
        batches = db.execute("SELECT COUNT(*) FROM main.MarketCollectionBatches").fetchone()[0]
        referenced = db.execute(
            "SELECT COUNT(DISTINCT CollectionBatchId) FROM main.MarketPriceSnapshots").fetchone()[0]
        ensure(batches == referenced and batches > 0, "Collection-batch preservation failed")
        columns = ("Id, MarketItemId, CollectionBatchId, InstantBuyPrice, "
                   "InstantSellPrice, InstantBuyTimeUtc, InstantSellTimeUtc, CollectedAtUtc")
        mismatches = db.execute(f"""
            SELECT COUNT(*) FROM (
                SELECT {columns} FROM src.MarketPriceSnapshots WHERE Id > ? AND Id <= ?
                EXCEPT SELECT {columns} FROM main.MarketPriceSnapshots
            )
        """, (start, end)).fetchone()[0]
        extras = db.execute(f"""
            SELECT COUNT(*) FROM (
                SELECT {columns} FROM main.MarketPriceSnapshots
                EXCEPT SELECT {columns} FROM src.MarketPriceSnapshots WHERE Id > ? AND Id <= ?
            )
        """, (start, end)).fetchone()[0]
        ensure(mismatches == 0 and extras == 0, "Snapshot values differ from frozen fixture")
        ensure(db.execute("PRAGMA integrity_check").fetchone()[0] == "ok", "Candidate failed integrity")
        ensure(not db.execute("PRAGMA foreign_key_check").fetchone(), "Candidate failed foreign-key check")
    print(f"    Snapshots={actual:,}; referenced batches={batches}; exact source rows match")


def concurrent_post(urls: list[str], allowed: set[int] = {200, 409}) -> list[int]:
    barrier = threading.Barrier(len(urls))

    def do_one(url: str) -> int:
        barrier.wait(timeout=5)
        status, _ = request(url, method="POST", timeout=150)
        return status

    with concurrent.futures.ThreadPoolExecutor(max_workers=len(urls)) as executor:
        statuses = list(executor.map(do_one, urls))
    ensure(200 in statuses and all(s in allowed for s in statuses),
           f"Unexpected concurrent responses: {statuses}")
    print(f"    Concurrent HTTP response codes: {statuses}")
    return statuses


def run_case(case: str) -> dict[str, Any]:
    fixture = recreate_fixture(case, age_snapshots=(case == "snapshots"))
    with ApiProcess(case, fixture, PORT1) as api:
        plan = prepare(api)
        archive_id = plan["archiveId"]
        endpoint = f"/api/dev/archive/{archive_id}"
        result: dict[str, Any] = {"archiveId": archive_id, "sourceId": plan["sourceId"]}

        if case == "snapshots":
            snap = next((r for r in plan["ranges"] if r["stream"] == 0), None)
            ensure(snap is not None, "Planner did not produce a snapshot range")
            candidate = extract(api, plan)
            independently_verify_snapshot_candidate(Path(candidate["candidatePath"]), fixture, snap)
            ensure(candidate["snapshotCount"] == snap["recordCount"], "Snapshot extraction count mismatch")
            ensure(candidate["batchCount"] > 0, "Snapshot batch metadata missing")
            published = publish(api, plan)
            verify_uncommitted(api, archive_id, 1)
            result.update({"snapshotCount": candidate["snapshotCount"],
                           "batchCount": candidate["batchCount"],
                           "archiveSha256": published["archiveSha256"]})

        elif case == "candidate_corruption":
            candidate = extract(api, plan)
            candidate_path = Path(candidate["candidatePath"])
            before = file_hash(candidate_path)
            corrupt_market_value(candidate_path)
            ensure(file_hash(candidate_path) != before, "Candidate mutation did not change SHA-256")
            api.post(endpoint + "/publish", expected=409)
            verify_uncommitted(api, archive_id, 0)
            ensure(not (ARTIFACT_ROOT / "archive-exports" / plan["archiveFileName"]).exists(),
                   "Publisher moved corrupted candidate")
            result["corruptionRejected"] = True

        elif case == "published_corruption":
            extract(api, plan)
            published = publish(api, plan)
            archive_path = Path(published["archivePath"])
            before = file_hash(archive_path)
            corrupt_market_value(archive_path)
            ensure(file_hash(archive_path) != before, "Published file mutation did not change SHA-256")
            api.post(endpoint + "/publish", expected=409)
            verify_uncommitted(api, archive_id, 1)
            result["corruptionRejected"] = True

        elif case == "manifest_corruption":
            extract(api, plan)
            published = publish(api, plan)
            manifest_path = Path(published["manifestPath"])
            original_db_hash = file_hash(Path(published["archivePath"]))
            # Valid JSON, wrong canonical bytes/checksum; publisher must fail closed.
            with manifest_path.open("ab") as manifest_file:
                manifest_file.write(b"\n")
            ensure(file_hash(manifest_path) != published["manifestSha256"],
                   "Manifest mutation did not change hash")
            api.post(endpoint + "/publish", expected=409)
            verify_uncommitted(api, archive_id, 1)
            ensure(file_hash(Path(published["archivePath"])) == original_db_hash,
                   "Manifest test unexpectedly changed DB bytes")
            result["manifestCorruptionRejected"] = True

        elif case == "manifest_recovery":
            extract(api, plan)
            final_path = ARTIFACT_ROOT / "archive-exports" / plan["archiveFileName"]
            manifest = Path(str(final_path) + ".manifest.json")
            partial = Path(str(manifest) + ".partial")
            api.post(endpoint + "/publish?simulateBeforeManifestMove=true", expected=409)
            ensure(final_path.exists() and partial.exists() and not manifest.exists(),
                   "Expected published DB and partial manifest without final manifest")
            verify_uncommitted(api, archive_id, 0)
            api.post(endpoint + "/publish?simulateAfterManifestMove=true", expected=409)
            ensure(final_path.exists() and manifest.exists() and not partial.exists(),
                   "Expected published DB and manifest after second simulated crash")
            verify_uncommitted(api, archive_id, 0)
            recovered = publish(api, plan)
            ensure(recovered["recoveredPublishedFile"] is True, "Recovery flag not set")
            verify_uncommitted(api, archive_id, 1)
            retried = publish(api, plan)
            ensure(retried["archiveSha256"] == recovered["archiveSha256"],
                   "Idempotent retry changed export hash")
            result["recoveredManifest"] = True

        elif case == "cross_process":
            with ApiProcess(case + "_second", fixture, PORT2) as second:
                race_extract_urls = [api.base + endpoint + "/extract",
                                     second.base + endpoint + "/extract"]
                extract_codes = concurrent_post(race_extract_urls, allowed={200, 409})
                ensure(extract_codes.count(200) == 1,
                       "Exactly one extraction must succeed; duplicate must reject")
                candidate_path = (ARTIFACT_ROOT / "archive-candidates" /
                                  f"market_{archive_id.replace('-', '')}.candidate.db")
                ensure(candidate_path.exists(), "Concurrent extraction lost the candidate file")
                original_hash = file_hash(candidate_path)
                race_publish_urls = [api.base + endpoint + "/publish",
                                     second.base + endpoint + "/publish"]
                concurrent_post(race_publish_urls, allowed={200, 409})
                result_published = publish(api, plan)
                ensure(result_published["archiveSha256"] == original_hash,
                       "Concurrent publication changed candidate bytes")
                verify_uncommitted(api, archive_id, 1)
                result["crossProcessPublishSafe"] = True

        else:
            raise AssertionError(f"Unknown test case {case}")
        return result


def safe_cleanup_path(path: Path) -> bool:
    absolute = path.resolve()
    for fixture in FIXTURES.values():
        if absolute == fixture.resolve() or absolute in {
            Path(str(fixture.resolve()) + suffix) for suffix in ("-wal", "-shm")
        }:
            return True
    return any(absolute.is_relative_to((ARTIFACT_ROOT / subdir).resolve())
               for subdir in ALLOWED_ARTIFACT_DIRS)


def cleanup() -> None:
    ensure(REPORT.is_file(), f"No inventory: {REPORT}. Nothing deleted.")
    manifest = json.loads(REPORT.read_text(encoding="utf-8"))
    paths = [Path(p) for p in manifest.get("createdFiles", [])]
    # Never delete files not in the saved exact-path inventory.
    ensure(all(safe_cleanup_path(p) for p in paths), "Unsafe path in cleanup inventory. Nothing deleted.")
    print("Cleanup inventory (only files created by this Pass 4 run):")
    for path in paths:
        if path.exists():
            print("  ", path)
    print("Stop any Pass 4 API processes before cleanup. The test harness normally stops them.")
    confirm = input("Type CLEANUP PASS4 to delete ONLY the files above: ")
    ensure(confirm == "CLEANUP PASS4", "Cleanup canceled; nothing deleted")
    for path in paths:
        if path.is_file():
            path.unlink()
    if REPORT.exists():
        REPORT.unlink()
    print("PASS 4 fixtures and generated artifacts cleaned. Previous Pass 3 files untouched.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cleanup", action="store_true",
                        help="Remove ONLY recorded Pass 4-created files after typed confirmation")
    args = parser.parse_args()
    if args.cleanup:
        cleanup()
        return 0
    ensure(sys.platform == "win32", "This harness is intended for Windows development")
    ensure(DLL.is_file(), f"Missing built API: {DLL}. Run dotnet build first.")
    ensure(SOURCE.is_file(), f"Missing source: {SOURCE}")
    ensure(not REPORT.exists(), "Pass 4 report exists; preserve it or clean up first")
    for case, fixture in FIXTURES.items():
        ensure(not fixture.exists(), f"Existing Pass 4 fixture {fixture}. Refusing overwrite.")
    unused_port(PORT1)
    unused_port(PORT2)
    existing = artifact_files()
    RUN_DIR.mkdir(parents=True, exist_ok=True)
    results: dict[str, Any] = {}
    failed = False
    try:
        for case in CASES:
            print(f"\n=== PASS 4: {case.replace('_', ' ').upper()} ===", flush=True)
            results[case] = run_case(case)
            print(f"PASS: {case}", flush=True)
    except BaseException as error:
        failed = True
        print(f"\nFAILED: {error}", file=sys.stderr)
        raise
    finally:
        created = (artifact_files() - existing)
        for fixture in FIXTURES.values():
            for candidate in (fixture, Path(str(fixture) + "-wal"), Path(str(fixture) + "-shm")):
                if candidate.is_file():
                    created.add(candidate.resolve())
        created.add(REPORT.resolve())
        report = {
            "status": "FAILED" if failed else "PASSED",
            "runFinishedUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
            "testDatabaseSource": str(SOURCE),
            "results": results,
            "createdFiles": sorted(str(path) for path in created),
        }
        REPORT.write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(f"\nRun report & cleanup inventory: {REPORT}")
    print("\nPHASE 4 PASS 4: ALL TESTS PASSED. Files retained until explicit --cleanup.")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        sys.exit(1)
