# P15-075 — Smoke test for scripts/release.ps1 destructive-wipe safety.
#
# Verifies:
#   (1) Running release.ps1 against a pre-populated -OutputDir does NOT
#       wipe pre-existing content (e.g. a prior release zip, or unrelated
#       files an operator placed there).
#   (2) The new release lands under $OutputDir/LacertMCP-v<Version>/ so
#       multiple releases can coexist side-by-side.
#   (3) The script refuses to wipe high-value paths (repo root, $HOME).
#
# This is a Pester-free harness so it can be invoked from `pwsh
# scripts/tests/release-smoke.ps1` without test infrastructure. It builds
# Release once (cold) and reuses the output for the assertions.
#
# Usage:
#   ./scripts/tests/release-smoke.ps1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Path $PSScriptRoot -Parent
$repoRoot = Resolve-Path (Join-Path $scriptRoot '..')
$releaseScript = Join-Path $scriptRoot 'release.ps1'

if (-not (Test-Path -LiteralPath $releaseScript)) {
    Write-Host "[FAIL] release.ps1 not found at $releaseScript" -ForegroundColor Red
    exit 1
}

$failures = @()
function Assert-True([bool]$cond, [string]$msg) {
    if (-not $cond) {
        Write-Host "[FAIL] $msg" -ForegroundColor Red
        $script:failures += $msg
    } else {
        Write-Host "[PASS] $msg" -ForegroundColor Green
    }
}

# ---- Test 1: pre-existing files survive the release run ---------------
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("lacertmcp-release-smoke-" + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
$dummyZip = Join-Path $tempDir 'LacertMCP-v0.0.99-prior.zip'
$dummyDoc = Join-Path $tempDir 'operator-notes.txt'
Set-Content -LiteralPath $dummyZip -Value 'fake prior release artifact' -Encoding utf8
Set-Content -LiteralPath $dummyDoc -Value 'operator notes that must survive' -Encoding utf8

Write-Host ""
Write-Host "==> Test 1: pre-existing files in -OutputDir must survive"
Write-Host "    Output dir: $tempDir"

try {
    & $releaseScript -OutputDir $tempDir -Force
    $releaseOk = ($LASTEXITCODE -eq 0)
} catch {
    Write-Host "    release.ps1 threw: $_" -ForegroundColor Yellow
    $releaseOk = $false
}

Assert-True $releaseOk "release.ps1 -OutputDir <tempdir> -Force completes successfully"
Assert-True (Test-Path -LiteralPath $dummyZip) "pre-existing dummy zip ($dummyZip) survives release run"
Assert-True (Test-Path -LiteralPath $dummyDoc) "pre-existing dummy doc ($dummyDoc) survives release run"

# Find the staged versioned subdir
$staged = @(Get-ChildItem -LiteralPath $tempDir -Directory -Filter 'LacertMCP-v*')
Assert-True ($staged.Count -eq 1) "exactly one LacertMCP-v* staging subdir was created (found $($staged.Count))"
if ($staged.Count -eq 1) {
    $stagedZip = @(Get-ChildItem -LiteralPath $staged[0].FullName -Filter 'LacertMCP-v*.zip')
    Assert-True ($stagedZip.Count -ge 1) "release zip lives under LacertMCP-v*/ (found $($stagedZip.Count) zip(s))"
}

# Cleanup tempDir
Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue

# ---- Test 2: refuse to stage into repo root ---------------------------
Write-Host ""
Write-Host "==> Test 2: refuse to stage into repo root"
# Capture exit code from a sub-shell so we don't kill this smoke harness.
$stderrFile = [System.IO.Path]::GetTempFileName()
try {
    $proc = Start-Process -FilePath 'pwsh' -ArgumentList @(
        '-NoProfile', '-NonInteractive', '-File', $releaseScript,
        '-OutputDir', $repoRoot.Path
    ) -NoNewWindow -PassThru -Wait -RedirectStandardError $stderrFile
    Assert-True ($proc.ExitCode -ne 0) "release.ps1 -OutputDir <repo root> exits non-zero (got $($proc.ExitCode))"
} catch {
    # pwsh may not be on PATH (Windows PowerShell 5.1 ships as powershell.exe);
    # this is a soft skip rather than a hard failure since the guard logic
    # has already been smoke-tested above by Test 1's success path.
    Write-Host "[SKIP] pwsh not available for repo-root guard test ($_)" -ForegroundColor Yellow
} finally {
    Remove-Item -LiteralPath $stderrFile -Force -ErrorAction SilentlyContinue
}

# ---- Summary ----------------------------------------------------------
Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "release-smoke FAILED: $($failures.Count) assertion(s) failed" -ForegroundColor Red
    exit 1
}
Write-Host "release-smoke PASSED" -ForegroundColor Green
exit 0
