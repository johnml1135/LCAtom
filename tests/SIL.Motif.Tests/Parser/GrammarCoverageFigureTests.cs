using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using Xunit;

namespace SIL.Motif.Tests.Parser;

/// <summary>
/// Unit tests for assembling a <see cref="GrammarCoverageFigure"/> from a <see cref="BatchAnalysis"/> and a
/// <see cref="Selection"/>. Built on synthetic rows rather than captured fixtures — the mapping from
/// raw parser text to <see cref="WordAnalysis"/> is already covered by <see cref="ParserOutputTests"/>
/// against real captured output; what is new and untested here is purely the arithmetic and provenance
/// assembly on top of already-typed results.
/// </summary>
public class GrammarCoverageFigureTests
{
    private static BatchAnalysis Batch(params (string Word, WordOutcome Outcome)[] words) =>
        new(
            Words: words.Select((w, i) => new WordAnalysis(i, w.Word, ElapsedMs: 10, w.Outcome, Signature: "-")).ToList(),
            Engine: ParserEngine.FstPrunedByHermitCrab,
            PerWordTimeoutMs: 5000,
            ProjectPath: "irrelevant.fwdata",
            Warnings: Array.Empty<string>());

    [Fact]
    public void Compute_DenominatorExcludesTimedOutAndSkippedWords()
    {
        var batch = Batch(
            ("mbali", WordOutcome.Analysed),
            ("ya", WordOutcome.Analysed),
            ("nkazi", WordOutcome.NoAnalysis),
            ("munthu", WordOutcome.TimedOut),
            ("anthu", WordOutcome.Skipped));
        var selection = Selection.Create("test-selection", batch.Words.Select(w => w.Word));

        var figure = GrammarCoverageFigure.Compute(batch, selection, "sha256:" + new string('a', 64));

        // Denominator is analysed (2) + no-analysis (1) = 3, not the 5 rows in the batch.
        Assert.Equal(3, figure.Adjudicated);
        Assert.Equal(2, figure.Analysed);
        Assert.Equal(1, figure.TimedOutCount);
        Assert.Equal(2.0 / 3.0, figure.Fraction);
    }

    [Fact]
    public void Compute_MarksLowerBoundWhenAnyWordTimedOut()
    {
        var batch = Batch(("mbali", WordOutcome.Analysed), ("ya", WordOutcome.TimedOut));
        var selection = Selection.Create("test-selection", new[] { "mbali", "ya" });

        var figure = GrammarCoverageFigure.Compute(batch, selection, "sha256:" + new string('a', 64));

        Assert.True(figure.IsLowerBound);
    }

    [Fact]
    public void Compute_NotALowerBoundWhenNoWordTimedOut()
    {
        var batch = Batch(("mbali", WordOutcome.Analysed), ("ya", WordOutcome.NoAnalysis));
        var selection = Selection.Create("test-selection", new[] { "mbali", "ya" });

        var figure = GrammarCoverageFigure.Compute(batch, selection, "sha256:" + new string('a', 64));

        Assert.False(figure.IsLowerBound);
        Assert.Equal(0, figure.TimedOutCount);
    }

    [Fact]
    public void Compute_FractionIsNullWhenNothingWasAdjudicated()
    {
        // Nothing was judged against the grammar (all timed out/skipped): no percentage — not 0%, not 100%.
        var batch = Batch(("mbali", WordOutcome.TimedOut), ("ya", WordOutcome.Skipped));
        var selection = Selection.Create("test-selection", new[] { "mbali", "ya" });

        var figure = GrammarCoverageFigure.Compute(batch, selection, "sha256:" + new string('a', 64));

        Assert.Equal(0, figure.Adjudicated);
        Assert.Null(figure.Fraction);
        Assert.True(figure.IsLowerBound);
    }

    [Fact]
    public void Compute_CitesTheFullProvenanceSetAdr0032Requires()
    {
        var batch = Batch(("mbali", WordOutcome.Analysed));
        var selection = Selection.Create("test-selection-3", new[] { "mbali" });
        var grammarHash = "sha256:" + new string('b', 64);

        var figure = GrammarCoverageFigure.Compute(batch, selection, grammarHash);

        Assert.Equal(selection.Name, figure.SelectionName);
        Assert.Equal(selection.Sha256, figure.SelectionSha256);
        Assert.Equal(grammarHash, figure.GrammarSourceSha256);
        Assert.Equal(ParserEngine.FstPrunedByHermitCrab, figure.Engine);
        Assert.Equal(5000, figure.PerWordTimeoutMs);
        Assert.Equal(0, figure.TimedOutCount);
    }

    [Fact]
    public void Compute_ThrowsWhenTheBatchsWordsDoNotMatchTheCorpus()
    {
        var batch = Batch(("mbali", WordOutcome.Analysed), ("ya", WordOutcome.Analysed));
        var selection = Selection.Create("test-selection", new[] { "mbali", "somethingElse" });

        var ex = Assert.Throws<ArgumentException>(
            () => GrammarCoverageFigure.Compute(batch, selection, "sha256:" + new string('a', 64)));

        Assert.Contains("test-selection", ex.Message);
    }

