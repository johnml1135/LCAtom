using SIL.LCAtom.Host.LcmUtils;
using SIL.LCModel;
using Xunit;

namespace SIL.LCAtom.Tests;

/// <summary>
/// Stage A proof: a real FieldWorks <c>.fwdata</c> loads headless through
/// <see cref="FwDataProjectLoader"/>, and the loaded project exposes a non-empty lexicon via the
/// public <see cref="ILexEntryRepository"/>.
/// </summary>
public class ProjectLoadTests
{
    [Fact]
    public void OpeningRealProject_ReportsProjectNameAndPositiveEntryCount()
    {
        var sourceProjectFolder = FindTestLangProjSource();

        // Never mutate the shared fixture: copy to a temp directory this test owns and cleans up.
        var tempRoot = Path.Combine(Path.GetTempPath(), "SIL.LCAtom.Tests", Guid.NewGuid().ToString("N"));
        var projectFolder = Path.Combine(tempRoot, "TestLangProj");
        Directory.CreateDirectory(projectFolder);
        try
        {
            CopyDirectory(sourceProjectFolder, projectFolder);
            var fwDataPath = Path.Combine(projectFolder, "TestLangProj.fwdata");
            Assert.True(File.Exists(fwDataPath), $"Copied project should contain the .fwdata file at '{fwDataPath}'.");

            var loader = new FwDataProjectLoader();
            using var cache = loader.LoadCache(fwDataPath);

            Assert.False(string.IsNullOrWhiteSpace(cache.ProjectId.Name));

            var entryRepo = cache.ServiceLocator.GetInstance<ILexEntryRepository>();
            Assert.True(entryRepo.Count > 0, "Expected the real TestLangProj fixture to contain lexical entries.");
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // best-effort cleanup; a locked native handle should not fail the test
            }
        }
    }

    /// <summary>
    /// Locates the read-only <c>FieldWorks/TestLangProj</c> fixture as a sibling of this repo
    /// checkout (see docs/build-stages.md, "Environment (verified)").
    /// </summary>
    private static string FindTestLangProjSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.Name != "LCAtom")
        {
            dir = dir.Parent;
        }

        if (dir?.Parent is null)
        {
            throw new InvalidOperationException(
                "Could not locate the LCAtom repo root from the test assembly location; " +
                "expected the FieldWorks/TestLangProj fixture as a sibling checkout.");
        }

        var testLangProj = Path.Combine(dir.Parent.FullName, "FieldWorks", "TestLangProj");
        if (!Directory.Exists(testLangProj))
        {
            throw new InvalidOperationException(
                $"Expected the read-only test project at '{testLangProj}' (see docs/build-stages.md).");
        }

        return testLangProj;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
        }
    }
}
