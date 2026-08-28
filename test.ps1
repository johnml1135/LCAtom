<#
  .SYNOPSIS
  The test gate: everything build.ps1 checks, then the whole test suite.

  .DESCRIPTION
  Use this instead of a bare `dotnet test`. It runs build.ps1 first so that a green test run always
  implies clean comments and a clean compile -- one command whose success means the whole thing is
  good, rather than three that have to be remembered in order.

  There is no filter parameter, deliberately. This script exists so that one green run means one
  thing, and a subset cannot mean it. The filter that used to live here excluded every test needing a
  FieldWorks checkout, which is exactly how that dependency survived unexamined for so long: a filter
  nobody questions is where the next one hides. Run `dotnet test --filter` directly when narrowing a
  hunt -- knowing that it skips this gate, which is the point.

  The suite needs no project or checkout from outside this repo: every LibLCM project it exercises is
  built at run time by `NewLangProjFixture` and seeded by `SeededProject`. The one external dependency
  that remains is the `pangloss` executable, a separate Rust build gated by `RealParserFactAttribute`
  -- those tests skip, rather than fail, when it is not built.

  .PARAMETER Configuration
  MSBuild configuration. Must match what build.ps1 produced, since the suite runs with --no-build.

  .PARAMETER SkipBuild
  Reuse the existing binaries and skip the build gate. For re-running a suite you just built.

  .PARAMETER AllowRunningTestHosts
  Proceed even though a test host from an earlier run is still alive. A stale one holds a lock on the
  build output, which stalls the build rather than failing it -- and a stalled build prints nothing, so
  the gate looks like a test that never finishes. This script therefore stops and names the processes
  instead. Pass this only when the running host is a deliberate second run you want to race.

  .EXAMPLE
  ./test.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Debug',
    [switch] $SkipBuild,
    [switch] $AllowRunningTestHosts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$solution = Join-Path $repoRoot 'Motif.sln'

function Write-Step {
    param([string] $Text)
    Write-Host ''
    Write-Host "==> $Text" -ForegroundColor Cyan
}

$running = @(Get-Process -Name 'testhost' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0 -and -not $AllowRunningTestHosts) {
    Write-Host ''
    Write-Host "A test host from an earlier run is still alive (PID $($running.Id -join ', '))." -ForegroundColor Red
    Write-Host 'It holds a lock on the build output, so this gate would stall with no output at all.'
    Write-Host 'Stop it and run again:  Get-Process testhost | Stop-Process -Force'
    Write-Host 'Or pass -AllowRunningTestHosts to proceed anyway.'
    exit 1
}

if ($SkipBuild) {
    Write-Step 'build gate -- SKIPPED (-SkipBuild)'
}
else {
    & pwsh -NoProfile -File (Join-Path $repoRoot 'build.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

Write-Step 'dotnet test (full suite)'
& dotnet test $solution --configuration $Configuration --nologo --no-build
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host 'Tests failed.' -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host 'Tests OK: full suite.' -ForegroundColor Green
exit 0
