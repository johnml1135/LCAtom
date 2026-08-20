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

    [Fact]
    public void AssessmentProjectionPreservesAutomaticAnalysisStatesAndReachFigures()
    {
        var reach = new UnanalysedReachFigure(UnanalysedCount: 9, ParsedCount: 4);
        var response = new AnalysisAggregateResponse(
            new[]
            {
                new WordFormAnalysisAggregate(
                    "wordform-guid-0003",
                    "zeta",
                    new[] { Analysis("manual-z", "zeta/V", 1) },
                    new[]
                    {
                        new AutomaticAnalysis("automatic-0009", "zeta/V"),
                        new AutomaticAnalysis("automatic-0001", "zeta/N"),
                    }),
                new WordFormAnalysisAggregate(
                    "wordform-guid-0002",
                    "beta",
                    new[] { Analysis("manual-b", "beta/N", 1) },
                    Array.Empty<AutomaticAnalysis>()),
                new WordFormAnalysisAggregate(
                    "wordform-guid-0001",
                    "alpha",
                    new[] { Analysis("manual-a", "alpha/N", 1) },
                    AutomaticAnalyses: null),
                Wordform("wordform-guid-0004", "unanalysed"),
            },
            new AnalysisAssessmentProvenance("corpus-one", "sha256:corpus", "sha256:grammar"),
            reach);

        var projection = AnalysisAggregateProjectionQuery.Build(
            response, "sha256:corpus", "sha256:grammar");
        var text = CommandTextRenderer.Render(projection);
        var json = ProjectionJson.Serialize(projection);

        Assert.Equal(new[] { "alpha", "beta", "zeta" }, projection.WordForms.Select(word => word.Form));
        Assert.Null(projection.WordForms[0].AutomaticAnalyses);
        Assert.Null(projection.WordForms[0].AutomaticAnalysisCount);
        Assert.Empty(projection.WordForms[1].AutomaticAnalyses!);
        Assert.Equal(0, projection.WordForms[1].AutomaticAnalysisCount);
        Assert.Equal(
            new[] { "automatic-0001", "automatic-0009" },
            projection.WordForms[2].AutomaticAnalyses!.Select(analysis => analysis.ContentDigest));
        Assert.Equal(2, projection.WordForms[2].AutomaticAnalysisCount);
        Assert.Equal(9, projection.UnanalysedReach!.UnanalysedCount);
        Assert.Equal(4, projection.UnanalysedReach.ParsedCount);
        Assert.Equal(reach.Describe(), projection.UnanalysedReach.Statement);
        Assert.Contains("automatic analyses: not covered", text);
        Assert.Contains("automatic analyses: 0", text);
        Assert.Contains("automatic analyses: 2", text);
        Assert.Contains(reach.Describe(), text);
        Assert.DoesNotContain("\"automaticAnalyses\"", ProjectionJson.Serialize(projection.WordForms[0]));
        Assert.Contains("\"automaticAnalyses\": []", ProjectionJson.Serialize(projection.WordForms[1]));
        FigureAudit.AssertEveryTextFigureAppearsInJson(text, json);
    }

    [Fact]
    public void AssessmentProjectionRendersCurrentAndStaleProvenanceTruthfully()
    {
        var response = new AnalysisAggregateResponse(
            Array.Empty<WordFormAnalysisAggregate>(),
            new AnalysisAssessmentProvenance(
                "corpus-one",
                "sha256:aaaaaaaaaaaabbbb",
                "sha256:ccccccccccccdddd"));

        var current = AnalysisAggregateProjectionQuery.Build(
            response, "sha256:aaaaaaaaaaaabbbb", "sha256:ccccccccccccdddd");
        var stale = AnalysisAggregateProjectionQuery.Build(
            response, "sha256:9999999999990000", "sha256:8888888888887777");

        Assert.Contains("still describes the current project", current.AssessmentState);
        Assert.Contains("corpus 'corpus-one'", current.AssessmentState);
        Assert.Contains("corpus has changed", stale.AssessmentState);
        Assert.Contains("grammar has changed", stale.AssessmentState);
        Assert.Contains("state that no longer exists", stale.AssessmentState);
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
