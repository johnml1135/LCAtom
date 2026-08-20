using SIL.Motif.Host.Parser;
using SIL.LCModel;

namespace SIL.Motif.Host.Analysis;

/// <summary>
/// Builds an <see cref="AnalysisAggregateResponse"/> from a live <see cref="LcmCache"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This never parses, and cannot by construction</b> (ADR 0038 decision 5): there is no
/// <c>PanGlossParser</c> reference anywhere in this type, and its only way to learn what the parser said
/// is a <see cref="StoredAssessment"/> the caller already produced by some other, slow, explicitly-invoked
/// step. Omit it and the response still returns every word form's manual side — the test suite — with
/// <see cref="WordFormAnalysisAggregate.AutomaticAnalyses"/> <c>null</c> throughout and
/// <see cref="AnalysisAggregateResponse.Assessment"/> <c>null</c>, exactly ADR 0038's "a consequence worth
/// having: this unblocks the aggregate from Assessment storage."
/// </para>
/// <para>
/// <b>Human approval is read through the supported route, not raw evaluations.</b>
/// <c>IWfiWordform.HumanApprovedAnalyses</c> is liblcm's own property for this — verified by decompiling
/// the shipped assembly (<c>SIL.LCModel.DomainImpl.WfiWordform.AnalysesWithHumanEvaluation</c>): it walks
/// <c>AnalysesOC</c> and keeps those where <c>GetAgentOpinion(DefaultUserAgent) == Opinions.approves</c>.
/// This mirrors <c>FieldWorks/Src/LexText/ParserCore/ParserReport.cs</c>'s own
/// <c>NumUserApprovedAnalysesMissing</c> computation, so Motif's notion of "approved" is FieldWorks' own,
/// not a reimplementation. The same decompilation confirms <c>IWfiWordform.HumanAndParserAgree</c> is
/// <c>throw new NotImplementedException()</c> in this liblcm version — corroborating ADR 0038's own
/// consequences section, which cites exactly that.
/// </para>
/// <para>
/// <b>Joining the automatic side to a word form is by surface text</b>, via
/// <c>AssessedWord.Word</c> — <c>ParserReport.ParseReports</c> is itself keyed by word string in
/// FieldWorks, one level looser than a GUID, so this join is at least as precise as FieldWorks' own. A
/// word form whose text does not appear in the stored assessment's word list gets
/// <c>AutomaticAnalyses = null</c> ("not covered"), never an empty list ("covered, parser found nothing") —
/// see <see cref="WordFormAnalysisAggregate.AutomaticAnalyses"/>.
/// </para>
/// <para>
/// <b><see cref="AnalysisAggregateResponse.UnanalysedReach"/> is folded from the same pass</b>, not a
/// second traversal: every word form with zero manually-approved analyses and a
/// <c>SpellingStatus</c> other than <c>Incorrect</c> contributes to
/// <see cref="UnanalysedReachFigure.UnanalysedCount"/>, and contributes to
/// <see cref="UnanalysedReachFigure.ParsedCount"/> too when its automatic side is non-empty. Only present
/// when an Assessment was supplied, for the same reason the automatic side of every
/// <see cref="WordFormAnalysisAggregate"/> is <c>null</c> without one: whether the grammar parses a given
/// word form is not knowable without a parser run already on record.
/// </para>
/// </remarks>
public static class AnalysisAggregateReader
{
    /// SpellingStatus's Incorrect member (0=Undecided;1=Correct;2=Incorrect); only this value is excluded.
    private const int IncorrectSpellingStatus = 2;

