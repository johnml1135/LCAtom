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
/// The CLI's first Layer-1 authoring surface (ADR 0009 decision 1): <c>compose-author-lexeme-form</c>
/// resolves one authored intent against a live project into <see cref="AuthorLexemeFormComposer"/>'s
/// operations, appends them to a draft the agent never enumerated by hand, and carries the intent
/// forward as non-hashed provenance rather than dropping it or folding it into the digest.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class ComposeAuthorLexemeFormTests
{
    private readonly SeededProject _seed;
    private readonly string _fwDataPath;
    private readonly string _storeDir;

    public ComposeAuthorLexemeFormTests(PristineProjectFixture pristine)
    {
        _seed = pristine.Seed;
        using var scratch = pristine.NewScratch();
        _fwDataPath = scratch.ProjectId.Path;
        var scratchRoot = Path.GetDirectoryName(Path.GetDirectoryName(_fwDataPath))!;
        _storeDir = Path.Combine(scratchRoot, ".motif-store");
    }

    private string IntentJson(string entry, bool includeGloss) =>
        JsonSerializer.Serialize(includeGloss
            ? new
            {
                entry,
                morphType = CanonicalId.FromGuid(MoMorphTypeTags.kguidMorphStem).Value,
                ws = "fr",
                text = "zzComposedForm",
                sense = CanonicalId.FromGuid(_seed.FirstSenseId).Value,
                glossWs = "en",
                glossText = "a composed gloss",
            }
            : (object)new
            {
                entry,
                morphType = CanonicalId.FromGuid(MoMorphTypeTags.kguidMorphStem).Value,
                ws = "fr",
                text = "zzComposedForm",
            });

    [Fact]
    public void ComposeAuthorLexemeForm_AppendsTheResolvedOperations_NotOneTheAgentEnumerated()
    {
        var entryId = CanonicalId.FromGuid(_seed.FirstEntryId).Value;
        Assert.Equal(0, Commands.New(_storeDir, "d", null).ExitCode);

        var result = Commands.ComposeAuthorLexemeForm(
            _storeDir, "d", _fwDataPath, IntentJson(entryId, includeGloss: true));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("2 operation(s) added", result.Output);
        DraftRationale.Author(
            _storeDir, "d", "Author a lexeme form", "Create the missing lexeme analysis and its attested gloss.");

        var finalize = Commands.Finalize(_storeDir, "d");
        Assert.Equal(0, finalize.ExitCode);
        var proposalId = ExtractProposalId(finalize.Output);

        var showJson = Commands.ShowJson(_storeDir, proposalId);
        Assert.Equal(0, showJson.ExitCode);
        Assert.Contains(LexEntryLexemeFormOperationKinds.CreateLexemeForm, showJson.Output);
        Assert.Contains(LexicalSenseOperationKinds.SetGloss, showJson.Output);
    }

    [Fact]
    public void ComposeAuthorLexemeForm_RecordsTheIntentAsNonHashedProvenance_NeverInTheDigest()
    {
        var entryId = CanonicalId.FromGuid(_seed.FirstEntryId).Value;
        Assert.Equal(0, Commands.New(_storeDir, "d", null).ExitCode);
        Assert.Equal(
            0,
            Commands.ComposeAuthorLexemeForm(_storeDir, "d", _fwDataPath, IntentJson(entryId, includeGloss: false))
                .ExitCode);
        DraftRationale.Author(
            _storeDir, "d", "Author a lexeme form", "Preserve the composer provenance in the finalized intent.");

        var finalize = Commands.Finalize(_storeDir, "d");
        Assert.Equal(0, finalize.ExitCode);
        var proposalId = ExtractProposalId(finalize.Output);
        var digest = ExtractIntentDigest(finalize.Output);

        var objectPath = new ProposalStore(_storeDir).ObjectPath(digest);
        var objectJson = File.ReadAllText(objectPath);
        Assert.Contains("\"composers\"", objectJson);
        Assert.Contains("\"AuthorLexemeForm\"", objectJson);

        // Must equal the same operations' digest with no extensions at all, proving provenance never entered it.
        var envelope = ProposalJsonParser.Parse(objectJson);
        var bareProposal = new Proposal(envelope.ContractVersions, envelope.ProposalId, envelope.Requires, envelope.Operations);
        Assert.Equal(digest, IntentDigest.Compute(bareProposal));
        Assert.NotNull(envelope.Extensions);
    }

    [Fact]
    public void Reopen_CarriesTheComposerProvenanceForward_RatherThanSilentlyDroppingIt()
    {
        var entryId = CanonicalId.FromGuid(_seed.FirstEntryId).Value;
        Assert.Equal(0, Commands.New(_storeDir, "d", null).ExitCode);
        Assert.Equal(
            0,
            Commands.ComposeAuthorLexemeForm(_storeDir, "d", _fwDataPath, IntentJson(entryId, includeGloss: false))
                .ExitCode);
        DraftRationale.Author(
            _storeDir, "d", "Author a lexeme form", "Create the lexical analysis before adding the related manual edit.");
        var firstFinalize = Commands.Finalize(_storeDir, "d");
        var proposalId = ExtractProposalId(firstFinalize.Output);

        Assert.Equal(0, Commands.Reopen(_storeDir, "amend", proposalId).ExitCode);
        // Amend with an ordinary hand-authored operation too, so the draft mixes composed and manual content.
        var secondTarget = CanonicalId.FromGuid(_seed.SecondSenseId).Value;
        Assert.Equal(0, Commands.AddSetGloss(_storeDir, "amend", secondTarget, "en", "manually added").ExitCode);
        var amendFinalize = Commands.Finalize(_storeDir, "amend");
        Assert.Equal(0, amendFinalize.ExitCode);

        var amendedDigest = ExtractIntentDigest(amendFinalize.Output);
        var objectJson = File.ReadAllText(new ProposalStore(_storeDir).ObjectPath(amendedDigest));
        Assert.Contains("\"composers\"", objectJson);
        Assert.Contains("\"AuthorLexemeForm\"", objectJson);
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
}
