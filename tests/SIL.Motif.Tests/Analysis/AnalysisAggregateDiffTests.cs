using SIL.Motif.Host.Analysis;
using Xunit;

namespace SIL.Motif.Tests.Analysis;

/// <summary>
/// <see cref="AnalysisAggregateDiff"/> — ADR 0038 decision 5's "what changed is the difference between two
/// responses", computed from the manual analyses only. There is no separate change-tracking type in this
/// codebase; this is the whole thing.
/// </summary>
/// <remarks>
/// The rules defended here, in order of how much damage getting them wrong would do:
/// <list type="number">
/// <item><b>Established, updated and removed are three separate lists, never netted.</b> Deleting the
/// last approved analysis on a word form must show up as <c>Removed</c> and must not cancel out against,
/// or be indistinguishable from, an <c>Established</c> entry elsewhere — netting them is exactly the
/// failure ADR 0038 names: a shrinking test suite would look identical to a growing one.</item>
/// <item><b>Removing the last approved analysis never reads as an improvement anywhere.</b> There is no
/// "net" or "score" field on <see cref="ManualAnalysisDiff"/> for a caller to misread — only four lists,
/// and <c>Removed</c> having an entry is the entire story.</item>
/// <item><b>A vanished word form is reported separately from a removed analysis set.</b> The word form
/// object disappearing entirely (ADR 0038 decision 3: no tombstone, no forwarding record) is a different,
/// more serious fact than the same word form persisting with zero approved analyses, and conflating them
/// would make a data loss and a deliberate cleanup look like the same event.</item>
/// <item><b>Comparison is by content digest, never by any per-object or per-response identity.</b> Two
/// <see cref="ApprovedAnalysis"/> records with the same <see cref="ApprovedAnalysis.ContentDigest"/> but
/// different origins (as they would be if rebuilt from a re-created <c>WfiAnalysis</c> with identical
/// content, per ADR 0038 decision 3) must not register as a change.</item>
/// <item><b>Count-only changes and freshly-unanalysed word forms produce no diff entry.</b> Occurrence
/// counts are text churn, and a brand-new word form with no manual analysis carries no expectation
/// (decision 7) — neither is a test change.</item>
/// </list>
/// </remarks>
public class AnalysisAggregateDiffTests
{
    private static WordFormAnalysisAggregate Wordform(string guid, string form, params ApprovedAnalysis[] manual) =>
        new(guid, form, manual, AutomaticAnalyses: null);

    private static ApprovedAnalysis Approved(string digest, int occurrences = 1) =>
        new(digest, MorphBreakdown: digest, OccurrenceCount: occurrences);

    private static AnalysisAggregateResponse Response(params WordFormAnalysisAggregate[] wordforms) =>
        new(wordforms, Assessment: null);

