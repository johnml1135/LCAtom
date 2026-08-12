<#
  .SYNOPSIS
  The test gate: everything build.ps1 checks, then the test suite.

  .DESCRIPTION
  Use this instead of a bare `dotnet test`. It runs build.ps1 first so that a green test run always
  implies clean comments and a clean compile -- one command whose success means the whole thing is
  good, rather than three that have to be remembered in order.

  The suite needs no project or checkout from outside this repo: every LibLCM project it exercises is
  built at run time by `NewLangProjFixture` and seeded by `SeededProject`. The one external dependency
  that remains is the `pangloss` executable, a separate Rust build gated by `RealParserFactAttribute`
  -- those tests skip, rather than fail, when it is not built.

  .PARAMETER Configuration
  MSBuild configuration. Must match what build.ps1 produced, since the suite runs with --no-build.

  .PARAMETER Filter
  Passed through to `dotnet test --filter`. Empty means the whole suite.

  .PARAMETER SkipBuild
  Reuse the existing binaries and skip the build gate. For re-running a suite you just built.

  .EXAMPLE
  ./test.ps1
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
