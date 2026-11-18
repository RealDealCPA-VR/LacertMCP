<#
.SYNOPSIS
  Installs the Lacerte MCP on the Lacerte PC as an always-on tailnet service
  with its control page (http://<this PC's Tailscale IP>:8767/).

.DESCRIPTION
  Run from an unzipped release folder (the one containing server\ and cli\),
  in PowerShell *as Administrator*, signed in as the Windows user who uses Lacerte:

      powershell -ExecutionPolicy Bypass -File .\install-http.ps1

  What it does (all reversible with -Uninstall):
    1. Copies server\, cli\ and dumper\ to %LOCALAPPDATA%\LacertMCP, merging in the
       writable-field map entries already built on this PC (same rule as
       `lacerte-mcp update`).
    2. Adds the URL reservation http://+:8767/ for this user (so the server can
       listen on the Tailscale address without running as admin).
    3. Adds a firewall rule: TCP 8767 inbound from the tailnet (100.64.0.0/10) only.
    4. Adds a hidden Startup-folder launcher that runs the server in live mode
       (LACERTE_LIVE=1, --http) at logon and restarts it if it stops. It must run
       in YOUR session: the Lacerte SDK and Intuit sign-in need the desktop.
    5. Starts it now (without elevation, the way Windows starts it at logon) and
       checks http://localhost:8767/healthz.

  Options (remembered in %LOCALAPPDATA%\LacertMCP\install.json; a re-run without
  them keeps the previous values):
    -DataRoot \\fileserver\Lacerte   where the {YY}Tax\{Module}Data folders live (default C:\Lacerte).
                                      The server only opens clients under this folder.
    -Port 8767                        listening port
    -Simulation                       run with built-in sample data instead of live Lacerte
                                      (-Simulation:$false switches a simulation install back to live)
    -LiveDetailWrites                 let agents write detail rows (income, deduction, K-1 amounts) to
                                      real returns. Turn on only after the REGRESSION_CHECKLIST
                                      "FP-2 live items" pass on this PC (-LiveDetailWrites:$false turns it off)
    -RequireApproval                  agents can't write to returns directly; data reaches a return only
                                      through a proposal a person approved (control page Proposals tab or
                                      `lacerte-mcp proposal approve`). -RequireApproval:$false turns it off
    -Uninstall                        remove everything this script added
#>
[CmdletBinding()]
param(
    [string]$DataRoot = '',
    [int]$Port = 8767,
    [switch]$Simulation,
    [switch]$LiveDetailWrites,
    [switch]$RequireApproval,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'LacertMCP'
$settingsPath = Join-Path $installDir 'install.json'
$startup = [Environment]::GetFolderPath('Startup')
$launcher = Join-Path $startup 'LacertMCP.vbs'
$ruleName = 'Lacerte MCP (tailnet only)'
$taskName = 'LacertMCP auto-update'
$account = "$env:USERDOMAIN\$env:USERNAME"

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Runs a console tool without $ErrorActionPreference='Stop' turning its stderr into
# a terminating error; returns @{ Code; Output }.
function Invoke-Native([string]$exe, [string[]]$arguments) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $exe @arguments 2>&1 | Out-String
        return @{ Code = $LASTEXITCODE; Output = $out.Trim() }
    } catch {
        return @{ Code = -1; Output = $_.Exception.Message }
    } finally {
        $ErrorActionPreference = $prev
    }
}

function Get-LauncherProcesses {
    # The full launcher path, not just the file name (another LacertMCP.vbs elsewhere is not ours).
    Get-CimInstance Win32_Process -Filter "Name='wscript.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($launcher, [StringComparison]::OrdinalIgnoreCase) -ge 0 }
}

