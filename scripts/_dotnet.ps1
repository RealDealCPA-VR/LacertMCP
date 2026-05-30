# P14-039 — Shared dotnet-resolver for the scripts/ wrappers.
#
# The system-wide dotnet on the operator box (and many fresh Windows boxes) is
# runtime-only — it can't drive `dotnet build`. The user-local SDK install at
# $env:USERPROFILE\.dotnet is the canonical SDK and is the path baked into
# README.md, HANDOFF.md, and REGRESSION_CHECKLIST.md. This file is the single
# source of truth for that path so the docs + scripts can never drift.
#
# Dot-source this from scripts/build.ps1, scripts/test.ps1, scripts/smoke.ps1,
# scripts/release.ps1 — sets $script:Dotnet to the absolute exe path and throws
# a clear error if no SDK (just a runtime) is found.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-DotnetSdk {
    # Prefer the user-local SDK install — this is what HANDOFF.md / README.md /
    # REGRESSION_CHECKLIST.md document and what every previous agent has hit
    # ENOENT on when they reached for the system dotnet.
    $userLocal = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $userLocal) {
        return $userLocal
    }

    # Fallback — try PATH. If `dotnet --list-sdks` returns at least one line we
    # can use it; if not, the install is runtime-only and `dotnet build` will
    # fail with a confusing "command not found" later. Better to fail loudly now.
    $cmd = Get-Command -Name dotnet -ErrorAction SilentlyContinue
    if ($null -ne $cmd) {
        $sdks = & $cmd.Source --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) {
            return $cmd.Source
        }
        throw "Found dotnet at '$($cmd.Source)' but it appears to be runtime-only (no SDKs installed). Install the .NET 8 SDK to $env:USERPROFILE\.dotnet or system-wide."
    }

    throw "No dotnet SDK found. Install the .NET 8 SDK to '$userLocal' (preferred) or system-wide."
}

$script:Dotnet = Resolve-DotnetSdk
$script:RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$script:Solution = Join-Path $script:RepoRoot 'LacertMCP.sln'
