namespace SIL.Motif.Generator;

/// <summary>
/// Locates repo-relative files the generator needs by default — namely the manifest — by walking up
/// from the running assembly until <c>Motif.sln</c> is found. Mirrors
/// <c>tests/SIL.Motif.Tests/TestFixtures/TestLangProjFixture.cs</c>'s own convention deliberately,
/// so there is exactly one "find the repo root" trick in this codebase rather than two guesses at
/// the same layout.
/// </summary>
public static class RepoPaths
{
    public static string FindRepoRoot(string? startDirectory = null)
    {
        var dir = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Motif.sln")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new GeneratorException(
                "Could not locate the Motif repo root — no Motif.sln found above the running assembly.");
    }

    public static string DefaultManifestPath(string? startDirectory = null) =>
        Path.Combine(FindRepoRoot(startDirectory), "manifest", "liblcm-inventory.tsv");

    /// <summary>
    /// Hand-written descriptions, one per authorable field, required for every emitted kind
    /// (ADR 0023 decision 5 as amended). A companion file rather than a manifest column: it grows per
    /// family as families ship, where the inventory is a fixed 898-row projection of the model.
    /// </summary>
    public static string DefaultDescriptionsPath(string? startDirectory = null) =>
        Path.Combine(FindRepoRoot(startDirectory), "manifest", "kind-descriptions.tsv");
}
