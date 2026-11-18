namespace LacertMCP.Server.Web
{
    /// <summary>
    /// The one-line workstation setup the Lacerte PC serves at GET /setup.ps1:
    ///
    ///   irm http://&lt;lacerte-pc-tailnet-ip&gt;:8767/setup.ps1 | iex
    ///
    /// Run on a workstation (Windows, Tailscale connected). It registers this
    /// Lacerte MCP with every AI app it finds on that workstation:
    /// <list type="bullet">
    ///   <item>Claude Code: native HTTP (<c>claude mcp add --transport http</c>).</item>
    ///   <item>Claude Desktop (classic and Microsoft Store installs) and the
    ///   Codex CLI: through <c>bridge.ps1</c>, a stdio-to-HTTP bridge served at
    ///   GET /bridge.ps1. It needs nothing but the Windows PowerShell 5.1 that
    ///   ships with Windows (no Node, no admin).</item>
    /// </list>
    /// It then asks the server what access this device has (GET /access-check)
    /// and says exactly what to do next. Re-running it updates; setting
    /// <c>$env:LACERTE_MCP_UNINSTALL='1'</c> first removes everything it added.
    ///
    /// Both scripts must stay pure ASCII: Windows PowerShell 5.1 reads BOM-less
    /// text as ANSI.
    /// </summary>
    internal static class WorkstationSetup
    {
        public const string InstallDir = "LacerteMcpClient";
        public const string ServerName = "lacerte";

        /// <summary>The setup script, pointed at <paramref name="baseUrl"/> (e.g. http://100.x.y.z:8767).</summary>
        public static string SetupScript(string baseUrl)
            => SetupTemplate
                .Replace("__BASE__", baseUrl)
                .Replace("__DIR__", InstallDir)
                .Replace("__NAME__", ServerName)
                .Replace("\r\n", "\n").Replace("\n", "\r\n");

        public static string BridgeScript()
            => BridgeTemplate.Replace("\r\n", "\n").Replace("\n", "\r\n");

        private const string SetupTemplate = """
# Lacerte MCP workstation setup, served by the Lacerte PC at __BASE__
# Set up or update:  irm __BASE__/setup.ps1 | iex
# Remove:            $env:LACERTE_MCP_UNINSTALL='1'; irm __BASE__/setup.ps1 | iex
& {
  $ErrorActionPreference = 'Stop'
  $ProgressPreference = 'SilentlyContinue'
  $Base = '__BASE__'
  $Url = "$Base/mcp"
  $Name = '__NAME__'
  $Root = Join-Path $env:LOCALAPPDATA '__DIR__'
  $Bridge = Join-Path $Root 'bridge.ps1'
  $Utf8 = New-Object System.Text.UTF8Encoding($false)
  $Uninstall = ($env:LACERTE_MCP_UNINSTALL -eq '1')
  if ($Uninstall) { Remove-Item Env:\LACERTE_MCP_UNINSTALL -ErrorAction SilentlyContinue }
  $BridgeArgs = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $Bridge, '-Url', $Url)
  $Done = New-Object System.Collections.Generic.List[string]

  function Get-Web([string]$u) {
    $wc = New-Object System.Net.WebClient
    $wc.Proxy = $null
    $wc.Encoding = $Utf8
    try { return $wc.DownloadString($u) } finally { $wc.Dispose() }
  }

  function ConvertTo-Plain($o) {
    if ($null -eq $o) { return $null }
    if ($o -is [System.Management.Automation.PSCustomObject]) {
      $h = [ordered]@{}
      foreach ($p in $o.PSObject.Properties) { $h[$p.Name] = ConvertTo-Plain $p.Value }
      return $h
    }
    if ($o -is [object[]]) { return ,@($o | ForEach-Object { ConvertTo-Plain $_ }) }
    return $o
  }

  function Save-Text([string]$path, [string]$text) {
    if (Test-Path $path) { Copy-Item $path ($path + '.bak') -Force }
    [System.IO.File]::WriteAllText($path, $text, $Utf8)
  }

  # Claude Desktop keeps its settings in a small JSON file. Edit only the "__NAME__"
  # entry; if the file can't be read, leave it alone rather than risk the rest of it.
  function Set-DesktopConfig([string]$path) {
    $dir = Split-Path $path -Parent
    $root = [ordered]@{}
    if (Test-Path $path) {
      $raw = [System.IO.File]::ReadAllText($path)
      if ($raw.Trim().Length -gt 0) {
        try { $root = ConvertTo-Plain ($raw | ConvertFrom-Json) } catch {
          Write-Warning "  Skipped $path : it isn't valid JSON. Fix or delete it, then run this again."
          return
        }
      }
    } elseif ($Uninstall) { return }
    if ($null -eq $root -or -not ($root -is [System.Collections.IDictionary])) { $root = [ordered]@{} }
    $servers = $root['mcpServers']
    if ($null -eq $servers -or -not ($servers -is [System.Collections.IDictionary])) { $servers = [ordered]@{} }
    if ($Uninstall) {
      if (-not $servers.Contains($Name)) { return }
      $servers.Remove($Name)
    } else {
      $servers[$Name] = [ordered]@{ command = 'powershell.exe'; args = $BridgeArgs }
    }
    $root['mcpServers'] = $servers
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Save-Text $path ($root | ConvertTo-Json -Depth 32)
    $Done.Add("Claude Desktop ($path)")
  }

  # Codex CLI: replace any [mcp_servers.__NAME__] table (and its sub-tables) in config.toml.
  function Set-CodexConfig([string]$path) {
    $lines = @()
    if (Test-Path $path) { $lines = @([System.IO.File]::ReadAllLines($path)) } elseif ($Uninstall) { return }
    $kept = New-Object System.Collections.Generic.List[string]
    $skip = $false; $found = $false
    foreach ($l in $lines) {
      if ($l -match '^\s*\[') {
        $skip = ($l -match ('^\s*\[\s*mcp_servers\.("?)' + $Name + '\1\s*(\.[^\]]*)?\]'))
        if ($skip) { $found = $true }
      }
      if (-not $skip) { $kept.Add($l) }
    }
    if ($Uninstall -and -not $found) { return }
    while ($kept.Count -gt 0 -and $kept[$kept.Count - 1].Trim() -eq '') { $kept.RemoveAt($kept.Count - 1) }
    if (-not $Uninstall) {
      $q = ($BridgeArgs | ForEach-Object { '"' + ($_ -replace '\\', '\\' -replace '"', '\"') + '"' }) -join ', '
      if ($kept.Count -gt 0) { $kept.Add('') }
      $kept.Add("[mcp_servers.$Name]")
      $kept.Add('command = "powershell.exe"')
      $kept.Add("args = [$q]")
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent) | Out-Null
    Save-Text $path (($kept -join "`r`n") + "`r`n")
    $Done.Add("Codex CLI ($path)")
  }

  # Claude Code speaks HTTP itself, so it gets the address directly.
  # Resolved to the real program (claude.exe / claude.cmd), never an alias, function
  # or .ps1, so $LASTEXITCODE below is that program's own exit code.
  function Set-ClaudeCode {
    $claude = Get-Command claude -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $claude) { return }
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
      & $claude.Source mcp remove $Name -s user 2>&1 | Out-Null
      if ($Uninstall) { $Done.Add('Claude Code'); return }
      $global:LASTEXITCODE = 1
      $out = & $claude.Source mcp add --transport http --scope user $Name $Url 2>&1
      if ($LASTEXITCODE -eq 0) { $Done.Add('Claude Code') }
      else { Write-Warning ("  Claude Code: " + (($out | Out-String).Trim())) }
    } finally { $ErrorActionPreference = $old }
  }

  # Claude Desktop: only where it is installed (classic: its settings folder or its
  # install folder exists; Microsoft Store: its package exists).
  $desktop = New-Object System.Collections.Generic.List[string]
  if ((Test-Path (Join-Path $env:APPDATA 'Claude')) -or (Test-Path (Join-Path $env:LOCALAPPDATA 'AnthropicClaude'))) {
    $desktop.Add((Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'))
  }
  $pkgs = Join-Path $env:LOCALAPPDATA 'Packages'
  if (Test-Path $pkgs) {
    Get-ChildItem $pkgs -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue |
      ForEach-Object { $desktop.Add((Join-Path $_.FullName 'LocalCache\Roaming\Claude\claude_desktop_config.json')) }
  }
  $codexDir = Join-Path $env:USERPROFILE '.codex'
  $wantCodex = (Test-Path $codexDir) -or [bool](Get-Command codex -ErrorAction SilentlyContinue)

  if ($Uninstall) {
    Write-Host "Removing the Lacerte MCP from this PC's AI apps"
    foreach ($p in $desktop) { Set-DesktopConfig $p }
    if ($wantCodex) { Set-CodexConfig (Join-Path $codexDir 'config.toml') }
    Set-ClaudeCode
    Remove-Item $Root -Recurse -Force -ErrorAction SilentlyContinue
    # Tell the Lacerte PC, so it stops showing this device as waiting for access (best effort).
    try {
      $q = [System.Net.HttpWebRequest]::Create("$Base/access-check?uninstall=1")
      $q.Proxy = $null; $q.Timeout = 5000
      $q.GetResponse().Close()
    } catch { }
    foreach ($d in $Done) { Write-Host "  removed from $d" }
    Write-Host 'Done. Restart Claude Desktop if it is open. Access granted on the Lacerte PC stays until you remove it there.'
    return
  }

  Write-Host "Setting up the Lacerte MCP on $env:COMPUTERNAME (server: $Base)"

  # 1. Can we reach the Lacerte PC, and what is this PC allowed to do?
  $access = $null
  try { $access = Get-Web "$Base/access-check" | ConvertFrom-Json } catch {
    throw "Can't reach the Lacerte MCP at $Base. Check that the Lacerte PC is on and that Tailscale is connected on both PCs. ($($_.Exception.Message))"
  }

  # 2. The bridge Claude Desktop and Codex use (they only start local programs).
  New-Item -ItemType Directory -Force -Path $Root | Out-Null
  [System.IO.File]::WriteAllText($Bridge, (Get-Web "$Base/bridge.ps1"), [System.Text.Encoding]::ASCII)

  # 3. Register with each AI app found on this PC.
  foreach ($p in $desktop) { Set-DesktopConfig $p }
  if ($wantCodex) { Set-CodexConfig (Join-Path $codexDir 'config.toml') }
  Set-ClaudeCode

  Write-Host ''
  foreach ($d in $Done) { Write-Host "  Added to $d" -ForegroundColor Green }
  if ($Done.Count -eq 0) { Write-Warning '  No Claude Desktop, Claude Code or Codex was found on this PC. Install one, then run this line again.' }
  Write-Host "  Server address: $Url"
  Write-Host ''
  switch ($access.level) {
    'write' { Write-Host "Ready. $($access.device) can read and change Lacerte returns." -ForegroundColor Green }
    'read'  { Write-Host "Ready. $($access.device) can read Lacerte returns (changes are refused)." -ForegroundColor Green }
    default {
      Write-Host "One more step: $($access.device) doesn't have access yet." -ForegroundColor Yellow
      Write-Host "  Ask whoever runs the Lacerte PC to open the Lacerte MCP control page, Access tab,"
      Write-Host "  and choose Read only or Read & write for $($access.device). It's marked 'Ran setup' there."
    }
  }
  Write-Host 'Restart Claude Desktop (quit it from the tray icon) so it picks up the change.'
  Write-Host "Run the same line again any time to update. To remove: `$env:LACERTE_MCP_UNINSTALL='1'; irm $Base/setup.ps1 | iex"
}
""";

        private const string BridgeTemplate = """
# Lacerte MCP bridge. Claude Desktop and Codex start local programs and talk to
# them over stdin/stdout; the Lacerte MCP runs on the Lacerte PC and speaks
# HTTP over the tailnet. This relays one to the other.
# Installed by setup.ps1 from the Lacerte PC; re-run setup to update it.
param([Parameter(Mandatory = $true)][string]$Url)
$ErrorActionPreference = 'Stop'
$Utf8 = New-Object System.Text.UTF8Encoding($false)
$In = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $Utf8)
$Out = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $Utf8)
$Out.AutoFlush = $true
$script:Session = $null
$script:InitLine = $null
$script:Emitted = 0

function Write-Log([string]$text) { [Console]::Error.WriteLine('[lacerte-bridge] ' + $text) }

function Send-Error($id, [string]$message) {
  if ($null -eq $id) { return }
  $o = @{ jsonrpc = '2.0'; id = $id; error = @{ code = -32000; message = $message } }
  $Out.WriteLine(($o | ConvertTo-Json -Compress -Depth 5))
}

function Get-ErrorText([string]$text, [int]$status) {
  try {
    $j = $text | ConvertFrom-Json
    if ($j.error.message) { return [string]$j.error.message }
    if ($j.error) { return [string]$j.error }
  } catch { }
  if ($text) { return "HTTP $status : $text" }
  return "HTTP $status"
}

# POSTs one JSON-RPC message. Server replies (JSON or a server-sent event
# stream) are written to stdout as they arrive when $emit is set.
function Invoke-Post([string]$body, [bool]$emit) {
  $req = [System.Net.HttpWebRequest]::Create($Url)
  $req.Method = 'POST'
  $req.ContentType = 'application/json'
  $req.Accept = 'application/json, text/event-stream'
  $req.Proxy = $null
  # A fresh connection per message: a pooled one can go stale when the Lacerte PC
  # restarts or sleeps, and the next request would hang on it.
  $req.KeepAlive = $false
  $req.Timeout = 3600000
  $req.ReadWriteTimeout = 3600000
  if ($script:Session) { $req.Headers['Mcp-Session-Id'] = $script:Session }
  $bytes = $Utf8.GetBytes($body)
  $req.ContentLength = $bytes.Length
  $s = $req.GetRequestStream()
  try { $s.Write($bytes, 0, $bytes.Length) } finally { $s.Close() }
  try { $resp = $req.GetResponse() } catch {
    $ex = $_.Exception
    while ($ex -and -not ($ex -is [System.Net.WebException])) { $ex = $ex.InnerException }
    if (-not $ex -or -not $ex.Response) { throw }
    $r = $ex.Response
    $code = [int]$r.StatusCode   # read before Close(): afterwards it can come back as 0
    $text = ''
    try { $text = (New-Object System.IO.StreamReader($r.GetResponseStream(), $Utf8)).ReadToEnd() } catch { } finally { $r.Close() }
    return @{ Status = $code; Text = $text }
  }
  try {
    $sid = $resp.Headers['Mcp-Session-Id']
    if ($sid) { $script:Session = $sid }
    $status = [int]$resp.StatusCode
    $reader = New-Object System.IO.StreamReader($resp.GetResponseStream(), $Utf8)
    if ($resp.ContentType -like 'text/event-stream*') {
      $data = New-Object System.Collections.Generic.List[string]
      while ($null -ne ($l = $reader.ReadLine())) {
        if ($l -eq '') {
          if ($data.Count -gt 0) { if ($emit) { $Out.WriteLine(($data -join ' ')); $script:Emitted++ }; $data.Clear() }
        } elseif ($l.StartsWith('data:')) {
          $data.Add($l.Substring(5).TrimStart())
        }
      }
      if ($data.Count -gt 0 -and $emit) { $Out.WriteLine(($data -join ' ')); $script:Emitted++ }
    } else {
      $text = $reader.ReadToEnd()
      if ($emit -and $text.Trim().Length -gt 0) { $Out.WriteLine(($text -replace '\r?\n', ' ')); $script:Emitted++ }
    }
    return @{ Status = $status; Text = '' }
  } finally { $resp.Close() }
}

while ($null -ne ($line = $In.ReadLine())) {
  if ($line.Trim().Length -eq 0) { continue }
  $id = $null; $method = $null
  try { $msg = $line | ConvertFrom-Json; $id = $msg.id; $method = $msg.method } catch { }
  if ($method -eq 'initialize') { $script:InitLine = $line; $script:Session = $null }
  $script:Emitted = 0
  try {
    $r = Invoke-Post $line $true
    if ($r.Status -eq 404 -and $script:Session -and $method -ne 'initialize' -and $script:InitLine) {
      # The Lacerte MCP restarted and forgot this connection: reconnect quietly and retry.
      Write-Log 'Connection expired (the Lacerte MCP restarted or closed it); reconnecting. The agent must call lacerte_set_context again before reading or writing returns.'
      $script:Session = $null
      $ri = Invoke-Post $script:InitLine $false
      if ($ri.Status -lt 300) {
        [void](Invoke-Post '{"jsonrpc":"2.0","method":"notifications/initialized"}' $false)
        $r = Invoke-Post $line $true
      }
    }
    if ($r.Status -ge 400 -or $r.Status -eq 0) { Send-Error $id (Get-ErrorText $r.Text $r.Status) }
    elseif ($script:Emitted -eq 0 -and $null -ne $id -and $null -ne $method) {
      # Never leave the app waiting on a request the server didn't answer.
      Send-Error $id 'The Lacerte MCP returned no answer. Try again.'
    }
  } catch {
    $e = $_.Exception
    while ($e.InnerException) { $e = $e.InnerException }
    Write-Log $e.Message
    Send-Error $id ("Can't reach the Lacerte MCP at $Url ($($e.Message)). Check that the Lacerte PC is on and that Tailscale is connected on both PCs.")
  }
}

if ($script:Session) {
  try {
    $d = [System.Net.HttpWebRequest]::Create($Url)
    $d.Method = 'DELETE'; $d.Proxy = $null; $d.Timeout = 3000
    $d.Headers['Mcp-Session-Id'] = $script:Session
    $d.GetResponse().Close()
  } catch { }
}
""";
    }
}