function Stop-LacertMcp {
    $ids = @()
    foreach ($w in @(Get-LauncherProcesses)) {
        Stop-Process -Id $w.ProcessId -Force -ErrorAction SilentlyContinue
        $ids += $w.ProcessId
    }
    $serverDir = (Join-Path $installDir 'server') + '\'
    foreach ($p in @(Get-Process -Name 'LacertMCP.Server' -ErrorAction SilentlyContinue)) {
        if ($p.Path -and $p.Path.StartsWith($serverDir, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            $ids += $p.Id
        }
    }
    # Killed processes release their file locks only once they have exited.
    if ($ids.Count -gt 0) { Wait-Process -Id $ids -Timeout 15 -ErrorAction SilentlyContinue }
}

# Remove-Item, retried: a just-stopped process (or antivirus) can hold a file for a moment.
function Remove-WithRetry([string]$path) {
    for ($i = 1; $i -le 10; $i++) {
        try {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
            return
        } catch {
            if ($i -eq 10) { throw }
            Start-Sleep -Seconds 1
        }
    }
}

if (-not (Test-Admin)) {
    Write-Host 'Please run this in PowerShell opened with "Run as administrator".' -ForegroundColor Red
    exit 1
}

# Settings from the previous install: a re-run without -DataRoot / -Port / -Simulation keeps them.
$saved = $null
if (Test-Path $settingsPath) {
    try { $saved = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json } catch { $saved = $null }
}
# No install.json yet (installed by an older installer): take the settings from
# the existing launcher's env lines instead, so a re-run doesn't silently switch
# a D:\ or \\server data root back to C:\Lacerte.
if (-not $saved -and (Test-Path $launcher)) {
    try {
        $vbsText = Get-Content -Raw -LiteralPath $launcher
        $fromVbs = @{}
        foreach ($m in [regex]::Matches($vbsText, 'env\("(LACERTE_[A-Z_]+)"\)\s*=\s*"([^"]*)"')) {
            $fromVbs[$m.Groups[1].Value] = $m.Groups[2].Value
        }
        $saved = [pscustomobject]@{
            dataRoot   = $fromVbs['LACERTE_DATA_ROOT']
            port       = if ($fromVbs['LACERTE_HTTP_PORT']) { [int]$fromVbs['LACERTE_HTTP_PORT'] } else { $null }
            simulation = ($fromVbs['LACERTE_SIMULATION'] -eq '1')
            liveDetailWrites = ($fromVbs['LACERTE_LIVE_DETAIL_WRITES'] -eq '1')
            requireApproval = ($fromVbs['LACERTE_REQUIRE_APPROVAL'] -eq '1')
        }
    } catch { $saved = $null }
}
$oldPort = if ($saved -and $saved.port) { [int]$saved.port } else { $null }
if (-not $PSBoundParameters.ContainsKey('Port') -and $oldPort) { $Port = $oldPort }
if (-not $PSBoundParameters.ContainsKey('DataRoot') -and $saved -and $saved.dataRoot) { $DataRoot = [string]$saved.dataRoot }
$useSimulation = if ($PSBoundParameters.ContainsKey('Simulation')) { [bool]$Simulation } elseif ($saved) { [bool]$saved.simulation } else { $false }
$useRequireApproval = if ($PSBoundParameters.ContainsKey('RequireApproval')) { [bool]$RequireApproval } elseif ($saved -and $saved.requireApproval) { [bool]$saved.requireApproval } else { $false }
$useDetailWrites = if ($PSBoundParameters.ContainsKey('LiveDetailWrites')) { [bool]$LiveDetailWrites } elseif ($saved -and $saved.liveDetailWrites) { [bool]$saved.liveDetailWrites } else { $false }
if (-not $DataRoot) { $DataRoot = 'C:\Lacerte' }
if (-not $Uninstall -and -not (Test-Path -LiteralPath $DataRoot)) {
    Write-Host "WARNING: the data root $DataRoot doesn't exist on this PC, so agents won't be able to open any returns." -ForegroundColor Yellow
    Write-Host "         Re-run with -DataRoot <the folder that holds the {YY}Tax folders>, e.g. -DataRoot \\fileserver\Lacerte" -ForegroundColor Yellow
}

if ($Uninstall) {
    Write-Host '==> Stopping the Lacerte MCP'
    Stop-LacertMcp
    if (Test-Path $launcher) { Remove-Item $launcher -Force }
    $r = Invoke-Native 'schtasks.exe' @('/Delete', '/F', '/TN', $taskName)
    if ($r.Code -eq 0) { Write-Host "==> Removed the `"$taskName`" scheduled task" }
    foreach ($p in (@($Port, $oldPort) | Where-Object { $_ } | Select-Object -Unique)) {
        Invoke-Native 'netsh.exe' @('http', 'delete', 'urlacl', "url=http://+:$p/") | Out-Null
    }
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    $mapDir = Join-Path $installDir 'server\Schema\FieldAccessMap\base'
    if (Get-ChildItem $mapDir -Filter *.json -ErrorAction SilentlyContinue) {
        $mapBackup = Join-Path $env:APPDATA ("lacerte-mcp\fieldmap-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        New-Item -ItemType Directory -Force -Path $mapBackup | Out-Null
        Copy-Item -Path (Join-Path $mapDir '*.json') -Destination $mapBackup -Force
        Write-Host "==> Saved the writable-field map to $mapBackup"
    }
    Remove-WithRetry $installDir
    Write-Host 'Removed. Your device-access list and activity history are kept in %APPDATA%\lacerte-mcp.' -ForegroundColor Green
    exit 0
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcServer = Join-Path $here 'server'
$srcCli = Join-Path $here 'cli'
if (-not (Test-Path (Join-Path $srcServer 'LacertMCP.Server.exe'))) {
    Write-Host "Couldn't find server\LacertMCP.Server.exe next to this script. Run it from the unzipped release folder." -ForegroundColor Red
    exit 1
}
# Run from the install itself, "copy server\ to the install" would first delete the
# very files it is about to copy.
$hereFull = [IO.Path]::GetFullPath($here).TrimEnd('\')
$installFull = [IO.Path]::GetFullPath($installDir).TrimEnd('\')
if ([string]::Equals($hereFull, $installFull, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "This is the installed copy ($installDir); the installer must run from the unzipped release folder." -ForegroundColor Red
    Write-Host "Download LacertMCP-v<version>.zip from GitHub, unzip it elsewhere and run its install-http.ps1 -- or just run:" -ForegroundColor Red
    Write-Host "  & '$installDir\cli\lacerte-mcp.exe' update" -ForegroundColor Red
    exit 1
}

Write-Host '==> Stopping any running copy'
Stop-LacertMcp

Write-Host "==> Copying to $installDir"
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
# Set this PC's writable-field map aside; it is merged into the release's below.
$mapDir = Join-Path $installDir 'server\Schema\FieldAccessMap\base'
$mapKeep = $null
if ((Test-Path $mapDir) -and (Get-ChildItem $mapDir -Filter *.json)) {
    $mapKeep = Join-Path $env:TEMP ("LacertMCP-fieldmap-" + [guid]::NewGuid().ToString('N'))
    Copy-Item -Path $mapDir -Destination $mapKeep -Recurse -Force
}
# Same for field-dictionary entries recorded on this PC with `lacerte-mcp map-diff record`.
$dictDir = Join-Path $installDir 'server\Schema\FieldDictionary'
$dictKeep = $null
if ((Test-Path $dictDir) -and (Get-ChildItem $dictDir -Filter *.json | Where-Object { $_.Length -gt 0 })) {
    $dictKeep = Join-Path $env:TEMP ("LacertMCP-dictionary-" + [guid]::NewGuid().ToString('N'))
    Copy-Item -Path $dictDir -Destination $dictKeep -Recurse -Force
}
foreach ($part in 'server', 'cli', 'dumper') {
    if (-not (Test-Path (Join-Path $here $part))) { continue }
    $dest = Join-Path $installDir $part
    Remove-WithRetry $dest
    Copy-Item -Path (Join-Path $here $part) -Destination $dest -Recurse -Force
}
# A fresh copy supersedes any update that was interrupted mid-swap; leaving its
# marker would make the next `lacerte-mcp update` restore that old backup over it.
$pendingMarker = Join-Path $installDir '.update-pending'
if (Test-Path -LiteralPath $pendingMarker) { Remove-WithRetry $pendingMarker }

$cliExe = Join-Path $installDir 'cli\lacerte-mcp.exe'
if ($mapKeep) {
    Write-Host '==> Merging your existing writable-field map into the release''s'
    # One rule, in one place: the same per-entry merge `lacerte-mcp update` uses
    # (curated fixes from the release arrive; entries built on this PC are kept).
    $r = Invoke-Native $cliExe @('field-access', 'merge', '--from', $mapKeep, '--into', $mapDir)
    if ($r.Output) { $r.Output -split "`r?`n" | ForEach-Object { Write-Host "    $_" } }
    if ($r.Code -eq 0) {
        Remove-Item $mapKeep -Recurse -Force
    } else {
        $saveTo = Join-Path $installDir ("backups\fieldmap-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        New-Item -ItemType Directory -Force -Path (Split-Path $saveTo) | Out-Null
        Move-Item -LiteralPath $mapKeep -Destination $saveTo
        Write-Host "The field-map merge failed; the release's map is in use and your previous one is saved in $saveTo" -ForegroundColor Yellow
    }
}
if ($dictKeep) {
    $r = Invoke-Native $cliExe @('field-access', 'merge-dictionary', '--from', $dictKeep, '--into', $dictDir)
    if ($r.Output) { $r.Output -split "`r?`n" | ForEach-Object { Write-Host "    $_" } }
    if ($r.Code -eq 0) {
        Remove-Item $dictKeep -Recurse -Force
    } else {
        $saveTo = Join-Path $installDir ("backups\fielddictionary-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        New-Item -ItemType Directory -Force -Path (Split-Path $saveTo) | Out-Null
        Move-Item -LiteralPath $dictKeep -Destination $saveTo
        Write-Host "The field-dictionary merge failed; the release's dictionary is in use and your recorded entries are saved in $saveTo" -ForegroundColor Yellow
    }
}

if ($oldPort -and $oldPort -ne $Port) {
    Write-Host "==> Removing the old URL reservation http://+:$oldPort/"
    Invoke-Native 'netsh.exe' @('http', 'delete', 'urlacl', "url=http://+:$oldPort/") | Out-Null
}
Write-Host "==> URL reservation http://+:$Port/ for $account"
Invoke-Native 'netsh.exe' @('http', 'delete', 'urlacl', "url=http://+:$Port/") | Out-Null
$r = Invoke-Native 'netsh.exe' @('http', 'add', 'urlacl', "url=http://+:$Port/", "user=$account")
if ($r.Code -ne 0) {
    # Without it the (non-elevated) server can't listen on the tailnet address.
    Write-Host "Couldn't add the URL reservation http://+:$Port/ for $account (netsh exit $($r.Code)):" -ForegroundColor Red
    Write-Host $r.Output -ForegroundColor Red
    exit 1
}

Write-Host '==> Firewall: TCP' $Port 'from the tailnet only'
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port `
    -RemoteAddress 100.64.0.0/10 -Action Allow -Profile Any | Out-Null

Write-Host '==> Startup launcher (runs at logon, restarts on exit)'
$serverExe = Join-Path $installDir 'server\LacertMCP.Server.exe'
$envLines = @()
if ($useSimulation) { $envLines += 'env("LACERTE_SIMULATION") = "1"' } else { $envLines += 'env("LACERTE_LIVE") = "1"' }
$envLines += "env(""LACERTE_HTTP_PORT"") = ""$Port"""
# Always confine the server to the data root, so a remote agent can't point it at any local path.
$envLines += "env(""LACERTE_DATA_ROOT"") = ""$DataRoot"""
if ($DataRoot.StartsWith('\\')) { $envLines += 'env("LACERTE_ALLOW_UNC") = "1"' }
if ($useDetailWrites) { $envLines += 'env("LACERTE_LIVE_DETAIL_WRITES") = "1"' }
if ($useRequireApproval) { $envLines += 'env("LACERTE_REQUIRE_APPROVAL") = "1"' }
$vbs = @"
' Lacerte MCP launcher, added by install-http.ps1. Delete this file (or run
' install-http.ps1 -Uninstall) to stop it starting at logon.
Set sh = CreateObject("WScript.Shell")
Set env = sh.Environment("PROCESS")
$($envLines -join "`r`n")
Do
  sh.Run """$serverExe"" --http", 0, True
  WScript.Sleep 15000
Loop
"@
# UTF-16 LE with BOM: Windows Script Host reads it, and a non-ASCII profile path
# (an accented user name, say) survives; ASCII would turn those letters into "?".
Set-Content -Path $launcher -Value $vbs -Encoding Unicode

[ordered]@{ dataRoot = $DataRoot; port = $Port; simulation = [bool]$useSimulation; liveDetailWrites = [bool]$useDetailWrites; requireApproval = [bool]$useRequireApproval } |
    ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8

Write-Host '==> Starting'
# Through Explorer, not Start-Process wscript.exe: from this elevated console that
# would run the launcher (and the server) elevated, and a later non-elevated
# `lacerte-mcp update` could then neither inspect nor stop it. Explorer hands the
# .vbs to the signed-in user's normal shell, exactly as the Startup folder does at logon.
Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList "`"$launcher`""
$started = $false
for ($i = 0; $i -lt 15 -and -not $started; $i++) {
    Start-Sleep -Seconds 1
    $started = [bool](Get-LauncherProcesses)
}
if (-not $started) {
    Write-Host "Explorer didn't start $launcher (is .vbs still opened by Windows Script Host on this PC?)." -ForegroundColor Red
    Write-Host 'Sign out of Windows and back in to start it from the Startup folder, or run this in a normal (not admin) PowerShell:' -ForegroundColor Red
    Write-Host "  wscript.exe `"$launcher`"" -ForegroundColor Red
    exit 1
}

$ok = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 1
    try {
        $h = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/healthz" -TimeoutSec 5 -ErrorAction Stop
        $ok = $true; $body = $h.Content; break
    } catch {
        if ($_.Exception.Response) { $ok = $true; $body = (New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())).ReadToEnd(); break }
    }
}
if (-not $ok) {
    Write-Host "The server didn't answer on http://localhost:$Port/. Try running $serverExe --http in a console to see why." -ForegroundColor Red
    exit 1
}
$checks = ($body | ConvertFrom-Json).checks
Write-Host ''
foreach ($c in $checks) {
    $color = @{ ok = 'Green'; warn = 'Yellow'; bad = 'Red' }[$c.status]
    Write-Host ("  [{0,-4}] {1}: {2}" -f $c.status, $c.name, $c.summary) -ForegroundColor $color
    if ($c.status -ne 'ok' -and $c.fix) { Write-Host ("         -> {0}" -f $c.fix) }
}
$ip = $null
$tailscale = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
if (Test-Path $tailscale) {
    $r = Invoke-Native $tailscale @('ip', '-4')
    if ($r.Code -eq 0 -and $r.Output) { $ip = ($r.Output -split "`r?`n" | Select-Object -First 1).Trim() }
}
if (-not $ip) { $ip = "<this PC's Tailscale IP>" }
Write-Host ''
Write-Host "Data root: $DataRoot   (the server only opens Lacerte clients under it; re-run with -DataRoot to change it)"
if ($useDetailWrites) {
    Write-Host 'Detail-row writes: ON (only for Series/Code in the verified field dictionary; re-run with -LiveDetailWrites:$false to turn off)'
} else {
    Write-Host 'Detail-row writes: off (re-run with -LiveDetailWrites after the REGRESSION_CHECKLIST "FP-2 live items" pass)'
}
if ($useRequireApproval) {
    Write-Host 'Approval required: ON (agents write to returns only through approved proposals)'
} else {
    Write-Host 'Approval required: off (re-run with -RequireApproval so agents can only write through approved proposals)'
}
Write-Host ''
Write-Host 'Field writes: only a few built-in fields are writable (none for Corporate, S-Corp or Fiduciary).'
Write-Host 'To allow another field, confirm in Lacerte that it is an input field, then:'
Write-Host "  & '$cliExe' field-access set --module I --table 1 --field <field> --access ReadWrite"
Write-Host '  (the automatic field-map builder is not available yet; re-running this installer or'
Write-Host '   `lacerte-mcp update` keeps the fields you add)'
Write-Host ''
Write-Host 'To update later (no admin needed; keeps your field map):'
Write-Host "  & '$cliExe' update                    # now"
Write-Host "  & '$cliExe' update --schedule daily   # automatically, every night at 03:00"
Write-Host ''
Write-Host "Control page:  http://localhost:$Port/   (from other tailnet devices: http://$ip`:$Port/)" -ForegroundColor Green
Write-Host "Agents connect to: http://$ip`:$Port/mcp   (give each device access on the Access tab first)" -ForegroundColor Green
