using SIL.Motif.Generator;
using SIL.Motif.Generator.Manifest;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// Guards the manifest rows that <c>manifest/classify.ps1</c> would silently revert: the analysis-approval
/// family scoped in by [ADR 0025], whose `Group` and `HcReachable` values were authored by hand.
/// </summary>
/// <remarks>
/// <para>
/// <c>manifest/README.md</c> used to say "rerun `classify.ps1` after any inventory regeneration rather than
/// hand-editing the TSV." Running it on 2026-08-06 rewrote **26 rows** — every `CmAgent`, `TextTag`,
/// `WfiAnalysis`, `WfiGloss`, `WfiMorphBundle` and `WfiWordform` row — because the script does not know
/// about ADR 0025's analysis-approval scoping. It set `Group` to <c>system</c> and `HcReachable` to
/// <c>unconfirmed</c>, discarding <c>analysis</c> and <c>no</c>. Nothing noticed. That is issue <c>D7</c>,
/// and this test is its guard.
/// </para>
/// <para>
/// The generated <c>.g.cs</c> files have <see cref="GeneratedFilesAreUpToDateTests"/> for exactly this
/// failure class. The manifest had nothing, which made it the one hand-authored artifact in the build with
/// a partial producer and no drift detection — the worst combination, because the producer looks
/// authoritative.
/// </para>
/// <para>
/// <b>When this fails, work out which of two things happened.</b> If you re-ran <c>classify.ps1</c>, it
/// clobbered hand-authored policy and the fix is to restore those rows, not to edit this test. If you
/// deliberately rescoped the analysis family, update the expectations here in the same commit — that is
/// what makes the change deliberate rather than incidental.
/// </para>
/// </remarks>
public class ManifestHandAuthoredRowsTests
{
    /// <summary>The count as of 2026-08-06: `CmAgent` 4, `WfiAnalysis` 9, `WfiMorphBundle` 5, `WfiWordform` 2, `WfiGloss` 1.</summary>
    private const int ExpectedAdr0025RowCount = 21;

    private static IReadOnlyList<ManifestRow> Adr0025Rows() =>
        ManifestTsvParser.Parse(RepoPaths.DefaultManifestPath())
            .Where(r => r.ScopeReason.Contains("ADR 0025", StringComparison.Ordinal))
            .ToList();

    [Fact]
    public void RowsScopedInByAdr0025_KeepTheirHandAuthoredGroupAndReachability()
    {
        var rows = Adr0025Rows();

        Assert.Equal(ExpectedAdr0025RowCount, rows.Count);

        var wrongGroup = rows.Where(r => r.Group != "analysis").ToList();
        var wrongReachability = rows.Where(r => r.HcReachable != "no").ToList();

        Assert.True(
            wrongGroup.Count == 0,
            $"{wrongGroup.Count} row(s) scoped in by ADR 0025 no longer carry Group='analysis' — " +
            $"classify.ps1 sets these to 'system'. Restore them, or update this test if the rescope was " +
            $"deliberate:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ",
                wrongGroup.Select(r => $"{r.Class}.{r.Field} has Group='{r.Group}'")));

        Assert.True(
            wrongReachability.Count == 0,
            $"{wrongReachability.Count} row(s) scoped in by ADR 0025 no longer carry HcReachable='no' — " +
            $"classify.ps1 sets these to 'unconfirmed'. The parser does not read these fields; that is the " +
            $"point of the ADR:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ",
                wrongReachability.Select(r => $"{r.Class}.{r.Field} has HcReachable='{r.HcReachable}'")));
    }

    [Fact]
    public void EveryAdr0025Row_IsInScopeAndAuthorable()
    {
        // The whole point of ADR 0025's second half is that the approval side of analysis is authorable
        // while occurrence assignment is not. A row that drifted to Scope != in would silently drop the
        // approval half of the parser-first slice.
        var notInScope = Adr0025Rows().Where(r => r.Scope != "in").ToList();

        Assert.True(
            notInScope.Count == 0,
            $"{notInScope.Count} row(s) citing ADR 0025 are no longer in scope:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ",
                notInScope.Select(r => $"{r.Class}.{r.Field} has Scope='{r.Scope}'")));
    }
}
