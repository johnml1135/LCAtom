using System;
using System.Linq;
using SIL.LCModel;
using SIL.Motif.Projection;

namespace SIL.Motif.Host.Analysis;

/// <summary>Reads and shapes the manual side of the project analysis aggregate without invoking PanGloss.</summary>
public static class ManualAnalysisProjectionQuery
{
    public static AnalysisAggregateProjection Read(LcmCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        return Build(AnalysisAggregateReader.Read(cache));
    }

    public static AnalysisAggregateProjection Build(AnalysisAggregateResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.HasAssessment)
        {
            throw new ArgumentException(
                "A manual analysis projection cannot discard a recorded Assessment.", nameof(response));
        }

        var wordForms = response.WordForms
            .Where(wordForm => wordForm.ManualAnalyses.Count > 0)
            .OrderBy(wordForm => wordForm.Form, StringComparer.Ordinal)
            .ThenBy(wordForm => wordForm.WordformGuid, StringComparer.Ordinal)
            .Select(wordForm =>
            {
                var manualAnalyses = wordForm.ManualAnalyses
                    .OrderBy(analysis => analysis.ContentDigest, StringComparer.Ordinal)
                    .Select(analysis => new ApprovedAnalysisView(
                        analysis.ContentDigest,
                        analysis.MorphBreakdown,
                        analysis.Occurrences
                            .Select(occurrence => new AnalysisOccurrenceView(
                                occurrence.SegmentGuid,
                                occurrence.AnalysisIndex))
                            .ToList()))
                    .ToList();

                return new WordFormAnalysisView(
                    wordForm.WordformGuid,
                    wordForm.Form,
                    manualAnalyses);
            })
            .ToList();

        return new AnalysisAggregateProjection(
            response.DescribeAssessmentState(string.Empty, string.Empty),
            wordForms);
    }
}
