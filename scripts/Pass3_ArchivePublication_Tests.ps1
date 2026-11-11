# Phase 4 Pass 3 — disposable Windows test only.
# Requires archive_planner_test.db on the isolated API at 127.0.0.1:5066,
# and the uncommitted Pass 2 candidate left on disk.
$ErrorActionPreference = 'Stop'
$base = 'http://127.0.0.1:5066'
$archiveId = '9376725e-9e5c-4363-8b24-3bd083c58725'
$expectedSourceId = '758c1211-561b-4f25-8385-742329c24a7e'
$root = 'C:\Users\Neo\Documents\Projects\OSRS_Market_Watch\MarketAPI'

$status = Invoke-RestMethod "$base/api/dev/archive/status"
if ($status.sourceId -ne $expectedSourceId) {
    throw "Wrong API/database! Expected source $expectedSourceId but got $($status.sourceId)."
}
$ledger = Invoke-RestMethod "$base/api/dev/archive/$archiveId"
if ($ledger.status -ne 0) {
    throw "Expected Prepared (0), got $($ledger.status). Stop rather than modifying existing state."
}

$candidatePath = Join-Path $root "artifacts\archive-candidates\market_$($archiveId.Replace('-','')).candidate.db"
$publishedPath = Join-Path $root "artifacts\archive-exports\$($ledger.archiveFileName)"
if (!(Test-Path -LiteralPath $candidatePath)) {
    throw "Pass 2 candidate not found: $candidatePath"
}
if (Test-Path -LiteralPath $publishedPath) {
    throw "Final file already exists; don't simulate a fresh move. Check recovery state manually."
}

Write-Host '\n[1/5] Simulating interruption after the atomic file move...' -ForegroundColor Cyan
$threw = $false
try {
    Invoke-RestMethod -Uri "$base/api/dev/archive/$archiveId/publish?simulateAfterMove=true" -Method Post | Out-Null
}
catch {
    $threw = $true
    Write-Host "Expected simulated interruption: $($_.Exception.Message)" -ForegroundColor Yellow
}
if (-not $threw) { throw 'Expected simulated interruption did not occur.' }

$ledger = Invoke-RestMethod "$base/api/dev/archive/$archiveId"
if ($ledger.status -ne 0) { throw 'Ledger advanced during simulated interruption!' }
if (!(Test-Path -LiteralPath $publishedPath)) { throw 'Final DB was not moved.' }
if (Test-Path -LiteralPath $candidatePath) { throw 'Candidate was not moved.' }
Write-Host 'PASS: final DB exists, candidate moved, ledger still Prepared.' -ForegroundColor Green

Write-Host '\n[2/5] Recovering the prepared-but-published file...' -ForegroundColor Cyan
$published = Invoke-RestMethod -Uri "$base/api/dev/archive/$archiveId/publish" -Method Post
$published | ConvertTo-Json -Depth 10
if (-not $published.recoveredPublishedFile) { throw 'Expected crash-recovery path.' }
if ($published.status -ne 'Exported_NotTransferredOrCommitted') { throw 'Incorrect publication status.' }

Write-Host '\n[3/5] Independently verifying both files...' -ForegroundColor Cyan
if (!(Test-Path -LiteralPath $published.archivePath)) { throw 'Published DB missing.' }
if (!(Test-Path -LiteralPath $published.manifestPath)) { throw 'Sidecar manifest missing.' }
$dbHash = (Get-FileHash -LiteralPath $published.archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifestHash = (Get-FileHash -LiteralPath $published.manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($dbHash -ne $published.archiveSha256) { throw 'Published database hash mismatch.' }
if ($manifestHash -ne $published.manifestSha256) { throw 'Manifest hash mismatch.' }
$manifestJson = Get-Content -LiteralPath $published.manifestPath -Raw | ConvertFrom-Json
if ($manifestJson.archiveId -ne $archiveId -or
    $manifestJson.sourceId -ne $expectedSourceId -or
    $manifestJson.archiveSha256 -ne $dbHash -or
    $manifestJson.archiveSizeBytes -ne (Get-Item -LiteralPath $published.archivePath).Length) {
    throw 'Manifest contents do not match the archive fixture.'
}
if ($manifestJson.ranges.Count -ne 2) { throw 'Unexpected manifest range count.' }

$ledger = Invoke-RestMethod "$base/api/dev/archive/$archiveId"
if ($ledger.status -ne 1) { throw "Expected Exported status 1; got $($ledger.status)." }
if ($ledger.archiveSha256 -ne $dbHash -or $ledger.manifestSha256 -ne $manifestHash) {
    throw 'Ledger hashes do not match published files.'
}
if ($ledger.archiveSizeBytes -ne (Get-Item -LiteralPath $published.archivePath).Length) {
    throw 'Ledger size does not match published DB.'
}
if ($ledger.transferredAtUtc -or $ledger.verifiedAtUtc -or $ledger.committedAtUtc) {
    throw 'Later lifecycle states were set unexpectedly.'
}
Write-Host 'PASS: hashes, size, and Exported-only ledger receipt match.' -ForegroundColor Green

Write-Host '\n[4/5] Idempotent retry...' -ForegroundColor Cyan
$again = Invoke-RestMethod -Uri "$base/api/dev/archive/$archiveId/publish" -Method Post
if ($again.archiveSha256 -ne $dbHash -or $again.manifestSha256 -ne $manifestHash) {
    throw 'Idempotent retry changed an artifact.'
}
Write-Host 'PASS: re-publication verified existing files without overwriting.' -ForegroundColor Green

Write-Host '\n[5/5] Retention protection...' -ForegroundColor Cyan
$coverage = Invoke-RestMethod "$base/api/dev/archive/coverage"
if ($coverage.safeToAuthorizeCleanup -ne $false) {
    throw 'UNSAFE: cleanup authorization became true!'
}
Write-Host 'PASS: archive remains uncommitted; cleanup unauthorized.' -ForegroundColor Green
Write-Host '\nPASS 3 LOCAL PUBLICATION TESTS COMPLETE. Keep fixture and artifacts until next review.' -ForegroundColor Green
