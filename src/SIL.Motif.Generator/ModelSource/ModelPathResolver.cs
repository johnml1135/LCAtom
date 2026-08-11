using System.Reflection;

namespace SIL.Motif.Generator.ModelSource;

/// <summary>
/// Finds <c>MasterLCModel.xml</c> without requiring a liblcm source checkout.
/// <c>SIL.LCModel.csproj</c> packs the file into the NuGet package under
/// <c>contentFiles/</c>, but not in the conventional <c>contentFiles/{lang}/{tfm}/</c> layout NuGet
/// auto-flows into a <c>PackageReference</c> consumer's build output — so it has to be located
/// directly in the package cache rather than found alongside this assembly.
/// </summary>
/// <remarks>
/// Verified fact, not re-derived here: for the pinned version, the real path is
/// <c>~/.nuget/packages/sil.lcmodel/{version}/contentFiles/MasterLCModel.xml</c>. NuGet always
/// lower-cases the package id when laying out the on-disk cache folder, regardless of the casing
/// written in a <c>PackageReference</c>, which is why <see cref="PackageId"/> below is lower-case.
/// </remarks>
public static class ModelPathResolver
{
    private const string PackageId = "sil.lcmodel";

    /// <summary>
    /// Resolves the package-cache path, falling back to a liblcm checkout only if
    /// <paramref name="libLcmCheckoutRoot"/> (or the <c>MOTIF_LIBLCM_CHECKOUT</c> environment
    /// variable) is set and the fallback file actually exists there. The fallback is accepted, never
    /// required — nothing in this repository's CI sets it, so the package-cache path is what CI
    /// always exercises.
    /// </summary>
    /// <param name="packagesRootOverride">Overrides the resolved NuGet package cache root. Exists so
    /// tests can exercise the "NUGET_PACKAGES unset" and "checkout fallback" branches without
    /// mutating process-wide environment variables that other tests might read concurrently.
    /// Production callers should omit this and let it fall through to <c>NUGET_PACKAGES</c> or the
    /// user-profile default.</param>
    /// <param name="libLcmCheckoutRoot">Overrides the checkout-fallback root for the same reason.</param>
    public static ModelPathResult Resolve(string? packagesRootOverride = null, string? libLcmCheckoutRoot = null)
    {
        var version = ReadPinnedPackageVersion();

        var packagesRoot = packagesRootOverride
            ?? Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        var packageCachePath = Path.Combine(packagesRoot, PackageId, version, "contentFiles", "MasterLCModel.xml");
        if (File.Exists(packageCachePath))
            return new ModelPathResult(packageCachePath, ModelPathSource.NuGetPackageCache);

        // Fallback only, never required (see summary above). Path convention per ADR 0014's own citation of the file in the liblcm repository: `liblcm/src/SIL.LCModel/MasterLCModel.xml`.
        var checkoutRoot = libLcmCheckoutRoot ?? Environment.GetEnvironmentVariable("MOTIF_LIBLCM_CHECKOUT");
        string? checkoutPath = null;
        if (!string.IsNullOrWhiteSpace(checkoutRoot))
        {
            checkoutPath = Path.Combine(checkoutRoot, "src", "SIL.LCModel", "MasterLCModel.xml");
            if (File.Exists(checkoutPath))
                return new ModelPathResult(checkoutPath, ModelPathSource.LibLcmCheckout);
        }

        throw new GeneratorException(
            $"Could not find MasterLCModel.xml. Tried the NuGet package cache at '{packageCachePath}'" +
            (checkoutPath is null
                ? " and no MOTIF_LIBLCM_CHECKOUT fallback was set."
                : $" and the liblcm checkout fallback at '{checkoutPath}'.") +
            " See docs/plan-motif.md, MOT-2.");
    }

    /// <summary>
    /// Reads the SIL.LCModel version this assembly was built against back out of the
    /// <see cref="AssemblyMetadataAttribute"/> the csproj emits from its own
    /// <c>$(SilLCModelPackageVersion)</c> property — the single source of truth, so the version
    /// string is never duplicated as a separate literal in code
    /// (SIL.Motif.Generator.csproj).
    /// </summary>
    public static string ReadPinnedPackageVersion()
    {
        var attribute = typeof(ModelPathResolver).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SilLCModelPackageVersion");

        return attribute?.Value
            ?? throw new GeneratorException(
                "SilLCModelPackageVersion assembly metadata is missing; check SIL.Motif.Generator.csproj.");
    }
}
