<#
  .SYNOPSIS
  Counts comment-hygiene violations in this repo's C# and fails if any remain.

  .DESCRIPTION
  ZERO TOLERANCE. Every violation is reported and every one is meant to go; there is no accepted
  count and no baseline file. A baseline records the CURRENT count as acceptable, and re-baselining
  after a rule change quietly relabels old debt as the new normal.

  Rules are stated in .claude/skills/code-comments/SKILL.md. Summary: a comment explains why; code
  says what, git says when, the plan and the issue register say where the project is. Project state
  in a source comment is true when written and unchecked forever after.

  The categories divide into two families.

  LINE-LEVEL (plan-reference, issue-reference, slice-status, date-in-comment, history-prose,
  attribution) score a single comment line against a regex. These catch project state: facts that
  were true when typed and are never checked again.

  BLOCK-LEVEL (impl-comment-too-long, cross-reference-claim, docs-link-broken, dead-citation) score a
  run of consecutive comment lines. They exist because the line-level family cannot see a comment
  that is simply FALSE ABOUT BEHAVIOUR, which is a different and more expensive class. Length is not
  why such a comment rots -- a one-line false claim rots identically. What rots is an unverifiable
  CLAIM ABOUT ANOTHER CODE ENTITY.

  The C# port differs from PanGloss's Rust original in two ways that matter:

    - `<see cref="X"/>` is resolved by the C# compiler (CS1574), so code-to-code references are NOT
      banned here. Rust's intra-doc links rot unobserved; C#'s do not. A cref still proves only that
      the name resolves, never that the sentence is true.
    - The interface/implementation split is `///` on a public or internal member (long form allowed)
      versus `//` anywhere and `///` on a private member (one line).

  Generated `*.g.cs` files are excluded: they are template output, and the fix for a bad comment in
  one is a fix to the emitter. The emitters ARE scanned, but their templates are C# string literals,
  so this checker cannot see comment text inside them. That is a known gap, not a covered case.

  .PARAMETER List
  Show the offending file, line number and text for every violation.

  .PARAMETER Category
  Report only one category.

  .EXAMPLE
  tools\comment-hygiene.ps1
  Report counts; exit 1 if any violation exists.

  .EXAMPLE
  tools\comment-hygiene.ps1 -List -Category impl-comment-too-long
  Show every over-long implementation comment.
