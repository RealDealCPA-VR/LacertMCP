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
# P15-075 — Destructive-wipe safety: the script no longer wipes the operator's
# -OutputDir wholesale. Instead, it isolates staging under a versioned
# subdirectory (LacertMCP-v<Version>/) and only wipes THAT subdirectory.
# Pre-flight guards refuse to wipe high-value paths (repo root, $HOME, drive
# roots). Pass -Force to overwrite a non-empty versioned subdir without
# prompting.
#
# Usage:
#   ./scripts/release.ps1                 # uses the version from Directory.Build.props
#   ./scripts/release.ps1 -Version 0.1.0  # explicit override
#   ./scripts/release.ps1 -OutputDir C:/builds/lacerte -Force

[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDir = $null,
    [switch]$Force
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

# ---- 4. Pre-flight: refuse to wipe high-value paths -------------------
# Resolve the absolute staging dir (it may not exist yet, so resolve the
# parent + leafname rather than Resolve-Path which throws on nonexistent
# paths). We compare resolved absolute paths case-insensitively so any
# alias/short-name/quirky-separator path lands at the same canonical form.
function Resolve-AbsolutePath([string]$path) {
    $parent = Split-Path -Path $path -Parent
    $leaf = Split-Path -Path $path -Leaf
    if ([string]::IsNullOrEmpty($parent)) {
        return [System.IO.Path]::GetFullPath($path)
    }
    if (-not (Test-Path -LiteralPath $parent)) {
        # Walk up until we find an existing ancestor so we can resolve through it.
        return [System.IO.Path]::GetFullPath($path)
    }
    $resolvedParent = (Resolve-Path -LiteralPath $parent).ProviderPath
    if ([string]::IsNullOrEmpty($leaf)) {
        return $resolvedParent
    }
    return [System.IO.Path]::Combine($resolvedParent, $leaf)
}

function Test-IsAncestorOrEqual([string]$candidate, [string]$child) {
    # True if $candidate equals $child or is an ancestor of $child.
    $c = $candidate.TrimEnd('\', '/')
    $d = $child.TrimEnd('\', '/')
    if ([string]::Equals($c, $d, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }
    $cWithSep = $c + [System.IO.Path]::DirectorySeparatorChar
    return $d.StartsWith($cWithSep, [System.StringComparison]::OrdinalIgnoreCase)
}

$resolvedStaging = Resolve-AbsolutePath $staging
$resolvedRepoRoot = (Resolve-Path -LiteralPath $script:RepoRoot).ProviderPath
$userProfile = $env:USERPROFILE
$resolvedHome = if ($userProfile) { (Resolve-Path -LiteralPath $userProfile).ProviderPath } else { $null }

# Refuse equal-to or ancestor-of repo root.
if (Test-IsAncestorOrEqual $resolvedStaging $resolvedRepoRoot) {
    Write-Host "Refusing to stage release into '$resolvedStaging' — equal to or ancestor of repo root '$resolvedRepoRoot'." -ForegroundColor Red
    Write-Host "Pick an -OutputDir that is OUTSIDE the repo tree (or use the default ./dist)." -ForegroundColor Red
    exit 1
}

# Refuse $HOME itself (subdirs of $HOME are fine).
if ($null -ne $resolvedHome) {
    $rh = $resolvedHome.TrimEnd('\', '/')
    $rs = $resolvedStaging.TrimEnd('\', '/')
    if ([string]::Equals($rh, $rs, [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "Refusing to stage release directly into `$HOME ('$resolvedStaging')." -ForegroundColor Red
        Write-Host "Pick a subdirectory of `$HOME or another path entirely." -ForegroundColor Red
        exit 1
    }
}

# Refuse drive roots (e.g. C:\, D:\). On Windows these are 3-char paths
# ending in '\' once we ensure a trailing separator.
$probe = $resolvedStaging
if (-not $probe.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
    $probe = $probe + [System.IO.Path]::DirectorySeparatorChar
}
if ($probe.Length -le 3) {
    Write-Host "Refusing to stage release into a drive root ('$resolvedStaging')." -ForegroundColor Red
    exit 1
}

# ---- 5. Stage to staging/LacertMCP-v<Version>/ and zip ----------------
# Versioned subdirectory isolates wipes: prior version zips and unrelated
# content under the operator's -OutputDir are preserved.
$stageRoot = Join-Path $staging "LacertMCP-v$Version"

if (Test-Path -LiteralPath $stageRoot) {
    # Determine emptiness — we always overwrite an empty dir; non-empty dirs
    # need -Force.
    $existingChildren = @(Get-ChildItem -LiteralPath $stageRoot -Force -ErrorAction SilentlyContinue)
    if ($existingChildren.Count -gt 0 -and -not $Force) {
        Write-Host "Refusing to overwrite existing non-empty staging subdir '$stageRoot'." -ForegroundColor Red
        Write-Host "Pass -Force to overwrite, or pick a different -OutputDir / -Version." -ForegroundColor Red
        exit 1
    }
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}

# Ensure the parent staging dir exists without wiping anything in it.
if (-not (Test-Path -LiteralPath $staging)) {
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
}

$serverStage = Join-Path $stageRoot 'server'
$cliStage = Join-Path $stageRoot 'cli'
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
        Copy-Item -LiteralPath $src -Destination $stageRoot -Force
    }
}

# ---- 6. Zip everything ------------------------------------------------
$zipPath = Join-Path $stageRoot "LacertMCP-v$Version.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

# Compress server/ + cli/ + any docs into one zip.
$toZip = @($serverStage, $cliStage)
foreach ($f in @('README.md', 'LICENSE', 'CHANGELOG.md')) {
    $p = Join-Path $stageRoot $f
    if (Test-Path -LiteralPath $p) { $toZip += $p }
}

Compress-Archive -Path $toZip -DestinationPath $zipPath -Force
Write-Host ""
Write-Host "==> Release artifact: $zipPath" -ForegroundColor Green
Write-Host "    Drop this on the operator box, unzip, and point Claude Desktop at server/LacertMCP.Server.exe." -ForegroundColor Green
