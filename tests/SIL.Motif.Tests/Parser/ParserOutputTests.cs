using SIL.Motif.Host.Parser;
using Xunit;

namespace SIL.Motif.Tests.Parser;

/// <summary>
/// Unit tests for the mapping from PanGloss's vocabulary into Motif's, over <b>captured real output</b>
/// rather than invented fixtures.
/// </summary>
/// <remarks>
/// <para>
/// The fixtures under <c>Fixtures/</c> are verbatim output from real runs: the batch rows come from parsing
/// the 56 MB <c>Sena 3.fwdata</c> and from an Amharic run that genuinely timed out, and the refusal text is
/// the exact message a real project produced when its grammar overflowed the FST enumeration budget. Invented
/// fixtures would test this code against my assumptions about the parser rather than against the parser.
/// </para>
/// <para>
/// What these tests defend is the distinction the whole grammar coverage story rests on: <b>timed out is not
/// failed</b>. See <c>docs/issues.md</c> <c>D9</c> — the moment a figure counts "we stopped waiting" as "the
/// grammar cannot analyse this", coverage stops being usable as a target, because it then moves when the
/// machine is busy.
/// </para>
/// </remarks>
public class ParserOutputTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Parser", "Fixtures", name));

    [Fact]
    public void BatchOutput_SeparatesAnalysedFromTimedOutFromSkipped()
    {
        var words = BatchTsvParser.Parse(Fixture("batch-mixed-outcomes.tsv"));

        Assert.Equal(10, words.Count);
        Assert.Equal(7, words.Count(w => w.Outcome == WordOutcome.Analysed));
        Assert.Equal(1, words.Count(w => w.Outcome == WordOutcome.Skipped));
        Assert.Equal(2, words.Count(w => w.Outcome == WordOutcome.TimedOut));

        // Nothing is a failure here, and in particular the two timeouts are not.
        Assert.Empty(words.Where(w => w.Outcome == WordOutcome.NoAnalysis));
    }

    [Fact]
    public void BatchOutput_PreservesWordsIndicesAndTimings()
    {
        var words = BatchTsvParser.Parse(Fixture("batch-mixed-outcomes.tsv"));

        var mbali = words.Single(w => w.Word == "mbali");
        Assert.Equal(2, mbali.Index);
        Assert.Equal(37, mbali.ElapsedMs);
        Assert.Equal(WordOutcome.Analysed, mbali.Outcome);
        Assert.Contains("bal", mbali.Signature);

        // A skipped word still carries its row: dropping it would silently shrink the denominator.
        var skipped = words.Single(w => w.Outcome == WordOutcome.Skipped);
        Assert.Equal("n'nyumba", skipped.Word);
    }

    [Fact]
    public void ABatchWithAnyTimeout_IsMarkedALowerBound()
    {
        var analysis = new BatchAnalysis(
            BatchTsvParser.Parse(Fixture("batch-mixed-outcomes.tsv")),
            ParserEngine.FstPrunedByHermitCrab,
            PerWordTimeoutMs: 5000,
            ProjectPath: "irrelevant.fwdata",
            Warnings: Array.Empty<string>());

        Assert.True(analysis.IsLowerBound);
        Assert.Equal(2, analysis.TimedOut);

        // The honest denominator excludes words with no verdict either way — 7 analysed + 0 unanalysable,
        // not 10. Using the row count would report 70% coverage where the truth is 100% of what was judged.
        Assert.Equal(7, analysis.Adjudicated);
        Assert.Equal(7, analysis.Analysed);
    }

    [Fact]
    public void ABatchWithNoTimeouts_IsNotALowerBound()
    {
        var clean = BatchTsvParser.Parse(Fixture("batch-mixed-outcomes.tsv"))
            .Where(w => w.Outcome != WordOutcome.TimedOut)
            .ToList();

        var analysis = new BatchAnalysis(
            clean, ParserEngine.FstPrunedByHermitCrab, 5000, "irrelevant.fwdata", Array.Empty<string>());

        Assert.False(analysis.IsLowerBound);
    }

    [Fact]
    public void AnUnrecognisedStatus_ThrowsRatherThanBecomingAFailure()
    {
        // The defect this prevents: a future parser status silently bucketed as "no analysis" would move
        // every coverage number downward with no diagnostic anywhere.
        var ex = Assert.Throws<InvalidOperationException>(
            () => BatchTsvParser.Parse("0\tword\t5\tSOMETHING_NEW\t-\n"));

        Assert.Contains("SOMETHING_NEW", ex.Message);
        Assert.Contains("D9", ex.Message);
    }

    [Fact]
    public void TheRealBudgetRefusal_IsRecognisedAsAGrammarFactSoTheFallbackCanFire()
    {
        var refusal = ParserRefusalRecognizer.Recognize(Fixture("refusal-budget.txt"));

        Assert.NotNull(refusal);
        Assert.Equal(ParserRefusalKind.FstEnumerationBudgetExceeded, refusal!.Kind);

        // The parser's own numbers survive into the diagnostic: a reviewer needs to see how far over the
        // limit this grammar is, not merely that it was over.
        Assert.Contains("200500", refusal.Detail);
        Assert.Contains("200000", refusal.Detail);
    }

    [Fact]
    public void AMissingExecutableOrBadPath_IsNotMistakenForARefusedGrammar()
    {
        // The two must stay distinguishable: one means "use the other engine", the other means "your build
        // environment is wrong". Conflating them files an environment problem as a linguistic finding.
        Assert.Null(ParserRefusalRecognizer.Recognize("No such file or directory"));
        Assert.Null(ParserRefusalRecognizer.Recognize(""));
        Assert.Null(ParserRefusalRecognizer.Recognize("thread 'main' panicked at src/main.rs:1:1"));
    }

    [Fact]
    public void EngineFlags_MapBothCommandSpellingsOfTheSameMode()
    {
        // batch and assess spell the identical propose-then-confirm composite differently, which has already
        // been mis-read once. Pinned so the next reader does not have to re-derive it.
        Assert.Equal("foma", ParserEngine.FstPrunedByHermitCrab.BatchEngine());
        Assert.Equal("foma-confirm", ParserEngine.FstPrunedByHermitCrab.AssessPipeline());
        Assert.Equal("default", ParserEngine.HermitCrabOnly.BatchEngine());
        Assert.Equal("hermitcrab", ParserEngine.HermitCrabOnly.AssessPipeline());
    }
}
