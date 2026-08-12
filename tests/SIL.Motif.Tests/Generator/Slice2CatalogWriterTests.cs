using SIL.Motif.Generator;
using SIL.Motif.Generator.Emit;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// Exercises slice 2's emission mechanism end to end, writing into a throwaway temp directory —
/// the slice-2 analogue of <c>GeneratedCatalogWriterTests</c>. A separate writer/test pair from slice
/// 1's (see <see cref="Slice2CatalogWriter"/>'s remarks for why) rather than a modification of the
/// existing one, so <c>GeneratedCatalogWriterTests</c>' own count assertion is untouched.
/// </summary>
public class Slice2CatalogWriterTests
{
    [Fact]
    public void WriteAll_RealModel_WritesEightFiles_EachContainingItsOwnKindStrings()
    {
        var model = MotifModelLoader.Load();
        var tempRoot = Path.Combine(Path.GetTempPath(), "motif-emit-slice2-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var written = Slice2CatalogWriter.WriteAll(model, tempRoot);

            // 5 Operations files + RelationsSnapshotter for LexEntry/MoForm (2) + shared SnapshotFields.Generated2.g.cs.
            Assert.Equal(8, written.Count);

            foreach (var file in written)
            {
                var fullPath = Path.Combine(tempRoot, file.RelativePath);
                Assert.True(File.Exists(fullPath), $"Expected {fullPath} to exist.");
            }

            var morphTypeFile = Read(tempRoot, written, "MoFormMorphType.g.cs");
            Assert.Contains("\"grammar/moForm/setMorphType\"", morphTypeFile);
            Assert.Contains("\"grammar/moForm/clearMorphType\"", morphTypeFile);
            Assert.DoesNotContain("\"grammar/moForm/moveMorphType\"", morphTypeFile);

            var dialectLabelsFile = Read(tempRoot, written, "LexEntryDialectLabels.g.cs");
            Assert.Contains("\"lexical/lexEntry/addRefDialectLabels\"", dialectLabelsFile);
            Assert.Contains("\"lexical/lexEntry/removeRefDialectLabels\"", dialectLabelsFile);
            // move is deliberately deferred for this slice -- see ReferenceCollectionFieldEmitter's remarks.
            Assert.DoesNotContain("\"lexical/lexEntry/moveDialectLabels\"", dialectLabelsFile);
            Assert.DoesNotContain("class LexEntryDialectLabelsMoveHandler", dialectLabelsFile);

            var doNotPublishInFile = Read(tempRoot, written, "LexEntryDoNotPublishIn.g.cs");
            Assert.Contains("\"lexical/lexEntry/addRefDoNotPublishIn\"", doNotPublishInFile);
            Assert.Contains("\"lexical/lexEntry/removeRefDoNotPublishIn\"", doNotPublishInFile);

            var lexemeFormFile = Read(tempRoot, written, "LexEntryLexemeForm.g.cs");
            Assert.Contains("\"lexical/lexEntry/createLexemeForm\"", lexemeFormFile);
            Assert.Contains("\"lexical/lexEntry/deleteLexemeForm\"", lexemeFormFile);
            Assert.Contains("MoFormConcreteClassSelection", lexemeFormFile);

            var snapshotFieldsFile = Read(tempRoot, written, "SnapshotFields.Generated2.g.cs");
            Assert.Contains("LexEntryDialectLabels", snapshotFieldsFile);
            Assert.Contains("MoFormMorphType", snapshotFieldsFile);
            Assert.Contains("LexEntryLexemeForm", snapshotFieldsFile);

            var lexEntryRelationsFile = Read(tempRoot, written, "LexEntryRelationsSnapshotter.g.cs");
            Assert.Contains("class LexEntryRelationsSnapshotter", lexEntryRelationsFile);
            Assert.Contains("ReferenceCollectionFieldSnapshotting", lexEntryRelationsFile);
            Assert.Contains("ReferenceFieldSnapshotting", lexEntryRelationsFile);

            var moFormRelationsFile = Read(tempRoot, written, "MoFormRelationsSnapshotter.g.cs");
            Assert.Contains("class MoFormRelationsSnapshotter", moFormRelationsFile);
            Assert.Contains("MorphTypeRA", moFormRelationsFile);
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
        var tempRoot = Path.Combine(Path.GetTempPath(), "motif-emit-slice2-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var firstRun = Slice2CatalogWriter.WriteAll(model, tempRoot)
                .ToDictionary(f => f.RelativePath, f => File.ReadAllText(Path.Combine(tempRoot, f.RelativePath)));

            var secondRun = Slice2CatalogWriter.WriteAll(model, tempRoot)
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
