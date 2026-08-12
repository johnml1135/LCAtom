<#
  .SYNOPSIS
  The test gate: everything build.ps1 checks, then the test suite.

  .DESCRIPTION
  Use this instead of a bare `dotnet test`. It runs build.ps1 first so that a green test run always
  implies clean comments and a clean compile -- one command whose success means the whole thing is
  good, rather than three that have to be remembered in order.

  ON THE EXTERNAL FIXTURE. Much of this suite reads a FieldWorks SIBLING checkout that is not vendored
  here: `TestLangProj` for a live LibLCM cache, and `DistFiles` for `ContextHelp.xml`. Without it those
  tests fail rather than skip, which is deliberate -- a suite that quietly shrinks when a fixture goes
  missing reports success for work it never did.

  Every one of them carries `[Trait("Fixture", "FieldWorks")]`, which is what makes them selectable.
  The trait is named for the checkout rather than for `TestLangProj`, because one of them wants a file
  from `DistFiles` instead, and a filter that reads as a lie is how the next one gets missed.

  That is why -Filter exists. CI has no sibling checkout, so it runs the subset needing none and says
  so in its own step name. A filtered run is not a full run and nothing here pretends otherwise: the
  count printed at the end is the count that actually executed.

  .PARAMETER Configuration
  MSBuild configuration. Must match what build.ps1 produced, since the suite runs with --no-build.

  .PARAMETER Filter
  Passed through to `dotnet test --filter`. Empty means the whole suite.

  .PARAMETER SkipBuild
  Reuse the existing binaries and skip the build gate. For re-running a suite you just built.

  .EXAMPLE
  ./test.ps1

  .EXAMPLE
  ./test.ps1 -Filter 'Fixture!=FieldWorks'
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Debug',
    [string] $Filter = '',
    [switch] $SkipBuild
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

if ($SkipBuild) {
    Write-Step 'build gate -- SKIPPED (-SkipBuild)'
}
else {
    & pwsh -NoProfile -File (Join-Path $repoRoot 'build.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

$arguments = @($solution, '--configuration', $Configuration, '--nologo', '--no-build')
if ($Filter) {
    Write-Step "dotnet test (filtered: $Filter)"
    $arguments += @('--filter', $Filter)
}
else {
    Write-Step 'dotnet test (full suite)'
}

& dotnet test @arguments
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host 'Tests failed.' -ForegroundColor Red
    exit 1
}

Write-Host ''
if ($Filter) {
    Write-Host "Tests OK -- FILTERED run, not the full suite: $Filter" -ForegroundColor Yellow
}
else {
    Write-Host 'Tests OK: full suite.' -ForegroundColor Green
}
exit 0
