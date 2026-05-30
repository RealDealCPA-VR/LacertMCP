# P14-010 — `./scripts/release.ps1` — produce a distributable zip of the MCP
# server + CLI binaries so the operator doesn't have to hand-copy from
# bin/x64/Release. Critically, this script verifies that the load-bearing
# Schema/FieldAccessMap tree lands in the staging directory — a single-exe
# hand-copy without the schema silently breaks at first field-write.
#
# Net48 has no real `dotnet publish` (it's roughly equivalent to `dotnet build`
# + bin copy) so we just stage the bin/x64/Release tree directly and verify
# the manifest is present.
#
# Usage:
#   ./scripts/release.ps1                 # uses the version from Directory.Build.props
#   ./scripts/release.ps1 -Version 0.1.0  # explicit override

[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDir = $null
)

. (Join-Path $PSScriptRoot '_dotnet.ps1')

$staging = if ($OutputDir) { $OutputDir } else { Join-Path $script:RepoRoot 'dist' }

# ---- 1. Build Release ---------------------------------------------------
Write-Host "==> Building Release x64..." -ForegroundColor Cyan
& $script:Dotnet build $script:Solution -c Release -p:Platform=x64 -nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed (exit $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

# ---- 2. Resolve the version -------------------------------------------
if (-not $Version) {
    # Pull from Directory.Build.props so version stays in one source-of-truth.
    $props = [xml](Get-Content -LiteralPath (Join-Path $script:RepoRoot 'Directory.Build.props') -Raw)
    $node = $props.SelectSingleNode("//Version")
    if ($null -ne $node -and $node.InnerText) {
        $Version = $node.InnerText.Trim()
    } else {
        $Version = '0.0.0'
    }
}
Write-Host "==> Releasing v$Version" -ForegroundColor Cyan

# ---- 3. Stage Server + verify Schema/FieldAccessMap tree --------------
$serverBin = Join-Path $script:RepoRoot 'src/LacertMCP.Server/bin/x64/Release'
$cliBin = Join-Path $script:RepoRoot 'src/LacertMCP.Cli/bin/x64/Release'

foreach ($p in @($serverBin, $cliBin)) {
    if (-not (Test-Path -LiteralPath $p)) {
        Write-Host "Missing build output: $p" -ForegroundColor Red
        exit 1
    }
}

# The load-bearing Schema tree — silent-failure trap if missing.
$schemaManifest = Join-Path $serverBin 'Schema/FieldAccessMap/manifest.json'
$schemaBase = Join-Path $serverBin 'Schema/FieldAccessMap/base'
$schemaOverlays = Join-Path $serverBin 'Schema/FieldAccessMap/overlays'

foreach ($required in @($schemaManifest, $schemaBase, $schemaOverlays)) {
    if (-not (Test-Path -LiteralPath $required)) {
        Write-Host "Missing load-bearing schema file: $required" -ForegroundColor Red
        Write-Host "The MCP will silently fail at first field-write without this." -ForegroundColor Red
        exit 1
    }
}

# Also verify the load-bearing App.config shim is in place.
$appConfig = Join-Path $serverBin 'LacertMCP.Server.exe.config'
if (-not (Test-Path -LiteralPath $appConfig)) {
    Write-Host "Missing LacertMCP.Server.exe.config — the COM mixed-mode runtime shim. Live mode will fail to load." -ForegroundColor Red
    exit 1
}

# ---- 4. Stage to dist/staging/ and zip --------------------------------
if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
$serverStage = Join-Path $staging 'server'
$cliStage = Join-Path $staging 'cli'
New-Item -ItemType Directory -Path $serverStage -Force | Out-Null
New-Item -ItemType Directory -Path $cliStage -Force | Out-Null

Write-Host "==> Staging server -> $serverStage"
Copy-Item -Path (Join-Path $serverBin '*') -Destination $serverStage -Recurse -Force

Write-Host "==> Staging CLI -> $cliStage"
Copy-Item -Path (Join-Path $cliBin '*') -Destination $cliStage -Recurse -Force

# Copy README + LICENSE if present at repo root so the operator gets a
# self-contained drop.
foreach ($f in @('README.md', 'LICENSE', 'CHANGELOG.md')) {
    $src = Join-Path $script:RepoRoot $f
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination $staging -Force
    }
}

# ---- 5. Zip everything ------------------------------------------------
$zipPath = Join-Path $staging "LacertMCP-v$Version.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

# Compress server/ + cli/ + any docs into one zip.
$toZip = @($serverStage, $cliStage)
foreach ($f in @('README.md', 'LICENSE', 'CHANGELOG.md')) {
    $p = Join-Path $staging $f
    if (Test-Path -LiteralPath $p) { $toZip += $p }
}

Compress-Archive -Path $toZip -DestinationPath $zipPath -Force
Write-Host ""
Write-Host "==> Release artifact: $zipPath" -ForegroundColor Green
Write-Host "    Drop this on the operator box, unzip, and point Claude Desktop at server/LacertMCP.Server.exe." -ForegroundColor Green
