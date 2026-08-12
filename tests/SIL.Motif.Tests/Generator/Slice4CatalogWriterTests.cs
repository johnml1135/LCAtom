using SIL.Motif.Generator;
using SIL.Motif.Generator.Emit;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// Exercises slice 4's emission mechanism end to end, writing into a throwaway temp directory — the
/// slice-4 analogue of <c>GeneratedCatalogWriterTests</c>/<c>Slice2CatalogWriterTests</c>/
/// <c>Slice3CatalogWriterTests</c>. A separate writer/test pair from slices 1-3's rather than a
/// modification of any of them, so their own count assertions are untouched.
/// </summary>
public class Slice4CatalogWriterTests
{
    [Fact]
    public void WriteAll_RealModel_WritesThreeFiles_ForTheOneIntegerEnumField()
    {
        var model = MotifModelLoader.Load();
        var tempRoot = Path.Combine(Path.GetTempPath(), "motif-emit-slice4-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var written = Slice4CatalogWriter.WriteAll(model, tempRoot);

            // WfiWordform.SpellingStatus: one Operations file, one Snapshotting file, one shared g.cs.
            Assert.Equal(3, written.Count);

            foreach (var file in written)
            {
                var fullPath = Path.Combine(tempRoot, file.RelativePath);
                Assert.True(File.Exists(fullPath), $"Expected {fullPath} to exist.");
            }

            var operationFile = Read(tempRoot, written, "WfiWordformSpellingStatus.g.cs");
            Assert.Contains("\"analysis/wfiWordform/setSpellingStatus\"", operationFile);
            // Both derived verbs, no exception (ADR 0022 decision 1). Clear writes the zero member.
            Assert.Contains("\"analysis/wfiWordform/clearSpellingStatus\"", operationFile);
            Assert.Contains("SpellingStatus = 0", operationFile);
            Assert.Contains("GetRequiredInteger", operationFile);
            Assert.Contains("AllowedValues = { 0, 1, 2 }", operationFile);

            var snapshotterFile = Read(tempRoot, written, "WfiWordformSnapshotter.g.cs");
            Assert.Contains("class WfiWordformSnapshotter", snapshotterFile);
            Assert.Contains("IntegerFieldAlternatives.ToAlternatives", snapshotterFile);

            var snapshotFieldsFile = Read(tempRoot, written, "SnapshotFields.Generated4.g.cs");
            Assert.Contains("WfiWordformSpellingStatus", snapshotFieldsFile);
            Assert.Contains("\"analysis/wfiWordform/spellingStatus\"", snapshotFieldsFile);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void WriteAll_RunTwiceAgainstTheSameModel_IsIdempotent()
    {
        var model = MotifModelLoader.Load();
        var tempRoot = Path.Combine(Path.GetTempPath(), "motif-emit-slice4-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var firstRun = Slice4CatalogWriter.WriteAll(model, tempRoot)
                .ToDictionary(f => f.RelativePath, f => File.ReadAllText(Path.Combine(tempRoot, f.RelativePath)));

            var secondRun = Slice4CatalogWriter.WriteAll(model, tempRoot)
                .ToDictionary(f => f.RelativePath, f => File.ReadAllText(Path.Combine(tempRoot, f.RelativePath)));

            Assert.Equal(firstRun.Keys.OrderBy(k => k), secondRun.Keys.OrderBy(k => k));
            foreach (var key in firstRun.Keys)
                Assert.Equal(firstRun[key], secondRun[key]);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string Read(string tempRoot, IReadOnlyList<GeneratedCatalogWriter.WrittenFile> written, string fileNameSuffix)
    {
        var match = written.Single(f => f.RelativePath.EndsWith("/" + fileNameSuffix, StringComparison.Ordinal));
        return File.ReadAllText(Path.Combine(tempRoot, match.RelativePath));
    }
}
