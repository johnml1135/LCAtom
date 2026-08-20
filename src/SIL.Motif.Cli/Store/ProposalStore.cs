using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Model;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Projection.Store;

namespace SIL.Motif.Cli.Store;

/// <summary>
/// Path layout for Motif's minimal git-style files store: immutable content-addressed committed
/// objects, mutable id-keyed review manifests, and mutable local-only drafts. No database.
/// </summary>
/// <remarks>
/// <para>
/// <c>drafts/&lt;draftName&gt;.json</c> is a mutable local draft the CLI builds incrementally across
/// invocations; it never leaves this machine and is deleted once <c>finalize</c> commits it.
/// </para>
/// <para>
/// <b>Keying is content-addressed objects, id-keyed manifest pointer — exactly git's object/ref
/// split.</b> <c>objects/&lt;intentDigest&gt;.json</c> is the immutable committed Proposal document
/// (envelope: contractVersions, proposalId, requires, operations), keyed by its content hash:
/// write-once, never revisited or overwritten, so amending a Proposal can never mutate a
/// supposedly-immutable object — the previous content's object simply stays on disk under its own
/// digest. <c>manifests/&lt;proposalId&gt;.json</c> is the mutable review state
/// (status/label/comment/<c>currentIntentDigest</c>) for that Proposal: a movable pointer at the
/// object currently "current" for this id, exactly a git ref pointing at a commit hash. <c>commit</c>
/// (via <c>Commands.Finalize</c>) writes a new object and either creates the manifest (first commit)
/// or moves its pointer (amend, via <c>Commands.Reopen</c> + re-<c>finalize</c>).
/// </para>
/// </remarks>
public sealed class ProposalStore
{
    public const string DefaultDirectoryName = ".motif";

    public ProposalStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Store root directory must not be empty.", nameof(rootDirectory));

        RootDirectory = Path.GetFullPath(rootDirectory);
        DraftsDirectory = Path.Combine(RootDirectory, "drafts");
        ObjectsDirectory = Path.Combine(RootDirectory, "objects");
        ManifestsDirectory = Path.Combine(RootDirectory, "manifests");
    }

    public string RootDirectory { get; }
    public string DraftsDirectory { get; }
    public string ObjectsDirectory { get; }
    public string ManifestsDirectory { get; }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DraftsDirectory);
        Directory.CreateDirectory(ObjectsDirectory);
        Directory.CreateDirectory(ManifestsDirectory);
    }

    /// <summary>
    /// Resolves <paramref name="requested"/>'s finalized prerequisite closure and returns the
    /// Proposals that must prepare a Dry Run scratch, in deterministic topological order. Applied
    /// prerequisites satisfy their dependency branch without requiring its stored objects.
    /// </summary>
    public IReadOnlyList<Proposal> PlanPrerequisites(
        Proposal requested, IReadOnlyCollection<Guid> appliedProposalIds)
    {
        if (requested is null) throw new ArgumentNullException(nameof(requested));
        if (appliedProposalIds is null) throw new ArgumentNullException(nameof(appliedProposalIds));

        return PrerequisiteClosurePlanner.Plan(
            requested,
            id => LoadFinalizedProposal(id).Envelope,
            appliedProposalIds);
    }

    public string DraftPath(string draftName) => Path.Combine(DraftsDirectory, SafeFileName(draftName) + ".json");

    /// <summary>
    /// The immutable committed object's path, keyed by its <c>intentDigest</c> (e.g.
    /// <c>sha256:&lt;64 hex&gt;</c>) — write-once, never revisited. The <c>sha256:</c>-style prefix
    /// before the first <c>:</c> is stripped for the filename (colons are not portable in file
    /// names); the remaining digest text is already filesystem-safe (lowercase hex).
    /// </summary>
    public string ObjectPath(string intentDigest) =>
        Path.Combine(ObjectsDirectory, DigestFileName(intentDigest) + ".json");

    public string ManifestPath(string proposalId) => Path.Combine(ManifestsDirectory, proposalId + ".json");

    /// <summary>
    /// Loads one finalized Proposal and verifies that the lookup id, manifest id, envelope id, and
    /// content-addressed object digest all identify the same immutable content.
    /// </summary>
    internal (ManifestDocument Manifest, string ManifestPath, Proposal Envelope) LoadFinalizedProposal(
        string proposalId)
    {
        var manifestPath = ManifestPath(proposalId);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"Proposal '{proposalId}' not found in store '{RootDirectory}'. Run 'list' to see " +
                "committed proposals.");
        }

        var manifest = JsonSerializer.Deserialize<ManifestDocument>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Manifest file '{manifestPath}' is empty or invalid.");
        if (!string.Equals(manifest.ProposalId, proposalId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Finalized Proposal store identity is inconsistent: lookup id {proposalId} names a " +
                $"manifest whose Proposal id is {manifest.ProposalId}.");
        }

        var objectPath = ObjectPath(manifest.CurrentIntentDigest);
        if (!File.Exists(objectPath))
        {
            throw new InvalidOperationException(
                $"Proposal '{proposalId}' manifest points at intentDigest " +
                $"'{manifest.CurrentIntentDigest}', but no object exists at '{objectPath}' " +
                "(store inconsistency).");
        }

        var proposal = ProposalJsonParser.Parse(File.ReadAllText(objectPath));
        if (!string.Equals(proposal.ProposalId.Value, proposalId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Finalized Proposal store identity is inconsistent: lookup and manifest id " +
                $"{proposalId} point to an envelope whose Proposal id is {proposal.ProposalId.Value}.");
        }

        var actualDigest = IntentDigest.Compute(proposal);
        if (!string.Equals(actualDigest, manifest.CurrentIntentDigest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Proposal {proposalId} object content computes intentDigest '{actualDigest}', not " +
                $"the manifest-bound '{manifest.CurrentIntentDigest}' (store inconsistency).");
        }

        return (manifest, manifestPath, proposal);
    }

    private static string DigestFileName(string intentDigest)
    {
        if (string.IsNullOrWhiteSpace(intentDigest))
            throw new ArgumentException("An intent digest must not be empty.", nameof(intentDigest));

        var colonIndex = intentDigest.IndexOf(':');
        return colonIndex >= 0 ? intentDigest[(colonIndex + 1)..] : intentDigest;
    }

    /// <summary>Blocks path-traversal chars so a name can't escape <see cref="DraftsDirectory"/>.</summary>
    private static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A draft name must not be empty.", nameof(name));

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            if (name.Contains(invalid))
                throw new ArgumentException($"Draft name '{name}' contains an invalid character '{invalid}'.", nameof(name));
        }

        if (name.Contains("..") || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException($"Draft name '{name}' must not contain path separators.", nameof(name));

        return name;
    }
}
