using System;
using System.IO;

namespace SIL.LCAtom.Tests.TestFixtures;

/// <summary>
/// Shared helper for tests that need a real, mutable-but-disposable copy of the read-only
/// <c>FieldWorks/TestLangProj</c> fixture. Every test using this must copy to a temp directory it
/// owns and clean up afterward — never mutate the shared source directly.
/// </summary>
internal static class TestLangProjFixture
{
    /// <summary>
    /// Locates the read-only <c>FieldWorks/TestLangProj</c> fixture as a sibling of this repo
    /// checkout (see docs/build-stages.md, "Environment (verified)").
    /// </summary>
    public static string FindSource()
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

    /// <summary>
    /// Copies the fixture into a fresh temp directory this caller owns, and returns the path to the
    /// copied <c>.fwdata</c> file. The caller is responsible for deleting <paramref name="tempRoot"/>
    /// (best-effort; a locked native handle should not fail the test).
    /// </summary>
    public static string CopyToTempAndGetFwDataPath(string tempRoot)
    {
        var projectFolder = Path.Combine(tempRoot, "TestLangProj");
        Directory.CreateDirectory(projectFolder);
        CopyDirectory(FindSource(), projectFolder);

        var fwDataPath = Path.Combine(projectFolder, "TestLangProj.fwdata");
        if (!File.Exists(fwDataPath))
            throw new InvalidOperationException($"Copied project should contain the .fwdata file at '{fwDataPath}'.");

        return fwDataPath;
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
