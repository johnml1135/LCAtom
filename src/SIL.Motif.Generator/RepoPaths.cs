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

    /// <summary>
    /// Descriptions harvested out of FieldWorks' compiled help by the dev-time <c>harvest-help</c> command.
    /// Checked in so the <c>.chm</c> — and the Windows-only tool that opens it — stay out of the build.
    /// </summary>
    public static string DefaultHelpDescriptionsPath(string? startDirectory = null) =>
        Path.Combine(FindRepoRoot(startDirectory), "manifest", "fieldworks-help-descriptions.tsv");

    /// <summary>
    /// What the model says about ordering, for every in-scope row whose <c>ComparisonClass</c> claims order
    /// carries meaning (<see cref="Ordering.OrderingEvidenceHarvester"/>).
    /// </summary>
    public static string DefaultOrderingEvidencePath(string? startDirectory = null) =>
        Path.Combine(FindRepoRoot(startDirectory), "manifest", "ordering-evidence.tsv");

    /// <summary>
    /// Which release of liblcm and FieldWorks the cited descriptions were copied from
    /// (<see cref="Descriptions.Harvest.SourcePins"/>).
    /// </summary>
    public static string DefaultSourcePinsPath(string? startDirectory = null) =>
        Path.Combine(FindRepoRoot(startDirectory), "manifest", "source-pins.tsv");
}
