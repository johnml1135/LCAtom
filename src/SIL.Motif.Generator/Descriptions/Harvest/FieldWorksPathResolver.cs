namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// Finds a FieldWorks checkout, the way <c>SIL.Motif.Spikes.LabelHarvest</c>'s <c>Program.FindDefault</c>
/// already does for that harvest, and the way <see cref="ModelSource.ModelPathResolver"/>'s checkout fallback
/// does for liblcm — except FieldWorks ships no NuGet package at all, so a checkout is the only option here,
/// not a fallback.
/// </summary>
public static class FieldWorksPathResolver
{
    private const string EnvironmentVariable = "MOTIF_FIELDWORKS_CHECKOUT";

    /// <summary>
    /// Resolves the FieldWorks checkout root. Checks, in order: an explicit
    /// <paramref name="checkoutRootOverride"/> (tests), the <c>MOTIF_FIELDWORKS_CHECKOUT</c> environment
    /// variable, then the conventional sibling-checkout layout this repo already assumes elsewhere
    /// (<c>.../repos/motif</c> next to <c>.../repos/FieldWorks</c>).
    /// </summary>
    public static string ResolveContextHelpPath(string? checkoutRootOverride = null, string? startDirectory = null) =>
        ResolveFile(
            Path.Combine("DistFiles", "Language Explorer", "Configuration", "ContextHelp.xml"),
            checkoutRootOverride, startDirectory);

    /// <summary>
    /// The compiled help file whose <c>User_Interface/Field_Descriptions</c> pages
    /// <see cref="FieldWorksHelpHarvester"/> reads. Wanted only by the dev-time <c>harvest-help</c> command;
    /// everything else reads the harvested TSV.
    /// </summary>
    public static string ResolveCompiledHelpPath(string? checkoutRootOverride = null, string? startDirectory = null) =>
        ResolveFile(
            Path.Combine("DistFiles", "Helps", "FieldWorks_Language_Explorer_Help.chm"),
            checkoutRootOverride, startDirectory);

    /// <summary>The checkout root itself, for <c>git describe</c> when pinning the source release.</summary>
    public static string ResolveCheckoutRoot(string? checkoutRootOverride = null, string? startDirectory = null)
    {
        var checkoutRoot = checkoutRootOverride
            ?? Environment.GetEnvironmentVariable(EnvironmentVariable)
            ?? FindSiblingCheckout(startDirectory);

        return checkoutRoot ?? throw new GeneratorException(
            "Could not find a FieldWorks checkout. Set MOTIF_FIELDWORKS_CHECKOUT to its root, or check " +
            "one out as a sibling of this repo (.../repos/FieldWorks next to .../repos/motif).");
    }

    private static string ResolveFile(string relativePath, string? checkoutRootOverride, string? startDirectory)
    {
        var checkoutRoot = ResolveCheckoutRoot(checkoutRootOverride, startDirectory);

        var path = Path.Combine(checkoutRoot, relativePath);
        if (!File.Exists(path))
        {
            throw new GeneratorException(
                $"MOTIF_FIELDWORKS_CHECKOUT or the sibling checkout at '{checkoutRoot}' does not contain " +
                $"'{path}'. Is this really a FieldWorks checkout?");
        }

        return path;
    }

    private static string? FindSiblingCheckout(string? startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "motif")))
            dir = dir.Parent;

        if (dir is null) return null;

        var candidate = Path.Combine(dir.FullName, "FieldWorks");
        return Directory.Exists(candidate) ? candidate : null;
    }
}