    [Fact]
    public void AWordFormGainingItsFirstApprovedAnalysis_IsEstablished()
    {
        var before = Response(Wordform("w1", "mbali"));
        var after = Response(Wordform("w1", "mbali", Approved("d1")));

        var diff = AnalysisAggregateDiff.Compute(before, after);

        Assert.Single(diff.Established);
        Assert.Equal("w1", diff.Established[0].WordformGuid);
        Assert.Empty(diff.Updated);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Vanished);
    }

    [Fact]
    public void ABrandNewWordFormWithAnApprovedAnalysis_IsEstablished()
    {
        var before = Response(); // the word form did not exist at all in the earlier response
        var after = Response(Wordform("w1", "mbali", Approved("d1")));

        var diff = AnalysisAggregateDiff.Compute(before, after);

        Assert.Single(diff.Established);
    }

    [Fact]
    public void ABrandNewWordFormWithNoApprovedAnalysis_ProducesNoDiffEntryAtAll()
    {
        // A word nobody has analysed carries no expectation (ADR 0038 decision 7) — merely appearing in
        // the project is not itself a test change.
        var before = Response();
        var after = Response(Wordform("w1", "mbali"));

        var diff = AnalysisAggregateDiff.Compute(before, after);

        Assert.Empty(diff.Established);
        Assert.Empty(diff.Updated);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Vanished);
    }

    [Fact]
    public void ChangingWhichAnalysesAreApproved_IsUpdated_NotEstablishedOrRemoved()
    {
        var before = Response(Wordform("w1", "mbali", Approved("d1")));
        var after = Response(Wordform("w1", "mbali", Approved("d2")));

        var diff = AnalysisAggregateDiff.Compute(before, after);

        Assert.Single(diff.Updated);
        Assert.Empty(diff.Established);
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public void RemovingTheLastApprovedAnalysis_IsRemoved_AndNeverReadsAsAnImprovementAnywhere()
    {
        var before = Response(Wordform("w1", "mbali", Approved("d1")));
        var after = Response(Wordform("w1", "mbali")); // zero approved analyses now, word form still exists

        var diff = AnalysisAggregateDiff.Compute(before, after);

        // It lands in Removed, and nowhere else: not Established (nothing was gained), not Updated (the
        // set did not move to another non-empty set), not Vanished (the word form itself still exists).
        Assert.Single(diff.Removed);
        Assert.Empty(diff.Established);
        Assert.Empty(diff.Updated);
        Assert.Empty(diff.Vanished);

        // There is no field anywhere on ManualAnalysisDiff — no net, no score, no boolean — from which
        // this could be read off as an improvement; Removed having one entry is the whole story.
    }

    [Fact]
    public void EstablishedUpdatedAndRemoved_AreNeverNetted_AllThreeAtOnceStayInThreeSeparateLists()
    {
        var before = Response(
            Wordform("gaining", "a"),
            Wordform("changing", "b", Approved("d1")),
            Wordform("losing-its-last", "c", Approved("d1")));
        var after = Response(
            Wordform("gaining", "a", Approved("d-new")),
            Wordform("changing", "b", Approved("d2")),
            Wordform("losing-its-last", "c"));

        var diff = AnalysisAggregateDiff.Compute(before, after);

        // Three real events, three separate counts of one each — never summed or reported as "net zero".
        Assert.Single(diff.Established);
        Assert.Single(diff.Updated);
        Assert.Single(diff.Removed);
        Assert.Equal("gaining", diff.Established[0].WordformGuid);
        Assert.Equal("changing", diff.Updated[0].WordformGuid);
        Assert.Equal("losing-its-last", diff.Removed[0].WordformGuid);
    }

    [Fact]
    public void AWordFormThatDisappearsEntirely_IsVanished_NotRemoved()
    {
        var before = Response(Wordform("w1", "mbali", Approved("d1")));
        var after = Response(); // w1 is simply gone, e.g. merged away per ADR 0038 decision 3

        var diff = AnalysisAggregateDiff.Compute(before, after);

        Assert.Single(diff.Vanished);
        Assert.Equal("w1", diff.Vanished[0].WordformGuid);

        // Distinct fact from Removed, which means the word form still exists with nothing approved.
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public void ContentDigestEqualityIsWhatMatters_NotWhereTheRecordCameFrom()
    {
        // Two ApprovedAnalysis records built independently that happen to carry the same content digest
        // (as they would if rebuilt from a re-created WfiAnalysis with identical Morph/Msa/InflType
        // references, per ADR 0038 decision 3) must not register as a change, even though their
        // OccurrenceCount and MorphBreakdown differ — those are display and text-churn facts, not identity.
        var before = Response(Wordform("w1", "mbali", new ApprovedAnalysis("same-digest", "root-SFX (old count)", 4)));
        var after = Response(Wordform("w1", "mbali", new ApprovedAnalysis("same-digest", "root-SFX (new count)", 9)));

        var diff = AnalysisAggregateDiff.Compute(before, after);

        Assert.Empty(diff.Established);
        Assert.Empty(diff.Updated);
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public void AWordFormAbsentFromBothOrUnchangedInBoth_ProducesNoEntryInAnyCategory()
    {
        var unchanged = Wordform("stable", "nyumba", Approved("d1"));
        var before = Response(unchanged);
        var after = Response(unchanged with { }); // a fresh record with identical content

        var diff = AnalysisAggregateDiff.Compute(before, after);

        Assert.Empty(diff.Established);
        Assert.Empty(diff.Updated);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Vanished);
    }
}
