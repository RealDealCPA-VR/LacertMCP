# P14-039 — `./scripts/build.ps1` — thin wrapper around `dotnet build` so the
# user-local SDK path isn't duplicated in every doc + every fresh agent's head.
#
# Usage (from anywhere):
#   ./scripts/build.ps1                          # Debug x64 (the docs-canonical build)
#   ./scripts/build.ps1 -Configuration Release   # Release x64
#   ./scripts/build.ps1 -- /p:WarningLevel=0     # forward extra args after `--`
#   pwsh -File scripts/build.ps1 /p:WarningLevel=0  # from cmd/CI: omit `--` (pwsh -File
#                                                # parses a bare `--` as an empty param name)

# PositionalBinding=$false: without it, the first arg forwarded after `--`
# (e.g. `/p:...`) binds positionally to -Configuration and fails ValidateSet.
# Named params only; everything unbound lands in $Forward.
[CmdletBinding(PositionalBinding = $false)]
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
