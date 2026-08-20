using System;
using SIL.Motif.Host.Analysis;
using SIL.Motif.Projection.Rendering;
using Xunit;

namespace SIL.Motif.Tests.Projection;

public sealed class AnalysisAggregateProjectionTests
{
    [Fact]
    public void ManualProjectionIsDeterministicAndCarriesEveryTextFigureInJson()
    {
        var response = new AnalysisAggregateResponse(
            new[]
            {
                Wordform("wordform-guid-0002", "zeta", Analysis("analysis-digest-0002", "zeta/V", 2)),
                Wordform(
                    "wordform-guid-0001", "alpha",
                    Analysis("analysis-digest-0009", "alpha/N", 4),
                    new ApprovedAnalysis(
                        "analysis-digest-0001",
                        "alpha/ADJ",
                        new[] { new AnalysisOccurrenceLink("segment-guid-0001", AnalysisIndex: 3) })),
                Wordform("wordform-guid-0003", "unanalysed"),
            },
            Assessment: null);

        var projection = ManualAnalysisProjectionQuery.Build(response);
        var text = CommandTextRenderer.Render(projection);
        var json = ProjectionJson.Serialize(projection);

        Assert.Equal(2, projection.WordFormCount);
        Assert.Equal("wordform-guid-0001", projection.WordForms[0].WordformGuid);
        Assert.Equal("analysis-digest-0001", projection.WordForms[0].ManualAnalyses[0].ContentDigest);
        Assert.Equal(2, projection.WordForms[0].ManualAnalysisCount);
        Assert.Equal("segment-guid-0001", projection.WordForms[0].ManualAnalyses[0].Occurrences[0].SegmentGuid);
        Assert.DoesNotContain("unanalysed", text);
        Assert.Contains("segment-guid-0001[3]", text);
        Assert.Contains("\"analysisIndex\": 3", json);
        Assert.Contains("No assessment is on record", text);
        Assert.DoesNotContain("automaticAnalyses", json);
        Assert.Equal(text, CommandTextRenderer.Render(ManualAnalysisProjectionQuery.Build(response)));
        FigureAudit.AssertEveryTextFigureAppearsInJson(text, json);
    }

    [Fact]
    public void ManualProjectionRejectsAnAssessmentInsteadOfDiscardingItsAutomaticSide()
    {
        var response = new AnalysisAggregateResponse(
            Array.Empty<WordFormAnalysisAggregate>(),
            new AnalysisAssessmentProvenance("corpus", "corpus-sha", "grammar-sha"));

        var error = Assert.Throws<ArgumentException>(() => ManualAnalysisProjectionQuery.Build(response));

        Assert.Contains("manual", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static WordFormAnalysisAggregate Wordform(
        string guid,
        string form,
        params ApprovedAnalysis[] analyses) =>
        new(guid, form, analyses, AutomaticAnalyses: null);

    private static ApprovedAnalysis Analysis(string digest, string breakdown, int occurrenceCount) =>
        new(
            digest,
            breakdown,
            Enumerable.Range(0, occurrenceCount)
                .Select(index => new AnalysisOccurrenceLink("segment-guid-0002", index))
                .ToArray());
}
