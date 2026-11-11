<#
.SYNOPSIS
  Installs the Lacerte MCP on the Lacerte PC as an always-on tailnet service
  with its control page (http://<this PC's Tailscale IP>:8767/).

.DESCRIPTION
  Run from an unzipped release folder (the one containing server\ and cli\),
  in PowerShell *as Administrator*, signed in as the Windows user who uses Lacerte:

      powershell -ExecutionPolicy Bypass -File .\install-http.ps1

  What it does (all reversible with -Uninstall):
    1. Copies server\ and cli\ to %LOCALAPPDATA%\LacertMCP.
    2. Adds the URL reservation http://+:8767/ for this user (so the server can
       listen on the Tailscale address without running as admin).
    3. Adds a firewall rule: TCP 8767 inbound from the tailnet (100.64.0.0/10) only.
    4. Adds a hidden Startup-folder launcher that runs the server in live mode
       (LACERTE_LIVE=1, --http) at logon and restarts it if it stops. It must run
       in YOUR session: the Lacerte SDK and Intuit sign-in need the desktop.
    5. Starts it now and checks http://localhost:8767/healthz.

  Options:
    -DataRoot \\fileserver\Lacerte   where the {YY}Tax\{Module}Data folders live (default C:\Lacerte)
    -Port 8767                        listening port
    -Simulation                       run with built-in sample data instead of live Lacerte
    -Uninstall                        remove everything this script added
#>
[CmdletBinding()]
param(
    [string]$DataRoot = '',
    [int]$Port = 8767,
    [switch]$Simulation,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'LacertMCP'
$startup = [Environment]::GetFolderPath('Startup')
$launcher = Join-Path $startup 'LacertMCP.vbs'
$ruleName = 'Lacerte MCP (tailnet only)'
$account = "$env:USERDOMAIN\$env:USERNAME"

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Stop-LacertMcp {
    Get-CimInstance Win32_Process -Filter "Name='wscript.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*LacertMCP.vbs*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Get-Process -Name 'LacertMCP.Server' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Admin)) {
    Write-Host 'Please run this in PowerShell opened with "Run as administrator".' -ForegroundColor Red
    exit 1
}

if ($Uninstall) {
    Write-Host '==> Stopping the Lacerte MCP'
    Stop-LacertMcp
    if (Test-Path $launcher) { Remove-Item $launcher -Force }
    netsh http delete urlacl url=http://+:$Port/ | Out-Null
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
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

Write-Host '==> Stopping any running copy'
Stop-LacertMcp
Start-Sleep -Seconds 1

Write-Host "==> Copying to $installDir"
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
# Keep a writable-field map already built by the dumper (the release ships empty shards).
$mapDir = Join-Path $installDir 'server\Schema\FieldAccessMap\base'
$mapKeep = $null
if ((Test-Path $mapDir) -and (Get-ChildItem $mapDir -Filter *.json | Where-Object { $_.Length -gt 2 })) {
    $mapKeep = Join-Path $env:TEMP ("LacertMCP-fieldmap-" + [guid]::NewGuid().ToString('N'))
    Copy-Item -Path $mapDir -Destination $mapKeep -Recurse -Force
}
foreach ($part in 'server', 'cli', 'dumper') {
    if (-not (Test-Path (Join-Path $here $part))) { continue }
    $dest = Join-Path $installDir $part
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    Copy-Item -Path (Join-Path $here $part) -Destination $dest -Recurse -Force
}

if ($mapKeep) {
    Write-Host '==> Restoring your existing writable-field map'
    Get-ChildItem $mapKeep -Filter *.json | Where-Object { $_.Length -gt 2 } | Copy-Item -Destination $mapDir -Force
    Remove-Item $mapKeep -Recurse -Force
}

Write-Host "==> URL reservation http://+:$Port/ for $account"
netsh http delete urlacl url=http://+:$Port/ 2>$null | Out-Null
$out = netsh http add urlacl url=http://+:$Port/ user="$account"
if ($LASTEXITCODE -ne 0) { Write-Host $out -ForegroundColor Yellow }

Write-Host '==> Firewall: TCP' $Port 'from the tailnet only'
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port `
    -RemoteAddress 100.64.0.0/10 -Action Allow -Profile Any | Out-Null

Write-Host '==> Startup launcher (runs at logon, restarts on exit)'
$serverExe = Join-Path $installDir 'server\LacertMCP.Server.exe'
$envLines = @()
if ($Simulation) { $envLines += 'env("LACERTE_SIMULATION") = "1"' } else { $envLines += 'env("LACERTE_LIVE") = "1"' }
$envLines += "env(""LACERTE_HTTP_PORT"") = ""$Port"""
if ($DataRoot) { $envLines += "env(""LACERTE_DATA_ROOT"") = ""$DataRoot"""; if ($DataRoot.StartsWith('\\')) { $envLines += 'env("LACERTE_ALLOW_UNC") = "1"' } }
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
Set-Content -Path $launcher -Value $vbs -Encoding ASCII

Write-Host '==> Starting'
Start-Process -FilePath 'wscript.exe' -ArgumentList "`"$launcher`""

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
$ip = (& "$env:ProgramFiles\Tailscale\tailscale.exe" ip -4 2>$null | Select-Object -First 1)
$dumper = Join-Path $installDir 'dumper\LacertMCP.FieldAccessDumper.exe'
Write-Host ''
Write-Host 'To allow field writes, build the writable-field map once per tax year you use (then restart by signing out/in, or re-run this installer):'
Write-Host "  & '$dumper' --live --year 2024 --out '$mapDir'"
Write-Host '  (re-running this installer keeps a map you have already built)'
Write-Host ''
Write-Host "Control page:  http://localhost:$Port/   (from other tailnet devices: http://$ip`:$Port/)" -ForegroundColor Green
Write-Host "Agents connect to: http://$ip`:$Port/mcp   (give each device access on the Access tab first)" -ForegroundColor Green
