using System.Text.Json.Serialization;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;

namespace SIL.Motif.Host.Assess;

/// <summary>
/// Reads the <c>ScopeJson</c> shape <c>TrialJobHandler</c> writes, just enough to rebuild a
/// <see cref="BatchAnalysis"/> for <see cref="GrammarCoverageFigure.Compute(BatchAnalysis, CorpusDescriptor, string)"/>.
/// </summary>
internal static class ScopeJsonReader
{
    /// <exception cref="ReportRefusalException">The scope's engine name or per-word limit cannot be read.</exception>
    public static (ParserEngine Engine, int? PerWordLimitMs) ReadEngineAndLimit(string scopeJson, string reportKind)
    {
        ScopeWireShape? shape;
        try
        {
            shape = System.Text.Json.JsonSerializer.Deserialize<ScopeWireShape>(scopeJson);
        }
        catch (System.Text.Json.JsonException)
        {
            shape = null;
        }
        if (shape is null)
            throw new ReportRefusalException(reportKind, "the Assessment's recorded scope could not be read.");
        if (string.IsNullOrWhiteSpace(shape.Engine) || !PanGlossEngineNames.TryParse(shape.Engine, out var engine))
        {
            throw new ReportRefusalException(reportKind,
                $"the Assessment's recorded scope names an engine ('{shape.Engine}') this report cannot interpret.");
        }
        return (engine, (int)shape.PerWordLimitMs);
    }

    private sealed record ScopeWireShape(
        [property: JsonPropertyName("engine")] string? Engine,
        [property: JsonPropertyName("perWordLimitMs")] long PerWordLimitMs);
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
        if (!string.Equals(assessment.Kind, "ParseTime", StringComparison.Ordinal))
        {
            throw new ReportRefusalException(KindName,
                $"this Assessment is a '{assessment.Kind}' measurement; a coverage report needs one collected " +
                "as 'ParseTime' (per-word outcome under a timing pass), which this scope did not collect.");
        }

        var (engine, perWordLimitMs) = ScopeJsonReader.ReadEngineAndLimit(assessment.ScopeJson, KindName);
        var words = assessment.Words.Select((word, index) => new WordAnalysis(
            index, word.Word, 0, StoredWordOutcome.Parse(word.Outcome, KindName), string.Empty)).ToList();
        var batch = new BatchAnalysis(words, engine, perWordLimitMs, string.Empty, Array.Empty<string>());
        var corpus = new CorpusDescriptor(assessment.CorpusId, assessment.CorpusWords, assessment.CorpusSha256);
        var figure = GrammarCoverageFigure.Compute(batch, corpus, assessment.GrammarSourceSha256);
        return new RenderedReport(KindName, figure.Describe(assessment.CorpusSha256, assessment.GrammarSourceSha256));
    }
}

/// <summary>
/// Correctness against manual analysis: of the words this scope declared as carrying a human-decided
/// analysis, how many the parser still produces at least one analysis for. Rendered from a <c>Correctness</c>
/// Assessment's own stored words and analyses — a word counts as analysed here when it carries at least one
/// stored <see cref="ParsedAnalysis"/> row, never by comparing digests across PanGloss's and FieldWorks'
/// separate identity schemes, which <c>AutomaticAnalysis</c>'s own remarks document as not comparable.
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
        if (!string.Equals(assessment.Kind, "Correctness", StringComparison.Ordinal))
        {
            throw new ReportRefusalException(KindName,
                $"this Assessment is a '{assessment.Kind}' measurement; a correctness report needs one " +
                "collected as 'Correctness' (parses checked against manual analysis), which this scope did " +
                "not collect.");
        }

        var (engine, perWordLimitMs) = ScopeJsonReader.ReadEngineAndLimit(assessment.ScopeJson, KindName);
        var words = assessment.Words.Select((word, index) => new WordAnalysis(
            index, word.Word, 0,
            word.Analyses.Count > 0 ? WordOutcome.Analysed : WordOutcome.NoAnalysis,
            word.Analyses.Count > 0 ? word.Analyses[0].IdentityDigest : string.Empty)).ToList();
        var batch = new BatchAnalysis(words, engine, perWordLimitMs, string.Empty, Array.Empty<string>());
        var corpus = new CorpusDescriptor(assessment.CorpusId, assessment.CorpusWords, assessment.CorpusSha256);
        var figure = GrammarCoverageFigure.Compute(batch, corpus, assessment.GrammarSourceSha256);
        var text = "Correctness against manual analysis — " +
            figure.Describe(assessment.CorpusSha256, assessment.GrammarSourceSha256);
        return new RenderedReport(KindName, text);
    }
}

/// <summary>
/// Reads the ScopeJson shape <c>CompareCommands</c> writes for a <c>Difference</c> Assessment — the two
/// input Assessments' ids and word counts, and the tokeniser warning, none of which travels on
/// <see cref="ReportableAssessment"/> itself.
/// </summary>
internal static class DifferenceScopeJsonReader
{
    /// <exception cref="ReportRefusalException">The recorded scope could not be read.</exception>
    public static DifferenceScopeWireShape Read(string scopeJson, string reportKind)
    {
        DifferenceScopeWireShape? shape;
        try
        {
            shape = System.Text.Json.JsonSerializer.Deserialize<DifferenceScopeWireShape>(scopeJson);
        }
        catch (System.Text.Json.JsonException)
        {
            shape = null;
        }
        return shape ?? throw new ReportRefusalException(
            reportKind, "the Assessment's recorded comparison scope could not be read.");
    }

    internal sealed record DifferenceScopeWireShape(
        [property: JsonPropertyName("fromAssessmentId")] string FromAssessmentId,
        [property: JsonPropertyName("toAssessmentId")] string ToAssessmentId,
        [property: JsonPropertyName("fromWordCount")] int FromWordCount,
        [property: JsonPropertyName("toWordCount")] int ToWordCount,
        [property: JsonPropertyName("sharedWordCount")] int SharedWordCount,
        [property: JsonPropertyName("tokeniserMismatch")] bool TokeniserMismatch,
        [property: JsonPropertyName("tokeniserWarning")] string? TokeniserWarning);
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
        if (!string.Equals(assessment.Kind, "Difference", StringComparison.Ordinal))
        {
            throw new ReportRefusalException(KindName,
                $"this Assessment is a '{assessment.Kind}' measurement; a difference report needs one " +
                "collected as 'Difference' (a comparison between two Assessments), which this scope did " +
                "not collect.");
        }

        var meta = DifferenceScopeJsonReader.Read(assessment.ScopeJson, KindName);
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
