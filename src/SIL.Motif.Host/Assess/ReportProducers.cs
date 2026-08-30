using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;

namespace SIL.Motif.Host.Assess;

/// <summary>
/// Resolves a Trial scope's opaque <see cref="StoredScope.Trial.Engine"/> name into the <see cref="ParserEngine"/>
/// a <see cref="BatchAnalysis"/> needs — PanGloss's own vocabulary, never <see cref="ScopeCodec"/>'s concern.
/// </summary>
internal static class ScopeEngine
{
    /// <exception cref="ReportRefusalException">The name is not one PanGloss's Assessor ever writes.</exception>
    public static ParserEngine Resolve(string engine, string reportKind)
    {
        if (string.IsNullOrWhiteSpace(engine) || !PanGlossEngineNames.TryParse(engine, out var parsed))
        {
            throw new ReportRefusalException(reportKind,
                $"the Assessment's recorded scope names an engine ('{engine}') this report cannot interpret.");
        }
        return parsed;
    }
}

/// <summary>Converts a stored per-word outcome string back to the enum it was written from.</summary>
internal static class StoredWordOutcome
{
    /// <exception cref="ReportRefusalException"><paramref name="outcome"/> is not a recognised stored value.</exception>
    public static WordOutcome Parse(string outcome, string reportKind) => outcome switch
    {
        "analysed" => WordOutcome.Analysed,
        "no-analysis" => WordOutcome.NoAnalysis,
        "timed-out" => WordOutcome.TimedOut,
        "skipped" => WordOutcome.Skipped,
        _ => throw new ReportRefusalException(reportKind, $"stored word outcome '{outcome}' is not recognised."),
    };
}

/// <summary>
/// Grammar coverage — the share of a scope's words the parser analysed — rendered from a <c>ParseTime</c>
/// Assessment's own stored words and outcomes. Delegates nothing to an Assessor and reimplements no part of
/// <see cref="GrammarCoverageFigure"/>; this type only rebuilds the <see cref="BatchAnalysis"/> that figure
/// already knows how to read.
/// </summary>
public sealed class CoverageReportProducer : IReportProducer
{
    /// <summary>The registry name this kind is asked for under.</summary>
    public const string KindName = "coverage";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public string Description => "Grammar coverage: the share of a scope's words the parser analysed.";

    /// <inheritdoc />
    public RenderedReport Produce(ReportableAssessment assessment, ReportQuery query, IAssessorCatalog assessors)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (!assessment.Kind.IsStoredKind(AssessmentKind.ParseTime))
        {
            throw new ReportRefusalException(KindName,
                $"this Assessment is a '{assessment.Kind}' measurement; a coverage report needs one collected " +
                "as 'ParseTime' (per-word outcome under a timing pass), which this scope did not collect.");
        }

        var scope = ScopeCodec.ReadTrial(assessment.ScopeJson, KindName);
        var engine = ScopeEngine.Resolve(scope.Engine, KindName);
        var words = assessment.Words.Select((word, index) => new WordAnalysis(
            index, word.Word, 0, StoredWordOutcome.Parse(word.Outcome, KindName), string.Empty)).ToList();
        var batch = new BatchAnalysis(
            words, engine, (int)scope.PerWordLimit.TotalMilliseconds, string.Empty, Array.Empty<string>());
        var corpus = new CorpusDescriptor(assessment.CorpusId, assessment.CorpusWords, assessment.CorpusSha256);
        var figure = GrammarCoverageFigure.Compute(batch, corpus, assessment.GrammarSourceSha256);
        return new RenderedReport(KindName, figure.Describe(assessment.CorpusSha256, assessment.GrammarSourceSha256));
    }
}

/// <summary>
/// Turns a <c>Correctness</c> Assessment's own stored words and analyses into the
/// <see cref="GrammarCoverageFigure"/> both <see cref="CorrectnessReportProducer"/> and
/// <c>RegressionChecker</c> need — a word counts as analysed when it carries at least one stored
/// <see cref="ParsedAnalysis"/> row, never by comparing digests across PanGloss's and FieldWorks' separate
/// identity schemes, which <c>AutomaticAnalysis</c>'s own remarks document as not comparable.
/// </summary>
internal static class CorrectnessCoverage
{
    /// <exception cref="ReportRefusalException">The scope's engine name cannot be read.</exception>
    public static GrammarCoverageFigure Compute(
        IReadOnlyList<AssessedWord> words, StoredScope.Trial scope, CorpusDescriptor corpus,
        string grammarSourceSha256, string reportKind)
    {
        var engine = ScopeEngine.Resolve(scope.Engine, reportKind);
        var analysed = words.Select((word, index) => new WordAnalysis(
            index, word.Word, 0,
            word.Analyses.Count > 0 ? WordOutcome.Analysed : WordOutcome.NoAnalysis,
            word.Analyses.Count > 0 ? word.Analyses[0].IdentityDigest : string.Empty)).ToList();
        var batch = new BatchAnalysis(
            analysed, engine, (int)scope.PerWordLimit.TotalMilliseconds, string.Empty, Array.Empty<string>());
        return GrammarCoverageFigure.Compute(batch, corpus, grammarSourceSha256);
    }
}

