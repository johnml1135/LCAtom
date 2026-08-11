<#
  .SYNOPSIS
  Proves that an edit changed only comments, by diffing against HEAD.

  .DESCRIPTION
  Requires every line a change ADDS and every line it REMOVES to be a comment or blank.

  The symmetry is the whole point, and it is what the obvious version of this check gets wrong.
  Asking only "is everything you WROTE a comment?" is answered trivially and correctly by a pure
  deletion, so an Edit whose `old_string` ran past the end of a comment block -- deleting the `using`
  block and two type definitions along with it -- passes that check while the file stops compiling.

  Anchor the end of an `old_string` on the last comment line, never on the code that follows it. If a
  file does trip this verifier, `git checkout -- <file>` and redo the edit in smaller pieces rather
  than patching the wreckage.

  Two things this cannot do, so do not read a green result as more than it is. It is a diff-shape
  check, not a semantic one: it cannot tell a good comment from a bad one (that is
  comment-hygiene.ps1), and it cannot tell that a deleted comment should have been kept. What it does
  tell you, with no compiler and in under a second, is that the code is untouched.

  .PARAMETER Path
  Files to verify. Omit to verify every modified file in the working tree.

  .EXAMPLE
  tools\verify-comment-only.ps1
  Verify every file modified against HEAD.

  .EXAMPLE
  tools\verify-comment-only.ps1 src\SIL.Motif.Generator\Program.cs
  Verify one file.
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    if (-not $Path -or $Path.Count -eq 0) {
        $Path = @(git diff --name-only HEAD -- '*.cs' '*.ps1')
    }

    if (-not $Path -or $Path.Count -eq 0) {
        Write-Host 'verify-comment-only: no modified C#/PowerShell files against HEAD.'
        exit 0
    }

    # An empty diff for a file named as changed means the caller is verifying the wrong thing.
    $failures = New-Object System.Collections.ArrayList
    $checked = 0

    foreach ($file in $Path) {
        $diff = @(git diff -U0 HEAD -- $file)
        if ($diff.Count -eq 0) {
            Write-Host ("  ?  {0}: no diff against HEAD" -f $file)
            continue
        }

        $checked++
        $isPowerShell = $file -like '*.ps1'
        $lineNo = 0

        foreach ($line in $diff) {
            if ($line -match '^(\+\+\+|---|diff |index |@@)') { continue }
            if ($line.Length -eq 0) { continue }

            $sign = $line[0]
            if ($sign -ne '+' -and $sign -ne '-') { continue }

            $body = $line.Substring(1).Trim()
            if ($body -eq '') { continue }

            $isComment =
                if ($isPowerShell) { $body.StartsWith('#') -or $body.StartsWith('<#') -or $body.StartsWith('#>') }
                else { $body.StartsWith('//') -or $body.StartsWith('/*') -or $body.StartsWith('*') -or $body.StartsWith('*/') }

            if (-not $isComment) {
                [void]$failures.Add(('{0}: {1}{2}' -f $file, $sign, $body))
            }
        }
    }

    if ($checked -eq 0) {
        Write-Error 'verify-comment-only: every named file had an empty diff. Nothing was verified.'
        exit 2
    }

    if ($failures.Count -gt 0) {
        Write-Host ''
        Write-Host ("verify-comment-only: FAILED -- {0} non-comment line(s) in the diff" -f $failures.Count)
        foreach ($f in $failures) { Write-Host "  $f" }
        Write-Host ''
        Write-Host 'This edit touched code. `git checkout -- <file>` and redo it in smaller pieces.'
        exit 1
    }

    Write-Host ("verify-comment-only: OK -- {0} file(s), comments and blank lines only." -f $checked)
    exit 0
}
finally {
    Pop-Location
}