    [Fact]
    public void Compute_TheAssessReportOverload_PullsTheGrammarHashFromTheReport()
    {
        var batch = Batch(("mbali", WordOutcome.Analysed));
        var selection = Selection.Create("test-selection", new[] { "mbali" });
        var report = new AssessReport(
            Words: Array.Empty<AssessedWord>(),
            OutcomeDigest: "irrelevant",
            SemanticDigest: "irrelevant",
            GrammarSourceSha256: "sha256:" + new string('c', 64),
            ModelFingerprint: "irrelevant",
            Pipeline: "foma-confirm",
            DiagnosticCount: 0);

        var figure = GrammarCoverageFigure.Compute(batch, selection, report);

        Assert.Equal(report.GrammarSourceSha256, figure.GrammarSourceSha256);
    }

    // ------------------------------------------------------- rendering: tense carries staleness

    private static GrammarCoverageFigure Figure(
        string corpusSha = "sha256:aaaaaaaaaaaabbbb", string grammarSha = "sha256:ccccccccccccdddd",
        int analysed = 620, int adjudicated = 1000, int timedOut = 0, int? cap = null) =>
        new("tst-wikipedia", corpusSha, grammarSha, ParserEngine.FstPrunedByHermitCrab, cap, timedOut,
            analysed, adjudicated);

    /// <summary>
    /// A figure cannot be rendered without saying what the current selection and grammar are.
    /// </summary>
    /// <remarks>
    /// There is no parameterless <c>Describe</c>, and that omission is the enforcement: the rule "a report
    /// always names the state it describes" is not a convention someone has to remember, it is the only
    /// available call. If a bare overload is ever added, the rule is dead — every caller will use the shorter
    /// one.
    /// </remarks>
    [Fact]
    public void EveryRenderingNamesTheCorpusAndTheGrammar()
    {
        var figure = Figure();

        foreach (var sentence in new[]
                 {
                     figure.Describe("sha256:aaaaaaaaaaaabbbb", "sha256:ccccccccccccdddd"), // current
                     figure.Describe("sha256:9999999999990000", "sha256:ccccccccccccdddd"), // selection moved
                     figure.Describe("sha256:aaaaaaaaaaaabbbb", "sha256:8888888888887777"), // grammar moved
                 })
        {
            Assert.Contains("tst-wikipedia", sentence);
            Assert.Contains("aaaaaaaaaaaa", sentence);   // the selection this was measured over
            Assert.Contains("cccccccccccc", sentence);   // the grammar this was measured under
        }
    }

    /// <summary>
    /// Present tense is licensed only while both hashes still match; otherwise the claim is past tense.
    /// </summary>
    /// <remarks>
    /// A stale coverage figure is not wrong — it is a correct measurement of a state that has since changed,
    /// and it is real evidence about whether a change helped, so suppressing it would cost the reviewer the
    /// thing they most need. What makes a stale number dangerous is stating it in the <b>present</b> tense
    /// with a caveat attached, because the caveat is what gets dropped when somebody quotes it. Putting the
    /// date inside the claim cannot be paraphrased away. So the defence is grammar, not suppression.
    /// </remarks>
    [Fact]
    public void AStaleFigureIsStatedInThePastTenseAndSaysWhatMoved()
    {
        var figure = Figure();

        var current = figure.Describe("sha256:aaaaaaaaaaaabbbb", "sha256:ccccccccccccdddd");
        Assert.Contains("coverage is 62.0", current);
        Assert.DoesNotContain("was", current);
        Assert.DoesNotContain("no longer exists", current);

        var grammarMoved = figure.Describe("sha256:aaaaaaaaaaaabbbb", "sha256:8888888888887777");
        Assert.StartsWith("As of the assessment", grammarMoved);
        Assert.Contains("coverage was 62.0", grammarMoved);
        Assert.Contains("the grammar has changed", grammarMoved);
        Assert.Contains("888888888888", grammarMoved);          // names what it moved to
        Assert.DoesNotContain("the selection has changed", grammarMoved);

        // Both moved: both are named, so a reader knows the figure is doubly detached.
        var bothMoved = figure.Describe("sha256:9999999999990000", "sha256:8888888888887777");
        Assert.Contains("the selection has changed", bothMoved);
        Assert.Contains("the grammar has changed", bothMoved);
    }

    /// <summary>
    /// A lower bound says so in either tense, and nothing-adjudicated never renders as a percentage.
    /// </summary>
    /// <remarks>
    /// These are the two existing refusals this type already enforces (the lower-bound flag and
    /// the zero-adjudicated rule), and rendering must not be the place they leak. A sentence is what people
    /// actually read, so a caveat that survives in the record but not in the prose has not survived.
    /// </remarks>
    [Fact]
    public void RenderingPreservesTheLowerBoundAndTheNoVerdictRefusal()
    {
        var lowerBound = Figure(analysed: 620, adjudicated: 1000, timedOut: 7, cap: 5000);
        Assert.Contains("is at least 62.0", lowerBound.Describe("sha256:aaaaaaaaaaaabbbb", "sha256:ccccccccccccdddd"));
        Assert.Contains("was at least 62.0", lowerBound.Describe("sha256:aaaaaaaaaaaabbbb", "sha256:8888888888887777"));
        Assert.Contains("lower bound", lowerBound.Describe("sha256:aaaaaaaaaaaabbbb", "sha256:ccccccccccccdddd"));

        // Zero adjudicated is not 0% — no verdict was ever reached, so no percentage may appear at all.
        var noVerdict = Figure(analysed: 0, adjudicated: 0, timedOut: 40, cap: 5000);
        var sentence = noVerdict.Describe("sha256:aaaaaaaaaaaabbbb", "sha256:ccccccccccccdddd");
        Assert.Contains("not computable", sentence);
        Assert.DoesNotContain("%", sentence);
    }
}
