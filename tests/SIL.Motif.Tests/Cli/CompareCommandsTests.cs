using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SIL.Motif.Cli;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Responses;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// Drives the real <c>compare</c> verb end to end against a paired store: two Assessments recorded through
/// <see cref="AssessmentRepository"/>, joined, stored as a third, and read back exactly as any other
/// Assessment would be.
/// </summary>
public sealed class CompareCommandsTests : IDisposable
{
    private const string ProductVersion = "1.0";
    private const string GrammarSha = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-compare-cmd-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;

    public CompareCommandsTests()
    {
        Directory.CreateDirectory(_root);
        _project = Path.Combine(_root, "sample.fwdata");
        File.WriteAllText(_project, "<languageproject/>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void DifferentWordSets_CompareOnTheIntersection_AndReportEachSidesCount()
    {
        var fromId = RecordAssessment("pangloss", "ParseTime", "none", "1", ("alpha", true), ("beta", true));
        var toId = RecordAssessment("pangloss", "ParseTime", "none", "1", ("beta", true), ("gamma", true));

        var result = CompareCommands.Produce(_project, ProductVersion, fromId, toId, asJson: true);

        Assert.Equal(0, result.ExitCode);
        var response = ProjectionJson.Deserialize<CompareResponse>(result.Output)!;
        Assert.Equal(2, response.FromWordCount);
        Assert.Equal(2, response.ToWordCount);
        Assert.Equal(1, response.SharedWordCount);
    }

    [Fact]
    public void DifferingTokenisers_StillCompare_AndTheResponseCarriesTheWarning()
    {
        var fromId = RecordAssessment("pangloss", "ParseTime", "whitespace", "1", ("alpha", true));
        var toId = RecordAssessment("pangloss", "ParseTime", "icu", "74", ("alpha", true));

        var result = CompareCommands.Produce(_project, ProductVersion, fromId, toId, asJson: true);

        Assert.Equal(0, result.ExitCode);
        var response = ProjectionJson.Deserialize<CompareResponse>(result.Output)!;
        Assert.True(response.TokeniserMismatch);
        Assert.NotNull(response.TokeniserWarning);
        Assert.Contains("whitespace", response.TokeniserWarning, StringComparison.Ordinal);
        Assert.Contains("icu", response.TokeniserWarning, StringComparison.Ordinal);
        Assert.Contains("WARNING", response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentKinds_Refuses_NamingBoth()
    {
        var fromId = RecordAssessment("pangloss", "ParseTime", "none", "1", ("alpha", true));
        var toId = RecordAssessment("pangloss", "Correctness", "none", "1", ("alpha", true));

        var result = CompareCommands.Produce(_project, ProductVersion, fromId, toId, asJson: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ParseTime", result.Output, StringComparison.Ordinal);
        Assert.Contains("Correctness", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentAssessors_Refuses_NamingBoth()
    {
        var fromId = RecordAssessment("pangloss", "ParseTime", "none", "1", ("alpha", true));
        var toId = RecordAssessment("hermit-crab", "ParseTime", "none", "1", ("alpha", true));

        var result = CompareCommands.Produce(_project, ProductVersion, fromId, toId, asJson: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("pangloss", result.Output, StringComparison.Ordinal);
        Assert.Contains("hermit-crab", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDifference_IsRecordedAsAnAssessment_AndReadableAfterwardsLikeAnyOther()
    {
        var fromId = RecordAssessment("pangloss", "ParseTime", "none", "1", ("alpha", true), ("beta", true));
        var toId = RecordAssessment("pangloss", "ParseTime", "none", "1", ("alpha", false), ("beta", true));

        var result = CompareCommands.Produce(_project, ProductVersion, fromId, toId, asJson: true);
        Assert.Equal(0, result.ExitCode);
        var response = ProjectionJson.Deserialize<CompareResponse>(result.Output)!;

        AssessmentRecord stored = null!;
        var readResult = ProjectStoreCommand.Run(_project, ProductVersion, (database, _) =>
        {
            stored = new AssessmentRepository(database).Get(response.AssessmentId);
            return new CommandResult(0, string.Empty);
        });
        Assert.Equal(0, readResult.ExitCode);
        Assert.Equal("Difference", stored.Kind);
        Assert.Equal("pangloss", stored.Assessor);
    }

    [Fact]
    public void AWordThatParsedInOneAndNotTheOther_AppearsInTheDifference_AWordThatBehavedIdenticallyDoesNot()
    {
        var fromId = RecordAssessment("pangloss", "ParseTime", "none", "1", ("alpha", true), ("beta", true));
        var toId = RecordAssessment("pangloss", "ParseTime", "none", "1", ("alpha", false), ("beta", true));

        var result = CompareCommands.Produce(_project, ProductVersion, fromId, toId, asJson: true);
        Assert.Equal(0, result.ExitCode);
        var response = ProjectionJson.Deserialize<CompareResponse>(result.Output)!;

        AssessmentRecord stored = null!;
        ProjectStoreCommand.Run(_project, ProductVersion, (database, _) =>
        {
            stored = new AssessmentRepository(database).Get(response.AssessmentId);
            return new CommandResult(0, string.Empty);
        });

        var changedWords = stored.Words!.Select(w => w.Word).ToArray();
        Assert.Contains("alpha", changedWords);
        Assert.DoesNotContain("beta", changedWords);
    }

    private string RecordAssessment(string assessor, string kind, string tokeniserName, string tokeniserVersion,
        params (string Word, bool Analysed)[] words)
    {
        var corpus = CorpusDescriptor.Create("test", words.Select(w => w.Word));
        var assessmentId = CanonicalId.Mint("assessment/").Value;
        var assessedWords = words
            .Select(w => new AssessedWord(
                w.Word, w.Analysed ? "analysed" : "no-analysis",
                w.Analysed
                    ? new[] { new ParsedAnalysis(null, Array.Empty<string>(), 0, "digest") }
                    : Array.Empty<ParsedAnalysis>()))
            .ToArray();

        var result = ProjectStoreCommand.Run(_project, ProductVersion, (database, _) =>
        {
            new AssessmentRepository(database).Record(new NewAssessmentRecord(
                AssessmentId: assessmentId,
                ProposalId: null,
                ProposalIntentDigest: null,
                Assessor: assessor,
                Kind: kind,
                ScopeJson: """{"words":[],"engine":"fast","collect":[],"perWordLimitMs":1000}""",
                ScopeDigest: "sha256:" + new string('a', 64),
                TokeniserName: tokeniserName,
                TokeniserVersion: tokeniserVersion,
                BaselineToken: "{}",
                Corpus: corpus,
                OutcomeDigest: "sha256:" + new string('b', 64),
                SemanticDigest: "sha256:" + new string('c', 64),
                GrammarSourceSha256: GrammarSha,
                ModelFingerprint: "model",
                Pipeline: "pipeline",
                DiagnosticCount: 0,
                Words: assessedWords));
            return new CommandResult(0, string.Empty);
        });
        Assert.Equal(0, result.ExitCode);
        return assessmentId;
    }
}
