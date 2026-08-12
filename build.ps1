<#
  .SYNOPSIS
  The build gate: comment hygiene first, then compile. Stops at the first step that fails.

  .DESCRIPTION
  Use this instead of a bare `dotnet build`. The difference is the hygiene gate, and the reason it is
  wired in here rather than left to whoever remembers is that a rule nothing enforces is a rule that
  decays to a suggestion. Comment rot is silent by construction: nothing fails, nothing looks wrong,
  and a stale comment keeps being believed because it looks maintained.

  Both steps are hard failures. The hygiene gate runs FIRST and on its own, because it is seconds of
  work against minutes of compilation, and because a violation is a fact about the source that does
  not depend on whether the code compiles.

  The hygiene script is invoked as a child process so that its own `exit` code arrives here intact
  rather than terminating this script's scope.

  .PARAMETER Configuration
  MSBuild configuration. Defaults to Debug, matching a bare `dotnet build`.

  .PARAMETER SkipHygiene
  Compile without the comment gate. For bisecting a build break only -- CI never passes this, so
  anything it lets through fails there instead.

  .EXAMPLE
  ./build.ps1

  .EXAMPLE
  ./build.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Debug',
    [switch] $SkipHygiene
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

if ($SkipHygiene) {
    Write-Step 'comment hygiene -- SKIPPED (-SkipHygiene)'
}
else {
    Write-Step 'comment hygiene'
    & pwsh -NoProfile -File (Join-Path $repoRoot 'tools/comment-hygiene.ps1')
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host 'Comment hygiene failed. Run tools/comment-hygiene.ps1 -List to see every violation.' -ForegroundColor Red
        exit 1
    }
}

Write-Step "dotnet build ($Configuration)"
& dotnet build $solution --configuration $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host 'Build failed.' -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host 'Build OK: comments clean, solution compiles.' -ForegroundColor Green
exit 0
