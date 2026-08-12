<#
  .SYNOPSIS
  Packs a local libpalaso checkout so motif can build and test against it instead of the pinned
  NuGet release. See the local libpalaso override section in AGENTS.md.

  .DESCRIPTION
  Adapted from FieldWorks' Build/Manage-LocalLibraries.ps1 and cut down to what motif actually
  depends on: SIL.WritingSystems and its SIL.Core dependency. The rest of the libpalaso solution
  (WinForms etc.) is never packed here.

  One difference from the FieldWorks script matters: that one registers LOCAL_NUGET_REPO as a
  NuGet source via `dotnet nuget add source` (user-level config). motif's nuget.config contains
  `<clear />`, which wipes sources merged in from the user-level config, so a source registered
  that way would be silently ignored. This script registers nothing; Directory.Build.props adds
  LOCAL_NUGET_REPO to RestoreAdditionalProjectSources at restore time instead, which is not
  affected by `<clear />`.

  Packs SIL.Core and SIL.WritingSystems in Debug with symbols, letting GitVersion produce the
  checkout's own branch-derived version rather than forcing one. Detects that version from the
  produced .nupkg filenames, writes it into SilVersions.props, and clears any stale extraction of
  the same version from the global NuGet cache so a re-pack after further local edits is not
  served the previous attempt's bits.

  Run `git checkout SilVersions.props` afterward to revert, and unset LOCAL_NUGET_REPO to fully
  disable the override.

  .PARAMETER PalasoPath
  Path to a local libpalaso checkout. Overrides the LIBPALASO_PATH environment variable.

  .EXAMPLE
  $env:LOCAL_NUGET_REPO = 'C:\localnugetpackages'
  ./tools/Manage-LocalLibraries.ps1 -PalasoPath C:\Users\johnm\Documents\repos\libpalaso
#>
[CmdletBinding()]
param(
    [string] $PalasoPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$versionsPropsPath = Join-Path $repoRoot 'SilVersions.props'

$sourceDir = $PalasoPath
if (-not $sourceDir) { $sourceDir = $env:LIBPALASO_PATH }
if (-not $sourceDir) {
    throw 'Pass -PalasoPath or set LIBPALASO_PATH to a libpalaso checkout.'
}
if (-not (Test-Path $sourceDir)) {
    throw "libpalaso checkout not found: $sourceDir"
}

$localRepo = $env:LOCAL_NUGET_REPO
if (-not $localRepo) {
    throw 'LOCAL_NUGET_REPO is not set. Set it to a folder path, e.g. C:\localnugetpackages.'
}
if (-not (Test-Path $localRepo)) {
    Write-Host "Creating local NuGet repo folder: $localRepo" -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path $localRepo | Out-Null
}

# Only these two -- the rest of the libpalaso solution is not built or referenced here.
$projects = @('SIL.Core/SIL.Core.csproj', 'SIL.WritingSystems/SIL.WritingSystems.csproj')

foreach ($proj in $projects) {
    $projPath = Join-Path $sourceDir $proj
    if (-not (Test-Path $projPath)) {
        throw "Expected project not found at $projPath -- is $sourceDir a libpalaso checkout?"
    }
    Write-Host "Packing $proj..." -ForegroundColor Cyan
    & dotnet pack $projPath -c Debug -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg --output $localRepo
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $proj." }
}

# Newest per id, not "produced just now": an unchanged checkout leaves an existing nupkg untouched.
$newPackages = @(
    Get-ChildItem -Path $localRepo -Filter '*.nupkg' -File |
        Where-Object { $_.BaseName -match '^(SIL\.Core|SIL\.WritingSystems)\.' } |
        Group-Object { $_.BaseName -replace '^(SIL\.Core|SIL\.WritingSystems)\..*$', '$1' } |
        ForEach-Object { $_.Group | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
)
if ($newPackages.Count -eq 0) {
    throw "No SIL.Core/SIL.WritingSystems .nupkg produced in $localRepo."
}

# Split on '.'; the first segment starting with a digit and everything after it is the version.
function Get-PackageVersion {
    param([string] $FileName)
    $base = $FileName -replace '\.nupkg$', ''
    $segments = $base -split '\.'
    for ($i = 0; $i -lt $segments.Count; $i++) {
        if ($segments[$i] -match '^\d') { return ($segments[$i..($segments.Count - 1)] -join '.') }
    }
    return $null
}

$versions = @($newPackages | ForEach-Object { Get-PackageVersion $_.Name } | Sort-Object -Unique)
if ($versions.Count -ne 1) {
    throw "Expected SIL.Core and SIL.WritingSystems to share one version, got: $($versions -join ', ')"
}
$version = $versions[0]
Write-Host "Packed version: $version" -ForegroundColor Green

$content = Get-Content -LiteralPath $versionsPropsPath -Raw
$content = $content -replace '<SilLibPalasoVersion>[^<]*</SilLibPalasoVersion>', "<SilLibPalasoVersion>$version</SilLibPalasoVersion>"
Set-Content -LiteralPath $versionsPropsPath -Value $content -NoNewline
Write-Host "Updated SilVersions.props (SilLibPalasoVersion = $version)" -ForegroundColor Yellow

# A stale extraction of this same version would otherwise keep serving old bits after a re-pack.
$cacheRoot = Join-Path $env:USERPROFILE '.nuget\packages'
foreach ($id in @('sil.core', 'sil.writingsystems')) {
    $stale = Join-Path $cacheRoot "$id\$($version.ToLowerInvariant())"
    if (Test-Path $stale) {
        Remove-Item -Recurse -Force $stale
        Write-Host "Cleared stale cache entry: $stale" -ForegroundColor Yellow
    }
}

# libpalaso builds to output/Debug/, not bin/Debug; copied here so a debugger can use LOCAL_NUGET_REPO.
$pdbDir = Join-Path $sourceDir 'output/Debug'
if (Test-Path $pdbDir) {
    Get-ChildItem -Path $pdbDir -Filter '*.pdb' -Recurse -File |
        Where-Object { $_.BaseName -in @('SIL.Core', 'SIL.WritingSystems') } |
        Copy-Item -Destination $localRepo -Force
}

Write-Host ''
Write-Host "Done. Run ./build.ps1 to build against $version from $localRepo." -ForegroundColor Green
Write-Host 'To revert: git checkout SilVersions.props' -ForegroundColor Yellow
