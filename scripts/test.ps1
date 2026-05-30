# P14-039 — `./scripts/test.ps1` — thin wrapper around `dotnet test` so the
# user-local SDK path isn't duplicated in every doc + every fresh agent's head.
#
# Usage:
#   ./scripts/test.ps1                              # --no-build (assumes scripts/build.ps1 ran)
#   ./scripts/test.ps1 -Rebuild                     # build first, then test
#   ./scripts/test.ps1 -Configuration Release       # Release
#   ./scripts/test.ps1 -- --filter Category=Live    # forward extra args after `--`

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$Rebuild,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Forward
)

. (Join-Path $PSScriptRoot '_dotnet.ps1')

$testProject = Join-Path $script:RepoRoot 'src/LacertMCP.Tests/LacertMCP.Tests.csproj'

$testArgs = @(
    'test',
    $testProject,
    '-c', $Configuration,
    '-p:Platform=x64',
    '-nologo'
)
if (-not $Rebuild) { $testArgs += '--no-build' }
if ($Forward) { $testArgs += $Forward }

& $script:Dotnet @testArgs
exit $LASTEXITCODE