    /// <param name="cache">The live project to read. Never mutated and never saved.</param>
    /// <param name="assessment">
    /// A parser run already produced elsewhere, paired with the corpus it covered — omit when no
    /// Assessment is on record. This method never produces one itself.
    /// </param>
    public static AnalysisAggregateResponse Read(LcmCache cache, StoredAssessment? assessment = null)
    {
        if (cache is null) throw new ArgumentNullException(nameof(cache));

        var byWord = assessment?.Report.Words.ToDictionary(w => w.Word, StringComparer.Ordinal);
        var objects = assessment is null ? null : cache.ServiceLocator.GetInstance<ICmObjectRepository>();

        var wordformRepository = cache.ServiceLocator.GetInstance<IWfiWordformRepository>();
        var wordforms = new List<WordFormAnalysisAggregate>();
        var unanalysedCount = 0;
        var unanalysedParsedCount = 0;

        foreach (var wordform in wordformRepository.AllInstances())
        {
            var form = wordform.Form.VernacularDefaultWritingSystem?.Text ?? string.Empty;

            var manual = wordform.HumanApprovedAnalyses
                .Select(BuildApproved)
                .ToList();

            IReadOnlyList<AutomaticAnalysis>? automatic = null;
            if (byWord is not null && byWord.TryGetValue(form, out var assessedWord))
            {
                automatic = assessedWord.Analyses
                    .Select(a => BuildAutomatic(a, objects!))
                    .ToList();
            }

            wordforms.Add(new WordFormAnalysisAggregate(
                WordformGuid: wordform.Guid.ToString(),
                Form: form,
                ManualAnalyses: manual,
                AutomaticAnalyses: automatic));

            if (manual.Count == 0 && wordform.SpellingStatus != IncorrectSpellingStatus)
            {
                unanalysedCount++;
                if (automatic is { Count: > 0 }) unanalysedParsedCount++;
            }
        }

        var provenance = assessment is null
            ? null
            : new AnalysisAssessmentProvenance(
                assessment.Corpus.CorpusId, assessment.Corpus.Sha256, assessment.Report.GrammarSourceSha256);

        var unanalysedReach = assessment is null
            ? null
            : new UnanalysedReachFigure(unanalysedCount, unanalysedParsedCount);

        return new AnalysisAggregateResponse(wordforms, provenance, unanalysedReach);
    }

    private static ApprovedAnalysis BuildApproved(IWfiAnalysis analysis)
    {
        var bundles = analysis.MorphBundlesOS
            .Select(mb => new MorphBundleContent(
                mb.MorphRA?.Guid.ToString(),
                mb.MsaRA?.Guid.ToString(),
                mb.InflTypeRA?.Guid.ToString()))
            .ToList();

        var occurrences = analysis.OccurrencesInTexts
            .SelectMany(segment => segment.GetOccurrencesOfAnalysis(analysis, int.MaxValue, includeChildren: true))
            .Select(occurrence => new AnalysisOccurrenceLink(
                occurrence.Segment.Guid.ToString(),
                occurrence.Index))
            .OrderBy(occurrence => occurrence.SegmentGuid, StringComparer.Ordinal)
            .ThenBy(occurrence => occurrence.AnalysisIndex)
            .ToList();

        return new ApprovedAnalysis(
            ContentDigest: AnalysisContent.ComputeDigest(bundles),
            MorphBreakdown: DescribeManualBreakdown(analysis),
            Occurrences: occurrences);
    }

    private static string DescribeManualBreakdown(IWfiAnalysis analysis)
    {
        var parts = analysis.MorphBundlesOS.Select(mb =>
        {
            // The real form, or the guessed-root fallback the parser stores when there is no MoForm (ParseFiler.cs).
            var text = mb.MorphRA?.Form?.VernacularDefaultWritingSystem?.Text
                       ?? mb.Form?.VernacularDefaultWritingSystem?.Text
                       ?? "?";
            var msa = mb.MsaRA?.InterlinearAbbr;
            return string.IsNullOrEmpty(msa) ? text : $"{text}/{msa}";
        });

        return string.Join("-", parts);
    }

    private static AutomaticAnalysis BuildAutomatic(ParsedAnalysis analysis, ICmObjectRepository objects)
    {
        var parts = analysis.MorphemeGuids.Select(guid => DescribeMorpheme(guid, objects));
        return new AutomaticAnalysis(analysis.IdentityDigest, string.Join("-", parts));
    }

    private static string DescribeMorpheme(string morphemeGuid, ICmObjectRepository objects)
    {
        // docs/research/2026-08-05-what-is-a-proper-word-analysis.md: unresolved keys are shown raw, never dropped.
        if (Guid.TryParse(morphemeGuid, out var guid)
            && objects.TryGetObject(guid, out var obj)
            && obj is IMoMorphSynAnalysis msa
            && !string.IsNullOrEmpty(msa.InterlinearAbbr))
        {
            return msa.InterlinearAbbr;
        }

        return morphemeGuid;
    }
}
