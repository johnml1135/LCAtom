using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SIL.Motif.Cli;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Config;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using SIL.Motif.Tests.TestFixtures;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// The trap ADR 0042's Trial amendment names, driven through the real <c>apply</c> verb on a real project:
/// applying promotes the Proposal's candidate Assessment to current, and a sweep of the Proposal's own
/// working Assessments must not take the promoted one with it. Also covers regression gating (ADR 0042
/// decision 5) — off by default, blocking when configured, and an override recorded as a Decision.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class ApplyPromotionGatingAndSweepTests
{
    private const string ProductVersion = "1.0";
    private const string GrammarSha = "sha256:" + "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    private readonly SeededProject _seed;
    private readonly string _fwDataPath;

    public ApplyPromotionGatingAndSweepTests(PristineProjectFixture pristine)
    {
        _seed = pristine.Seed;
        using var scratch = pristine.NewScratch();
        _fwDataPath = scratch.ProjectId.Path;
    }

    [Fact]
    public void ApplyWithPurgeOnDefault_PromotesTheCandidate_AndTheProposalsOtherAssessmentsAreGone()
    {
        var proposalId = FinalizeAndTrial("trap-purge-on", "purge-on test gloss");
        var intentDigest = GetRecord(proposalId).IntentDigest!;
        var candidateId = RecordAssessment(proposalId, intentDigest, "Correctness", ("alpha", true));
        var scratchId = RecordAssessment(proposalId, intentDigest, "ParseTime", ("alpha", true));

        var result = Commands.Apply(_fwDataPath, ProductVersion, proposalId, "tester");
        Assert.Equal(0, result.ExitCode);

        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        var assessments = new AssessmentRepository(database);
        // Asserted after the sweep already ran: the promoted Assessment survives by identity, not by luck.
        Assert.Equal(candidateId, assessments.GetCurrent()!.AssessmentId);
        Assert.Equal(candidateId, assessments.Get(candidateId).AssessmentId);
        Assert.Throws<KeyNotFoundException>(() => assessments.Get(scratchId));
        var remaining = assessments.ListByProposal(CanonicalId.Parse(proposalId));
        Assert.Equal(new[] { candidateId }, remaining.Select(record => record.AssessmentId));
    }

    [Fact]
    public void ApplyWithPurgeOff_LeavesEveryAssessmentReadable()
    {
        WriteConfiguration(gateOnRegression: false, purgeOnApply: false);
        var proposalId = FinalizeAndTrial("trap-purge-off", "purge-off test gloss");
        var intentDigest = GetRecord(proposalId).IntentDigest!;
        var candidateId = RecordAssessment(proposalId, intentDigest, "Correctness", ("alpha", true));
        var scratchId = RecordAssessment(proposalId, intentDigest, "ParseTime", ("alpha", true));

        var result = Commands.Apply(_fwDataPath, ProductVersion, proposalId, "tester");
        Assert.Equal(0, result.ExitCode);

        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        var assessments = new AssessmentRepository(database);
        Assert.Equal(candidateId, assessments.GetCurrent()!.AssessmentId);
        Assert.Equal(candidateId, assessments.Get(candidateId).AssessmentId);
        Assert.Equal(scratchId, assessments.Get(scratchId).AssessmentId);
        Assert.Equal(2, assessments.ListByProposal(CanonicalId.Parse(proposalId)).Count);
    }

    [Fact]
    public void RegressionByDefault_DoesNotGate_ApplyProceedsWithoutAnOverride()
    {
        var proposalId = FinalizeAndTrial("regression-default", "regression default gloss");
        var intentDigest = GetRecord(proposalId).IntentDigest!;
        PromotePrevious(("alpha", true));
        var candidateId = RecordAssessment(proposalId, intentDigest, "Correctness", ("alpha", false));

        var result = Commands.Apply(_fwDataPath, ProductVersion, proposalId, "tester");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(candidateId, GetCurrentAssessmentId());
    }

    [Fact]
    public void RegressionGated_WithoutAnOverride_RefusesAndNamesTheRegression()
    {
        WriteConfiguration(gateOnRegression: true, purgeOnApply: true);
        var proposalId = FinalizeAndTrial("regression-gated-refuse", "regression gated refuse gloss");
        var intentDigest = GetRecord(proposalId).IntentDigest!;
        var previousId = PromotePrevious(("alpha", true));
        RecordAssessment(proposalId, intentDigest, "Correctness", ("alpha", false));

        var result = Commands.Apply(_fwDataPath, ProductVersion, proposalId, "tester");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("regression", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alpha", result.Output, StringComparison.Ordinal);
        // Nothing moved: the previous Assessment is still current, and the Proposal was never applied.
        Assert.Equal(previousId, GetCurrentAssessmentId());
        Assert.Equal("proposed", GetRecord(proposalId).Status);
    }

    [Fact]
    public void RegressionGated_WithAnOverrideComment_AppliesAndRecordsTheOverrideAsADecision()
    {
        WriteConfiguration(gateOnRegression: true, purgeOnApply: true);
        var proposalId = FinalizeAndTrial("regression-gated-override", "regression gated override gloss");
        var intentDigest = GetRecord(proposalId).IntentDigest!;
        PromotePrevious(("alpha", true));
        var candidateId = RecordAssessment(proposalId, intentDigest, "Correctness", ("alpha", false));

        var result = Commands.Apply(
            _fwDataPath, ProductVersion, proposalId, "tester", overrideComment: "known false positive, checked by hand");

        Assert.Equal(0, result.ExitCode);
        var record = GetRecord(proposalId);
        Assert.Equal("applied", record.Status);
        Assert.NotNull(record.Decision);
        Assert.Equal("human", record.Decision!.ActorType);
        Assert.Equal("tester", record.Decision.ActorId);
        Assert.Contains("known false positive", record.Decision.Comment, StringComparison.Ordinal);
        Assert.Contains("alpha", record.Decision.Comment, StringComparison.Ordinal);
        Assert.Equal(candidateId, GetCurrentAssessmentId());
    }

    // --- helpers ---

    private string FinalizeAndTrial(string draftName, string newGloss)
    {
        var senseGuid = _seed.FirstSenseId;
        var wsTag = NewLangProjFixture.AnalysisTag;
        var canonicalId = CanonicalId.FromGuid(senseGuid);

        Assert.Equal(0, Commands.New(_fwDataPath, ProductVersion, draftName, null).ExitCode);
        Assert.Equal(0,
            Commands.AddSetGloss(_fwDataPath, ProductVersion, draftName, canonicalId.Value, wsTag, newGloss).ExitCode);
        DraftRationale.Author(_fwDataPath, draftName, "Clarify the first sense gloss", "Exercise apply's promotion and sweep.");

        var finalizeResult = Commands.Finalize(_fwDataPath, ProductVersion, draftName);
        Assert.Equal(0, finalizeResult.ExitCode);
        var proposalId = ExtractProposalId(finalizeResult.Output);

        Assert.Equal(0, DryRunJobRunner.Run(_fwDataPath, ProductVersion, proposalId).ExitCode);
        return proposalId;
    }

    private ProposalRecord GetRecord(string proposalId)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        return new ProposalRepository(database).Get(CanonicalId.Parse(proposalId));
    }

    private string? GetCurrentAssessmentId()
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        return new AssessmentRepository(database).GetCurrent()?.AssessmentId;
    }

    /// <summary>Records and promotes an unrelated Assessment: the project's prior current one.</summary>
    private string PromotePrevious(params (string Word, bool Analysed)[] words)
    {
        var id = RecordAssessment(proposalId: null, intentDigest: null, "Correctness", words);
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        new AssessmentRepository(database).PromoteToCurrent(id);
        return id;
    }

    private string RecordAssessment(
        string? proposalId, string? intentDigest, string kind, params (string Word, bool Analysed)[] words)
    {
        var corpus = Selection.Create("test", words.Select(w => w.Word));
        var assessmentId = CanonicalId.Mint("assessment/").Value;
        var assessedWords = words
            .Select(w => new AssessedWord(
                w.Word, w.Analysed ? "analysed" : "no-analysis",
                w.Analysed
                    ? new[] { new ParsedAnalysis(null, Array.Empty<string>(), 0, "digest:" + w.Word) }
                    : Array.Empty<ParsedAnalysis>()))
            .ToArray();

        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        new AssessmentRepository(database).Record(new NewAssessmentRecord(
            AssessmentId: assessmentId,
            ProposalId: proposalId is null ? null : CanonicalId.Parse(proposalId),
            ProposalIntentDigest: intentDigest,
            Assessor: "pangloss",
            Kind: kind,
            ScopeJson: """{"engine":"fast","perWordLimitMs":1000}""",
            ScopeDigest: "sha256:" + new string('a', 64),
            TokeniserName: "none",
            TokeniserVersion: "1",
            BaselineToken: "{}",
            Selection: corpus,
            OutcomeDigest: "sha256:" + new string('b', 64),
            SemanticDigest: "sha256:" + new string('c', 64),
            GrammarSourceSha256: GrammarSha,
            ModelFingerprint: "model",
            Pipeline: "pipeline",
            DiagnosticCount: 0,
            Words: assessedWords));
        return assessmentId;
    }

    private void WriteConfiguration(bool gateOnRegression, bool purgeOnApply)
    {
        var path = ProjectConfigurationReader.PathFor(
            new ProjectLocator(_fwDataPath, Path.GetFileNameWithoutExtension(_fwDataPath)));
        File.WriteAllText(path, ProjectConfigurationFile.Render(
            new ProjectConfiguration(
                new[] { AssessmentScopeConfiguration.Default() }, gateOnRegression, purgeOnApply)));
    }

    private static string ExtractProposalId(string finalizeOutput)
    {
        const string marker = "-> Proposal ";
        var start = finalizeOutput.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{marker}' in finalize output: {finalizeOutput}");
        start += marker.Length;
        var end = finalizeOutput.IndexOf(' ', start);
        Assert.True(end > start, $"Could not parse proposalId from finalize output: {finalizeOutput}");
        return finalizeOutput.Substring(start, end - start);
    }
}
