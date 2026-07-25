using System;
using System.IO;

namespace SIL.LCAtom.Cli.Store;

/// <summary>
/// Path layout for LCAtom's minimal git-style files store: immutable committed objects, mutable
/// review manifests, and mutable local-only drafts. No database — see
/// docs/stage2-change-management.md, S1, and docs/build-stages.md, Stage E.
/// </summary>
/// <remarks>
/// <c>drafts/&lt;draftName&gt;.json</c> is a mutable local draft the CLI builds incrementally across
/// invocations; it never leaves this machine and is deleted once <c>finalize</c> commits it.
/// <c>objects/&lt;changeSetId&gt;.json</c> is the immutable committed Change Set document (envelope:
/// contractVersions, changeSetId, requires, operations). <c>manifests/&lt;changeSetId&gt;.json</c> is
/// the mutable review state (status/label/comment/intentDigest) for that Change Set.
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

    public string ObjectPath(string changeSetId) => Path.Combine(ObjectsDirectory, changeSetId + ".json");

    public string ManifestPath(string changeSetId) => Path.Combine(ManifestsDirectory, changeSetId + ".json");

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
