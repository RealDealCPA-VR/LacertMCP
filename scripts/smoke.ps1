# P14-039 — `./scripts/smoke.ps1` — run the post-build smoke checks listed in
# HANDOFF.md "Verify Before Continuing" without hand-typing each binary path.
#
# Expects scripts/build.ps1 to have already produced bin/x64/Debug output.
# Off-Windows: `doctor` is allowed to exit 2 (probes skipped); on Windows with
# a fresh checkout `doctor` is allowed to exit 1 (no AppID until register).
#
# Usage:
#   ./scripts/smoke.ps1                      # Debug, all probes
#   ./scripts/smoke.ps1 -Configuration Release

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

. (Join-Path $PSScriptRoot '_dotnet.ps1')

$cliExe = Join-Path $script:RepoRoot "src/LacertMCP.Cli/bin/x64/$Configuration/lacerte-mcp.exe"
$serverExe = Join-Path $script:RepoRoot "src/LacertMCP.Server/bin/x64/$Configuration/LacertMCP.Server.exe"
$dumperExe = Join-Path $script:RepoRoot "src/LacertMCP.FieldAccessDumper/bin/x64/$Configuration/LacertMCP.FieldAccessDumper.exe"

$failed = @()

function Invoke-Smoke {
    param(
        [string]$Name,
        [scriptblock]$Block,
        [int[]]$AllowExitCodes = @(0)
    )
    Write-Host "==> $Name"
    try {
        $output = & $Block
        $code = $LASTEXITCODE
        if ($AllowExitCodes -notcontains $code) {
            Write-Host "    FAIL: exit $code (allowed: $($AllowExitCodes -join ','))" -ForegroundColor Red
            if ($output) { Write-Host ($output | Out-String) }
            $script:failed += $Name
        } else {
            Write-Host "    OK (exit $code)" -ForegroundColor Green
        }
    } catch {
        Write-Host "    FAIL: $($_.Exception.Message)" -ForegroundColor Red
        $script:failed += $Name
    }
}

if (-not (Test-Path -LiteralPath $cliExe)) {
    Write-Host "CLI binary missing at $cliExe — run ./scripts/build.ps1 first." -ForegroundColor Red
    exit 1
}

# `status` is a pure read of in-memory state — must exit 0 on every platform.
Invoke-Smoke -Name 'lacerte-mcp status' -Block { & $cliExe status }

# `doctor` is permitted to exit 1 (fail; e.g. no AppID on a fresh checkout)
# or 2 (skipped; off-Windows). Exit 0 means a full live Windows install. All
# three are legitimate "smoke didn't crash the binary" outcomes.
Invoke-Smoke -Name 'lacerte-mcp doctor' -Block { & $cliExe doctor } -AllowExitCodes @(0, 1, 2)

# Dumper smoke — in sim it prints "no fields to probe in sim; run on Windows
# for live introspection." and exits 0.
if (Test-Path -LiteralPath $dumperExe) {
    Invoke-Smoke -Name 'LacertMCP.FieldAccessDumper' -Block { & $dumperExe }
} else {
    Write-Host "==> LacertMCP.FieldAccessDumper" -ForegroundColor Yellow
    Write-Host "    SKIP: binary missing at $dumperExe"
}

# Server-startup probe — launch the MCP server, give it 1500ms to bind stdio,
# then kill it. If the process exits before the timeout, it crashed at startup.
Invoke-Smoke -Name 'LacertMCP.Server stdio startup' -Block {
    if (-not (Test-Path -LiteralPath $serverExe)) {
        $global:LASTEXITCODE = 127
        return "server binary missing at $serverExe"
    }
    $proc = Start-Process -FilePath $serverExe -PassThru -WindowStyle Hidden -RedirectStandardOutput ([IO.Path]::GetTempFileName()) -RedirectStandardError ([IO.Path]::GetTempFileName())
    Start-Sleep -Milliseconds 1500
    if ($proc.HasExited) {
        $global:LASTEXITCODE = 1
        return "server exited before 1500ms with code $($proc.ExitCode)"
    }
    try { $proc.Kill() } catch { }
    $proc.WaitForExit(5000) | Out-Null
    $global:LASTEXITCODE = 0
}

Write-Host ""
if ($failed.Count -gt 0) {
    Write-Host "Smoke FAILED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "Smoke OK." -ForegroundColor Green
exit 0