/// <summary>
/// Correctness against manual analysis: of the words this scope declared as carrying a human-decided
/// analysis, how many the parser still produces at least one analysis for. Rendered from a <c>Correctness</c>
/// Assessment's own stored words and analyses.
/// </summary>
/// <remarks>
/// Reuses <see cref="GrammarCoverageFigure"/> rather than a second figure type: whether a word still gets an
/// analysed verdict is exactly what that type already answers, and this kind differs from
/// <see cref="CoverageReportProducer"/> only in which Assessment kind it insists on and in what "analysed"
/// is read from.
/// </remarks>
public sealed class CorrectnessReportProducer : IReportProducer
{
    /// <summary>The registry name this kind is asked for under.</summary>
    public const string KindName = "correctness";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public string Description =>
        "Correctness against manual analysis: of the words carrying a human-decided analysis, how many the parser still analyses.";

    /// <inheritdoc />
    public RenderedReport Produce(ReportableAssessment assessment, ReportQuery query, IAssessorCatalog assessors)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (!assessment.Kind.IsStoredKind(AssessmentKind.Correctness))
        {
            throw new ReportRefusalException(KindName,
                $"this Assessment is a '{assessment.Kind}' measurement; a correctness report needs one " +
                "collected as 'Correctness' (parses checked against manual analysis), which this scope did " +
                "not collect.");
        }

        var corpus = new CorpusDescriptor(assessment.CorpusId, assessment.CorpusWords, assessment.CorpusSha256);
        var scope = ScopeCodec.ReadTrial(assessment.ScopeJson, KindName);
        var figure = CorrectnessCoverage.Compute(
            assessment.Words, scope, corpus, assessment.GrammarSourceSha256, KindName);
        var text = "Correctness against manual analysis — " +
            figure.Describe(assessment.CorpusSha256, assessment.GrammarSourceSha256);
        return new RenderedReport(KindName, text);
    }
}

/// <summary>
/// A comparison between two Assessments, rendered from a <c>Difference</c> Assessment's own stored rows —
/// what <c>compare</c> produced and stored, never recomputed from the two inputs it was made from. Each
/// stored word is one <see cref="WordChange"/> that survived the join; a word that behaved identically on
/// both sides was never written and so never appears here either.
/// </summary>
public sealed class DifferenceReportProducer : IReportProducer
{
    /// <summary>The registry name this kind is asked for under.</summary>
    public const string KindName = "difference";

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public string Description => "A comparison between two Assessments, joined on the word.";

    /// <inheritdoc />
    public RenderedReport Produce(ReportableAssessment assessment, ReportQuery query, IAssessorCatalog assessors)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (!assessment.Kind.IsStoredKind(AssessmentKind.Difference))
        {
            throw new ReportRefusalException(KindName,
                $"this Assessment is a '{assessment.Kind}' measurement; a difference report needs one " +
                "collected as 'Difference' (a comparison between two Assessments), which this scope did " +
                "not collect.");
        }

        var meta = ScopeCodec.ReadDifference(assessment.ScopeJson, KindName);
        var text = new System.Text.StringBuilder();
        text.AppendLine($"Comparing {meta.FromAssessmentId} -> {meta.ToAssessmentId}");
        text.AppendLine(
            $"  Words: {meta.FromWordCount} vs {meta.ToWordCount}, {meta.SharedWordCount} shared, " +
            $"{assessment.Words.Count} changed");
        if (meta.TokeniserMismatch) text.AppendLine("  WARNING: " + meta.TokeniserWarning);
        foreach (var word in assessment.Words.OrderBy(w => w.Word, StringComparer.Ordinal))
            text.AppendLine($"    {word.Word}: {word.Outcome}");
        return new RenderedReport(KindName, text.ToString());
    }
}
