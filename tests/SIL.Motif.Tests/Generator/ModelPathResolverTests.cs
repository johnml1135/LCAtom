using SIL.Motif.Generator;
using SIL.Motif.Generator.ModelSource;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// MOT-2's fact that must keep being true: <c>MasterLCModel.xml</c> is readable from the NuGet
/// package cache with no liblcm source checkout (docs/plan-motif.md MOT-2).
/// </summary>
public class ModelPathResolverTests
{
    [Fact]
    public void Resolve_FindsRealFile_ViaNuGetPackageCache()
    {
        var result = ModelPathResolver.Resolve();

        Assert.Equal(ModelPathSource.NuGetPackageCache, result.Source);
        Assert.True(File.Exists(result.Path), $"Expected '{result.Path}' to exist.");
        Assert.Contains("contentFiles", result.Path);
        Assert.EndsWith("MasterLCModel.xml", result.Path);
    }

    [Fact]
    public void Resolve_UsesExplicitPackagesRootOverride()
    {
        // NUGET_PACKAGES is how CI (and every dotnet build on this machine) actually locates the
        // cache; exercising the override parameter proves the same code path without mutating the
        // real process environment other tests might read concurrently.
        var realRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(
            ModelPathResolver.Resolve().Path)))!; // .../sil.lcmodel/{version}/contentFiles/MasterLCModel.xml -> .../packages
        var packagesRoot = Path.GetDirectoryName(realRoot)!;

        var result = ModelPathResolver.Resolve(packagesRootOverride: packagesRoot);

        Assert.Equal(ModelPathSource.NuGetPackageCache, result.Source);
        Assert.True(File.Exists(result.Path));
    }

    [Fact]
    public void Resolve_FallsBackToLibLcmCheckout_WhenPackageCacheMissingButCheckoutPresent()
    {
        var fakePackagesRoot = Path.Combine(Path.GetTempPath(), "motif-tests", Guid.NewGuid().ToString("N"), "empty-packages");
        Directory.CreateDirectory(fakePackagesRoot);

        var fakeCheckoutRoot = Path.Combine(Path.GetTempPath(), "motif-tests", Guid.NewGuid().ToString("N"), "liblcm-checkout");
        var fakeModelDir = Path.Combine(fakeCheckoutRoot, "src", "SIL.LCModel");
        Directory.CreateDirectory(fakeModelDir);
        File.WriteAllText(Path.Combine(fakeModelDir, "MasterLCModel.xml"), "<EntireModel version=\"1\"/>");

        try
        {
            var result = ModelPathResolver.Resolve(packagesRootOverride: fakePackagesRoot, libLcmCheckoutRoot: fakeCheckoutRoot);

            Assert.Equal(ModelPathSource.LibLcmCheckout, result.Source);
            Assert.True(File.Exists(result.Path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(fakePackagesRoot)!, recursive: true);
            Directory.Delete(Path.GetDirectoryName(fakeCheckoutRoot)!, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ThrowsNamingBothAttemptedPaths_WhenNeitherExists()
    {
        var fakePackagesRoot = Path.Combine(Path.GetTempPath(), "motif-tests", Guid.NewGuid().ToString("N"), "empty-packages");
        Directory.CreateDirectory(fakePackagesRoot);

        try
        {
            var ex = Assert.Throws<GeneratorException>(() =>
                ModelPathResolver.Resolve(packagesRootOverride: fakePackagesRoot, libLcmCheckoutRoot: null));

            Assert.Contains("MasterLCModel.xml", ex.Message);
            Assert.Contains(fakePackagesRoot, ex.Message);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(fakePackagesRoot)!, recursive: true);
        }
    }

    [Fact]
    public void ReadPinnedPackageVersion_MatchesTheCsprojProperty()
    {
        // The version this test expects is the plan's own verified fact (docs/plan-motif.md MOT-2),
        // and is also what SIL.Motif.Generator.csproj's SilLCModelPackageVersion property is pinned
        // to — the same string AssemblyMetadata carries into ReadPinnedPackageVersion.
        Assert.Equal("11.0.0-beta0150", ModelPathResolver.ReadPinnedPackageVersion());
    }
}
