using System;
using System.IO;

namespace SIL.LCAtom.Cli.Store;

/// <summary>
/// Path layout for LCAtom's minimal git-style files store: immutable content-addressed committed
/// objects, mutable id-keyed review manifests, and mutable local-only drafts. No database — see
/// docs/stage2-change-management.md, S1, and docs/build-stages.md, Stage E.
/// </summary>
/// <remarks>
/// <para>
/// <c>drafts/&lt;draftName&gt;.json</c> is a mutable local draft the CLI builds incrementally across
/// invocations; it never leaves this machine and is deleted once <c>finalize</c> commits it.
/// </para>
/// <para>
/// <b>Keying is content-addressed objects, id-keyed manifest pointer — exactly git's object/ref
/// split.</b> <c>objects/&lt;intentDigest&gt;.json</c> is the immutable committed Change Set document
/// (envelope: contractVersions, changeSetId, requires, operations), keyed by its content hash:
/// write-once, never revisited or overwritten, so amending a Change Set can never mutate a
/// supposedly-immutable object — the previous content's object simply stays on disk under its own
/// digest. <c>manifests/&lt;changeSetId&gt;.json</c> is the mutable review state
/// (status/label/comment/<c>currentIntentDigest</c>) for that Change Set: a movable pointer at the
/// object currently "current" for this id, exactly a git ref pointing at a commit hash. <c>commit</c>
/// (via <c>Commands.Finalize</c>) writes a new object and either creates the manifest (first commit)
/// or moves its pointer (amend, via <c>Commands.Reopen</c> + re-<c>finalize</c>) — see
/// docs/stage2-change-management.md, S1.
/// </para>
/// </remarks>
public sealed class ChangeSetStore
{
    public const string DefaultDirectoryName = ".lcatom";

    public ChangeSetStore(string rootDirectory)
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

    public string DraftPath(string draftName) => Path.Combine(DraftsDirectory, SafeFileName(draftName) + ".json");

    /// <summary>
    /// The immutable committed object's path, keyed by its <c>intentDigest</c> (e.g.
    /// <c>sha256:&lt;64 hex&gt;</c>) — write-once, never revisited. The <c>sha256:</c>-style prefix
    /// before the first <c>:</c> is stripped for the filename (colons are not portable in file
    /// names); the remaining digest text is already filesystem-safe (lowercase hex).
    /// </summary>
    public string ObjectPath(string intentDigest) =>
        Path.Combine(ObjectsDirectory, DigestFileName(intentDigest) + ".json");

    public string ManifestPath(string changeSetId) => Path.Combine(ManifestsDirectory, changeSetId + ".json");

    private static string DigestFileName(string intentDigest)
    {
        if (string.IsNullOrWhiteSpace(intentDigest))
            throw new ArgumentException("An intent digest must not be empty.", nameof(intentDigest));

        var colonIndex = intentDigest.IndexOf(':');
        return colonIndex >= 0 ? intentDigest[(colonIndex + 1)..] : intentDigest;
    }

    /// <summary>
    /// Draft names are user-chosen local labels, not canonical ids; reject path-traversal
    /// characters so a draft name can never escape <see cref="DraftsDirectory"/>.
    /// </summary>
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
