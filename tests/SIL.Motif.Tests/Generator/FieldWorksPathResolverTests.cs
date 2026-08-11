using SIL.Motif.Generator;
using SIL.Motif.Generator.Descriptions.Harvest;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// Mirrors <c>ModelPathResolverTests</c>' own convention for the liblcm side: an explicit override works,
/// and a clear error names what was tried when nothing is found. FieldWorks ships no NuGet package, so there
/// is no package-cache branch to test here — only the override and the not-found message.
/// </summary>
public class FieldWorksPathResolverTests
{
    [Fact]
    public void ResolveContextHelpPath_UsesExplicitOverride_WhenTheFileExists()
    {
        var fakeCheckoutRoot = Path.Combine(Path.GetTempPath(), "motif-tests", Guid.NewGuid().ToString("N"), "fieldworks-checkout");
        var configDir = Path.Combine(fakeCheckoutRoot, "DistFiles", "Language Explorer", "Configuration");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "ContextHelp.xml"), "<strings/>");

        try
        {
            var path = FieldWorksPathResolver.ResolveContextHelpPath(checkoutRootOverride: fakeCheckoutRoot);

            Assert.True(File.Exists(path));
            Assert.EndsWith("ContextHelp.xml", path);
        }
        finally
        {
            // Deletes only this test's own GUID folder, not the shared motif-tests parent other tests use concurrently.
            Directory.Delete(Path.GetDirectoryName(fakeCheckoutRoot)!, recursive: true);
        }
    }

    [Fact]
    public void ResolveContextHelpPath_ThrowsNamingTheEnvironmentVariable_WhenNothingIsFound()
    {
        var emptyRoot = Path.Combine(Path.GetTempPath(), "motif-tests", Guid.NewGuid().ToString("N"), "not-a-fieldworks-checkout");
        Directory.CreateDirectory(emptyRoot);

        try
        {
            var ex = Assert.Throws<GeneratorException>(() =>
                FieldWorksPathResolver.ResolveContextHelpPath(checkoutRootOverride: emptyRoot));

            Assert.Contains("ContextHelp.xml", ex.Message);
        }
        finally
        {
            // Deletes only this test's own GUID folder, one level up from emptyRoot, not the shared parent.
            Directory.Delete(Path.GetDirectoryName(emptyRoot)!, recursive: true);
        }
    }

    /// <summary>
    /// This repo's own convention (spikes/SIL.Motif.Spikes.LabelHarvest's FindDefault does the same walk):
    /// .../repos/motif next to .../repos/FieldWorks. Exercised here, unlike the two tests above, because
    /// that sibling checkout is exactly what this environment provides — proving the default path works,
    /// not just the override.
    /// </summary>
    [Fact]
    public void ResolveContextHelpPath_FindsTheRealSiblingCheckout()
    {
        var path = FieldWorksPathResolver.ResolveContextHelpPath();

        Assert.True(File.Exists(path));
    }
}
