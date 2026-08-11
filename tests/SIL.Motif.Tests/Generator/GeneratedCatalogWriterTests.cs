using SIL.Motif.Generator;
using SIL.Motif.Generator.Emit;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// Exercises the actual emission mechanism — checked-in files, produced by running a console command
/// (see <c>Program.cs</c>'s doc comment) — end to end, writing into a throwaway temp directory rather
/// than the real repo tree, so this test can run without depending on — or risking — whatever the
/// checked-in <c>Operations/Generated</c>/<c>Snapshotting/Generated</c> files currently look like.
/// </summary>
public class GeneratedCatalogWriterTests
{
    [Fact]
    public void WriteAll_RealModel_WritesFourteenFiles_EachContainingItsOwnKindStrings()
    {
        var model = MotifModelLoader.Load();
        var tempRoot = Path.Combine(Path.GetTempPath(), "motif-emit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var written = GeneratedCatalogWriter.WriteAll(model, tempRoot);

            // 10 Operations files + 3 per-class Snapshotting files + 1 shared SnapshotFields.Generated.g.cs.
            Assert.Equal(14, written.Count);

            foreach (var file in written)
            {
                var fullPath = Path.Combine(tempRoot, file.RelativePath);
                Assert.True(File.Exists(fullPath), $"Expected {fullPath} to exist.");
            }

            var glossFile = Read(tempRoot, written, "LexSenseGloss.g.cs");
            Assert.Contains("\"lexical/lexSense/setGloss\"", glossFile);
            Assert.Contains("\"lexical/lexSense/clearGloss\"", glossFile);
            Assert.Contains("class LexicalSenseOperationKinds", glossFile);

            var moFormFile = Read(tempRoot, written, "MoFormForm.g.cs");
            Assert.Contains("\"grammar/moForm/setForm\"", moFormFile);
            Assert.Contains("\"grammar/moForm/clearForm\"", moFormFile);

            var boolFile = Read(tempRoot, written, "LexEntryDoNotUseForParsing.g.cs");
            Assert.Contains("\"lexical/lexEntry/setDoNotUseForParsing\"", boolFile);
            Assert.Contains("GetRequiredBoolean", boolFile);

            var snapshotFieldsFile = Read(tempRoot, written, "SnapshotFields.Generated.g.cs");
            Assert.Contains("LexEntryCitationForm", snapshotFieldsFile);
            Assert.DoesNotContain("LexSenseGloss", snapshotFieldsFile); // stays hand-written, not re-emitted

            var lexEntrySnapshotterFile = Read(tempRoot, written, "LexEntrySnapshotter.g.cs");
            Assert.Contains("class LexEntrySnapshotter", lexEntrySnapshotterFile);
            Assert.Contains("BooleanFieldAlternatives.ToAlternatives", lexEntrySnapshotterFile);
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
        var tempRoot = Path.Combine(Path.GetTempPath(), "motif-emit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var firstRun = GeneratedCatalogWriter.WriteAll(model, tempRoot)
                .ToDictionary(f => f.RelativePath, f => File.ReadAllText(Path.Combine(tempRoot, f.RelativePath)));

            var secondRun = GeneratedCatalogWriter.WriteAll(model, tempRoot)
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