#>
[CmdletBinding()]
param(
    [switch] $List,
    [string] $Category
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent

# Left-boundaried so a legitimate research filename's tail cannot match.
$categories = [ordered]@{
    'plan-reference'  = 'docs/plan-motif|docs/plan-cross-repo|docs/plan-lcmcrdt|plan-motif\.md|HANDOFF\.md|build-stages\.md|implementation-plan\.md|operation-catalog-plan\.md'
    'issue-reference' = '(?-i:\bMOT-\d+)|docs/issues|issues\.md|(?-i:\b(?:issue|issues)\s+[A-Z]\d+)|(?-i:\b[A-Z]\d+\b(?=[,.;:]?\s+(?:in\s+)?docs/issues))'
    'slice-status'    = 'not wired|NOT wired|purely additive|Purely additive|not yet consumed|no slice ships|today exactly|currently names|a later increment|later slice'
    'date-in-comment' = '\b20\d\d-\d\d-\d\d\b'
    'history-prose'   = 'used to read|previously read|first shipped|used to be|this used to|renamed from|was stale|corrected in place|the older .* wording|earlier version of this'
    'attribution'     = 'an earlier agent|a subagent|the owner asked|the reviewer asked|another agent|this session'
}

# Present-tense active only, word-boundaried: a claim about a DIFFERENT entity, which nothing checks.
$claimVerbs = @(
    '\brefuses\b', '\brejects\b', '\baccepts unconditionally\b', '\balready refuses\b',
    '\bonly caller\b', '\bsole caller\b', '\bcalled from exactly\b', '\bzero callers\b',
    '\bno production caller\b', '\bunreachable\b', '\bnever called\b', '\bnever reached\b',
    '\bcannot happen\b', '\bcannot be reached\b', '\balways returns\b', '\bnever returns\b',
    '\bnever fires\b', '\balways fires\b', '\bis not wired\b'
) -join '|'

# A machine-checked citation to the test that pins a claim. C# test names are PascalCase with
# underscores, so the shape differs from the Rust original's snake_case.
$citationPhrase = '(?i)(?:pinned by|pins|asserted by|witnessed by|proved by|checked by)\s+`([A-Za-z_][A-Za-z0-9_]*)`'

# The only grounds on which an implementation comment may exceed one line: a closed, named set, so a
# class cannot be invented. C# `unsafe` is the same proof obligation Rust's is; this repo has none
# today, and the tag exists so that when one appears it has a home rather than an argument.
$exceptionTags = @('SAFETY:')

$sourceFiles = @(
    Get-ChildItem -Path (Join-Path $repoRoot 'src') -Filter '*.cs' -Recurse -File -ErrorAction SilentlyContinue
    Get-ChildItem -Path (Join-Path $repoRoot 'tests') -Filter '*.cs' -Recurse -File -ErrorAction SilentlyContinue
    Get-ChildItem -Path (Join-Path $repoRoot 'tools') -Filter '*.ps1' -Recurse -File -ErrorAction SilentlyContinue
) | Where-Object {
    $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -notmatch '\.g\.cs$'
}

if ($sourceFiles.Count -eq 0) {
    Write-Error "No source files found under '$repoRoot'. Refusing to report a clean tree from an empty scan."
    exit 2
}

$blockCategories = @('impl-comment-too-long', 'unanchored-exception', 'cross-reference-claim', 'docs-link-broken', 'dead-citation')
foreach ($cat in $blockCategories) { $categories[$cat] = $null }

# Every declared name in the tree, so a `pinned by` citation is checked rather than trusted.
$declaredNames = [System.Collections.Generic.HashSet[string]]::new()
foreach ($f in $sourceFiles) {
    if ($f.Extension -ne '.cs') { continue }
    foreach ($line in [System.IO.File]::ReadLines($f.FullName)) {
        foreach ($m in [regex]::Matches($line, '\b(?:class|record|struct|interface|enum|void|Task)\s+([A-Za-z_][A-Za-z0-9_]*)')) {
            [void]$declaredNames.Add($m.Groups[1].Value)
        }
        foreach ($m in [regex]::Matches($line, '^\s*(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?[A-Za-z_][\w<>,\[\]\. ]*\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(')) {
            [void]$declaredNames.Add($m.Groups[1].Value)
        }
    }
}

$counts = [ordered]@{}
foreach ($cat in $categories.Keys) { $counts[$cat] = 0 }
$counts['long-blocks-anchored'] = 0
$hits = @{}
foreach ($cat in $counts.Keys) { $hits[$cat] = New-Object System.Collections.ArrayList }

function Add-Hit {
    param([string] $Cat, [string] $File, [int] $Line, [string] $Text)
    $script:counts[$Cat]++
    [void]$script:hits[$Cat].Add(
        ('{0}:{1}: {2}' -f (Resolve-Path -Relative -Path $File), $Line, $Text.Trim()))
}

# A member's own visibility decides whether its `///` is an API doc or an implementation comment.
# Looks at the first non-blank, non-attribute, non-comment line beneath the block.
function Get-DeclarationVisibility {
    param([string[]] $Lines, [int] $StartIndex)

    for ($i = $StartIndex; $i -lt $Lines.Count -and $i -lt $StartIndex + 12; $i++) {
        $line = $Lines[$i].Trim()
        if ($line -eq '' -or $line.StartsWith('[') -or $line.StartsWith('//')) { continue }
        if ($line -match '^\s*(public|internal|protected)\b') { return 'api' }
        if ($line -match '^\s*private\b') { return 'private' }
        # C# defaults to private for members and internal for top-level types. A top-level `class`
        # with no modifier is internal, so it is an API surface within the assembly.
        if ($line -match '^\s*(sealed\s+|static\s+|abstract\s+|partial\s+)*(class|record|struct|interface|enum)\b') { return 'api' }
        return 'private'
    }

    return 'private'
}

foreach ($file in $sourceFiles) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    $isPowerShell = $file.Extension -eq '.ps1'

    # Block accumulation: a run of consecutive comment lines of the same kind.
    $blockStart = -1
    $blockLines = New-Object System.Collections.ArrayList
    $blockIsDoc = $false

    function Test-Block {
        param([string[]] $Text, [int] $Start, [bool] $IsDoc, [string[]] $AllLines, [string] $FilePath)

        if ($Text.Count -eq 0) { return }
        $joined = ($Text -join ' ')

        $visibility = if ($IsDoc) { Get-DeclarationVisibility -Lines $AllLines -StartIndex ($Start + $Text.Count) } else { 'private' }
        $isApiDoc = $IsDoc -and $visibility -eq 'api'

        $anchored =
            $joined -match 'docs/research/[A-Za-z0-9._/-]+\.md' -or
            $joined -match 'https?://' -or
            $joined -match $script:citationPhrase

        if (-not $isApiDoc -and $Text.Count -gt 1) {
            $hasException = $false
            foreach ($tag in $script:exceptionTags) {
                if ($joined -match [regex]::Escape($tag)) { $hasException = $true; break }
            }

            if ($hasException) {
                if ($Text.Count -gt 3) {
                    Add-Hit 'unanchored-exception' $FilePath ($Start + 1) $Text[0]
                }
            }
            else {
                Add-Hit 'impl-comment-too-long' $FilePath ($Start + 1) ("{0} lines: {1}" -f $Text.Count, $Text[0])
            }
        }
        elseif ($isApiDoc -and $Text.Count -gt 12 -and $anchored) {
            $script:counts['long-blocks-anchored']++
        }

        # A claim about another entity, with no citation of the test that pins it.
        if ($joined -match $script:claimVerbs -and $joined -notmatch $script:citationPhrase) {
            Add-Hit 'cross-reference-claim' $FilePath ($Start + 1) $Text[0]
        }

        foreach ($m in [regex]::Matches($joined, 'docs/[A-Za-z0-9._/-]+\.md')) {
            $target = Join-Path $script:repoRoot ($m.Value -replace '/', '\')
            if (-not (Test-Path $target)) {
                Add-Hit 'docs-link-broken' $FilePath ($Start + 1) $m.Value
            }
        }

        foreach ($m in [regex]::Matches($joined, $script:citationPhrase)) {
            $name = $m.Groups[1].Value
            if (-not $script:declaredNames.Contains($name)) {
                Add-Hit 'dead-citation' $FilePath ($Start + 1) ("pinned by ``{0}`` -- no such declaration" -f $name)
            }
        }
    }

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $raw = $lines[$i]
        $trimmed = $raw.Trim()

        $isDocLine = -not $isPowerShell -and $trimmed.StartsWith('///')
        $isImplLine = if ($isPowerShell) { $trimmed.StartsWith('#') -and -not $trimmed.StartsWith('#>') } else { $trimmed.StartsWith('//') -and -not $trimmed.StartsWith('///') }
        $isComment = $isDocLine -or $isImplLine

        if ($isComment) {
            $body = if ($isDocLine) { $trimmed.Substring(3) } elseif ($isPowerShell) { $trimmed.TrimStart('#') } else { $trimmed.Substring(2) }

            foreach ($cat in $categories.Keys) {
                if ($null -eq $categories[$cat]) { continue }
                if ($body -match $categories[$cat]) { Add-Hit $cat $file.FullName ($i + 1) $body }
            }

            if ($blockStart -lt 0) {
                $blockStart = $i
                $blockIsDoc = $isDocLine
                [void]$blockLines.Clear()
            }
            elseif ($blockIsDoc -ne $isDocLine) {
                Test-Block -Text $blockLines.ToArray() -Start $blockStart -IsDoc $blockIsDoc -AllLines $lines -FilePath $file.FullName
                $blockStart = $i
                $blockIsDoc = $isDocLine
                [void]$blockLines.Clear()
            }

            [void]$blockLines.Add($body)
        }
        elseif ($blockStart -ge 0) {
            Test-Block -Text $blockLines.ToArray() -Start $blockStart -IsDoc $blockIsDoc -AllLines $lines -FilePath $file.FullName
            $blockStart = -1
            [void]$blockLines.Clear()
        }
    }

    if ($blockStart -ge 0) {
        Test-Block -Text $blockLines.ToArray() -Start $blockStart -IsDoc $blockIsDoc -AllLines $lines -FilePath $file.FullName
    }
}

$total = 0
foreach ($cat in $categories.Keys) { $total += $counts[$cat] }

Write-Host ''
Write-Host ("comment hygiene -- {0} file(s) scanned" -f $sourceFiles.Count)
Write-Host ('-' * 60)
foreach ($cat in $categories.Keys) {
    $n = $counts[$cat]
    $marker = if ($n -gt 0) { 'X' } else { '.' }
    Write-Host ('  {0} {1,-24} {2,6}' -f $marker, $cat, $n)
}
Write-Host ('    {0,-24} {1,6}   (reported, never gated)' -f 'long-blocks-anchored', $counts['long-blocks-anchored'])
Write-Host ('-' * 60)
Write-Host ("  TOTAL {0}" -f $total)
Write-Host ''

if ($List) {
    foreach ($cat in $categories.Keys) {
        if ($Category -and $cat -ne $Category) { continue }
        if ($counts[$cat] -eq 0) { continue }
        Write-Host ''
        Write-Host ("=== {0} ({1}) ===" -f $cat, $counts[$cat])
        foreach ($hit in $hits[$cat]) { Write-Host "  $hit" }
    }
}

if ($total -gt 0) { exit 1 }
exit 0
