# P14-039 — `./scripts/build.ps1` — thin wrapper around `dotnet build` so the
# user-local SDK path isn't duplicated in every doc + every fresh agent's head.
#
# Usage (from anywhere):
#   ./scripts/build.ps1                          # Debug x64 (the docs-canonical build)
#   ./scripts/build.ps1 -Configuration Release   # Release x64
#   ./scripts/build.ps1 -- /p:WarningLevel=0     # forward extra args after `--`

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Forward
)

. (Join-Path $PSScriptRoot '_dotnet.ps1')

$buildArgs = @(
    'build',
    $script:Solution,
    '-c', $Configuration,
    '-p:Platform=x64',
    '-nologo'
)
if ($Forward) { $buildArgs += $Forward }

& $script:Dotnet @buildArgs
exit $LASTEXITCODE
