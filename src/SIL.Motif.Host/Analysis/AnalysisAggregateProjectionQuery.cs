using SIL.Motif.Contract.Responses;
using System;
using System.Linq;
using SIL.LCModel;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Projection;

namespace SIL.Motif.Host.Analysis;

/// <summary>Reads and shapes the project analysis aggregate without invoking PanGloss.</summary>
public static class AnalysisAggregateProjectionQuery
{
    public static AnalysisAggregateProjection Read(
        LcmCache cache,
        StoredAssessment assessment,
        string currentSelectionSha256,
        string currentGrammarSourceSha256)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(assessment);
        return Build(
            AnalysisAggregateReader.Read(cache, assessment),
            currentSelectionSha256,
            currentGrammarSourceSha256);
    }

    public static AnalysisAggregateProjection Build(
        AnalysisAggregateResponse response,
        string currentSelectionSha256,
        string currentGrammarSourceSha256)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Assessment is { } assessment)
        {
            Sha256Value.RequireCanonical(currentSelectionSha256, nameof(currentSelectionSha256));
            Sha256Value.RequireCanonical(currentGrammarSourceSha256, nameof(currentGrammarSourceSha256));
            Sha256Value.RequireCanonical(assessment.SelectionSha256, nameof(assessment.SelectionSha256));
            Sha256Value.RequireCanonical(
                assessment.GrammarSourceSha256,
                nameof(assessment.GrammarSourceSha256));
        }

        var wordForms = response.WordForms
            .Where(wordForm => wordForm.ManualAnalyses.Count > 0)
            .OrderBy(wordForm => wordForm.Form, StringComparer.Ordinal)
            .ThenBy(wordForm => wordForm.WordformGuid, StringComparer.Ordinal)
            .Select(wordForm => new WordFormAnalysisView(
                wordForm.WordformGuid,
                wordForm.Form,
                wordForm.ManualAnalyses
                    .OrderBy(analysis => analysis.ContentDigest, StringComparer.Ordinal)
                    .Select(analysis => new ApprovedAnalysisView(
                        analysis.ContentDigest,
                        analysis.MorphBreakdown,
                        analysis.Occurrences
                            .Select(occurrence => new AnalysisOccurrenceView(
                                occurrence.SegmentGuid,
                                occurrence.AnalysisIndex))
                            .ToList()))
                    .ToList(),
                wordForm.AutomaticAnalyses?
                    .OrderBy(analysis => analysis.ContentDigest, StringComparer.Ordinal)
                    .Select(analysis => new AutomaticAnalysisView(
                        analysis.ContentDigest,
                        analysis.MorphBreakdown))
                    .ToList()))
            .ToList();

        var reach = response.UnanalysedReach is null
            ? null
            : new UnanalysedReachView(
                response.UnanalysedReach.UnanalysedCount,
                response.UnanalysedReach.ParsedCount,
                response.UnanalysedReach.Describe());

        return new AnalysisAggregateProjection(
            response.DescribeAssessmentState(currentSelectionSha256, currentGrammarSourceSha256),
            wordForms,
            reach);
    }
}
