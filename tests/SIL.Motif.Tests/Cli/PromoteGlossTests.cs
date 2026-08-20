using System;
using System.IO;
using SIL.Motif.Cli;
using SIL.Motif.Cli.Store;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Tests.TestFixtures;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// <c>promote-gloss</c> — the only sanctioned route from the Motif store into the language project
/// (ADR 0036 decision 2): a <c>lexical/lexSense/setGloss</c> operation whose value is
/// evidenced by a stored corpus, carrying that corpus's origin forward as non-hashed provenance so a
/// licence obligation (e.g. CC-BY-SA attribution) is never lost between the evidence and the entry it
/// justified.
/// </summary>
public sealed class PromoteGlossTests : IDisposable
{
    private readonly string _storeDir =
        Path.Combine(Path.GetTempPath(), "motif-promote-gloss-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storeDir)) Directory.Delete(_storeDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void SeedCorpus(string corpusId = "wiki-testlang", string? licence = "CC-BY-SA-4.0") =>
        Assert.Equal(
            0,
            CorpusCommands.AddCorpus(
                _storeDir, corpusId, "Testlang Wikipedia dump", uri: "https://example.invalid/dump",
                licence: licence, capabilities: LicenceCapabilities.Unknown(),
                tokeniser: "whitespace-and-punctuation", tokeniserVersion: "1", tokeniserNotes: null).ExitCode);

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
    public void PromoteGloss_AddsTheOperation_AndRecordsTheCorpusOriginAsProvenance()
    {
        SeedCorpus();
        var target = CanonicalId.Mint().Value;
        Assert.Equal(0, Commands.New(_storeDir, "d", null).ExitCode);

        var result = Commands.PromoteGloss(_storeDir, "d", target, "en", "a promoted gloss", "wiki-testlang");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("promoted from corpus 'wiki-testlang'", result.Output);
        Assert.Contains("CC-BY-SA-4.0", result.Output);
        DraftRationale.Author(
            _storeDir, "d", "Promote a reviewed corpus gloss", "Use the attested corpus analysis in the language project.");

        var finalize = Commands.Finalize(_storeDir, "d");
        Assert.Equal(0, finalize.ExitCode);
        var digest = ExtractIntentDigest(finalize.Output);
        var objectJson = File.ReadAllText(new ProposalStore(_storeDir).ObjectPath(digest));
        Assert.Contains("\"promotions\"", objectJson);
        Assert.Contains("wiki-testlang", objectJson);
        Assert.Contains("CC-BY-SA-4.0", objectJson);

        // The digest must equal what the SAME operation hashes to with no extensions at all.
        var envelope = ProposalJsonParser.Parse(objectJson);
        var bareProposal = new Proposal(envelope.ContractVersions, envelope.ProposalId, envelope.Requires, envelope.Operations);
        Assert.Equal(digest, IntentDigest.Compute(bareProposal));
    }

    [Fact]
    public void PromoteGloss_SurfacesInShow_ForAReviewerWhoNeverOpensTheStoreFiles()
    {
        SeedCorpus();
        var target = CanonicalId.Mint().Value;
        Assert.Equal(0, Commands.New(_storeDir, "d", null).ExitCode);
        Assert.Equal(0, Commands.PromoteGloss(_storeDir, "d", target, "en", "a promoted gloss", "wiki-testlang").ExitCode);
        DraftRationale.Author(
            _storeDir, "d", "Promote an attested gloss", "Carry the corpus provenance into the finalized proposal.");
        var finalize = Commands.Finalize(_storeDir, "d");
        var proposalId = ExtractProposalId(finalize.Output);

        var showText = Commands.Show(_storeDir, proposalId);
        var showJson = Commands.ShowJson(_storeDir, proposalId);

        Assert.Contains("wiki-testlang", showText.Output);
        Assert.Contains("wiki-testlang", showJson.Output);
    }

    [Fact]
    public void PromoteGloss_UnknownCorpus_Refuses_AndAddsNoOperation()
    {
        Assert.Equal(0, Commands.New(_storeDir, "d", null).ExitCode);
        var draftPath = new ProposalStore(_storeDir).DraftPath("d");
        var before = File.ReadAllText(draftPath);

        var result = Commands.PromoteGloss(
            _storeDir, "d", CanonicalId.Mint().Value, "en", "text", "no-such-corpus");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(draftPath));
    }

    [Fact]
    public void PromoteGloss_UnknownDocumentWithinAKnownCorpus_Refuses()
    {
        SeedCorpus();

        Assert.Equal(0, Commands.New(_storeDir, "d", null).ExitCode);
        var result = Commands.PromoteGloss(
            _storeDir, "d", CanonicalId.Mint().Value, "en", "text", "wiki-testlang", "no-such-document");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("no document", result.Output, StringComparison.OrdinalIgnoreCase);
    }
}
