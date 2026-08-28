using System;
using System.IO;
using System.Text.Json;
using SIL.Motif.Cli;
using SIL.Motif.Cli.Store;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// The CLI's first grammar Layer-1 authoring surface, alongside <see cref="ComposeAuthorLexemeFormTests"/>:
/// <c>compose-author-feature-structure</c> resolves one authored intent against a live project into
/// <see cref="SIL.Motif.Runner.Composers.AuthorFeatureStructureComposer"/>'s one operation, and carries
/// the intent forward as non-hashed provenance.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class ComposeAuthorFeatureStructureTests
{
    private readonly SeededProject _seed;
    private readonly string _fwDataPath;
    private readonly string _storeDir;

    public ComposeAuthorFeatureStructureTests(PristineProjectFixture pristine)
    {
        _seed = pristine.Seed;
        using var scratch = pristine.NewScratch();
        _fwDataPath = scratch.ProjectId.Path;
        _storeDir = ProposalStore.ForProject(_fwDataPath).RootDirectory;
    }

    private string FirstMsaId()
    {
        using var cache = new SIL.Motif.Host.LcmUtils.FwDataProjectLoader().LoadCache(_fwDataPath);
        var msaGuid = cache.ServiceLocator.GetInstance<ILexSenseRepository>()
            .GetObject(_seed.FirstSenseId).MorphoSyntaxAnalysisRA.Guid;
        return CanonicalId.FromGuid(msaGuid).Value;
    }

    private static string ExtractProposalId(string output)
    {
        const string marker = "-> Proposal ";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{marker}' in output: {output}");
        start += marker.Length;
        var end = output.IndexOf(' ', start);
        Assert.True(end > start, $"Could not parse proposalId from output: {output}");
        return output.Substring(start, end - start);
    }

    private static string ExtractIntentDigest(string output)
    {
        const string marker = "intentDigest: ";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{marker}' in output: {output}");
        start += marker.Length;
        var end = output.IndexOfAny(new[] { '\r', '\n' }, start);
        Assert.True(end > start, $"Could not parse intentDigest from output: {output}");
        return output.Substring(start, end - start).Trim();
    }

    [Fact]
    public void ComposeAuthorFeatureStructure_AppendsTheResolvedOperation_NotOneTheAgentEnumerated()
    {
        var msaId = FirstMsaId();
        Assert.Equal(0, Commands.New(_storeDir, "d", null).ExitCode);

        var intentJson = JsonSerializer.Serialize(new { msa = msaId });
        var result = Commands.ComposeAuthorFeatureStructure(_storeDir, "d", _fwDataPath, intentJson);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("1 operation(s) added", result.Output);
        DraftRationale.Author(
            _storeDir, "d", "Author a feature structure", "Represent the selected grammatical analysis on the target MSA.");

        var finalize = Commands.Finalize(_storeDir, "d");
        Assert.Equal(0, finalize.ExitCode);
        var proposalId = ExtractProposalId(finalize.Output);

        var showJson = Commands.ShowJson(_storeDir, proposalId);
        Assert.Equal(0, showJson.ExitCode);
        Assert.Contains(MoStemMsaMsFeaturesOperationKinds.CreateMsFeatures, showJson.Output);
    }

    [Fact]
    public void ComposeAuthorFeatureStructure_RecordsTheIntentAsNonHashedProvenance_NeverInTheDigest()
    {
        var msaId = FirstMsaId();
        Assert.Equal(0, Commands.New(_storeDir, "d", null).ExitCode);
        var intentJson = JsonSerializer.Serialize(new { msa = msaId });
        Assert.Equal(0, Commands.ComposeAuthorFeatureStructure(_storeDir, "d", _fwDataPath, intentJson).ExitCode);
        DraftRationale.Author(
            _storeDir, "d", "Author a feature structure", "Preserve the composer provenance in the finalized intent.");

        var finalize = Commands.Finalize(_storeDir, "d");
        Assert.Equal(0, finalize.ExitCode);
        var digest = ExtractIntentDigest(finalize.Output);

        var objectPath = new ProposalStore(_storeDir).ObjectPath(digest);
        var objectJson = File.ReadAllText(objectPath);
        Assert.Contains("\"AuthorFeatureStructure\"", objectJson);

        var envelope = ProposalJsonParser.Parse(objectJson);
        var bareProposal = new Proposal(envelope.ContractVersions, envelope.ProposalId, envelope.Requires, envelope.Operations);
        Assert.Equal(digest, IntentDigest.Compute(bareProposal));
    }
}
