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
    /// <summary>
    /// The count as of 2026-08-09 (MOT-22): `CmAgent` 4, `WfiAnalysis` 9, `WfiMorphBundle` 5,
    /// `WfiWordform` 3 (`Analyses`, `Form`, and now `SpellingStatus`), `WfiGloss` 1. `SpellingStatus`
    /// was turned on by MOT-22 with the same hand-authored ADR 0025 `ScopeReason` its `WfiWordform`
    /// siblings carry, so it is picked up by <see cref="Adr0025Rows"/> automatically.
    /// </summary>
    private const int ExpectedAdr0025RowCount = 22;

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

    /// <summary>
    /// MOT-22's own row, pinned individually rather than only counted: `WfiWordform.SpellingStatus`
    /// carries the same hand-authored `Group='analysis'`/`HcReachable='no'` treatment its `Analyses`
    /// and `Form` siblings do (asserted generically above), plus the one thing unique to it that
    /// `classify.ps1` cannot derive either — the confirmed `EnumValues` mapping, which is what lets the
    /// generated payload parser range-check a value instead of trusting LibLCM to fix it.
    /// </summary>
    [Fact]
    public void WfiWordformSpellingStatus_CarriesTheMot22Treatment()
    {
        var row = ManifestTsvParser.Parse(RepoPaths.DefaultManifestPath())
            .Single(r => r.Class == "WfiWordform" && r.Field == "SpellingStatus");

        Assert.Equal("in", row.Scope);
        Assert.Contains("ADR 0025", row.ScopeReason, StringComparison.Ordinal);
        Assert.Equal("analysis", row.Group);
        Assert.Equal("no", row.HcReachable);
        Assert.Equal("set|clear", row.Verbs);
        Assert.Equal("0=Undecided;1=Correct;2=Incorrect", row.EnumValues);
    }

    /// <summary>
    /// The invariant MOT-22 briefly broke and this test now guards: <b>every</b> in-scope enum field —
    /// a basic `Integer` whose `EnumValues` column names its legal values — carries the derived
    /// `set|clear`, with no exception anywhere in the table (ADR 0022 decision 1).
    /// </summary>
    /// <remarks>
    /// MOT-22 first shipped `WfiWordform.SpellingStatus` as `set`-only, on the argument that a `clear`
    /// writing the zero member `Undecided` would be a synonym for `set 0`. The other ten rows here refute
    /// it: their zero members are `CenterInColumn`, `Variant`, `LeftToRightIterative`, `kpntName`,
    /// `Anywhere`, `ShowMinorEntry` — all substantive values, all keeping `clear`. `clear` in this
    /// manifest has never meant "erase to nothing"; it means "write the zero member". A future row that
    /// wants an exception has to break this test first, which is the point.
    /// </remarks>
    [Fact]
    public void EveryInScopeEnumField_CarriesTheDerivedSetClear()
    {
        var enumRows = ManifestTsvParser.Parse(RepoPaths.DefaultManifestPath())
            .Where(r => r.Scope == "in" && r.Kind == "basic" && r.Sig == "Integer")
            .Where(r => r.Verbs != "n/a")
            .Where(r => !string.IsNullOrWhiteSpace(r.EnumValues) && r.EnumValues != "unknown")
            .ToList();

        var departures = enumRows.Where(r => r.Verbs != "set|clear").ToList();

        Assert.True(
            departures.Count == 0,
            $"{departures.Count} enum row(s) depart from the derived set|clear:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ",
                departures.Select(r => $"{r.Class}.{r.Field} has Verbs='{r.Verbs}'")));

        // A guard on the guard: if the enum-row filter ever stops matching anything, the assertion above
        // passes vacuously and stops meaning what it says. Eleven rows today — the ten named in the
        // remarks plus SpellingStatus itself. (CmPossibility.UnderStyle is a twelfth enum field whose
        // EnumValues column still reads `unknown`, so it is not one this filter can speak for.)
        Assert.True(enumRows.Count >= 11, $"expected at least 11 enum rows, found {enumRows.Count}.");
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
