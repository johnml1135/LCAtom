using SIL.Motif.Generator;
using SIL.Motif.Generator.Emit;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// Exercises slice 3's emission mechanism end to end, writing into a throwaway temp
/// directory — the slice-3 analogue of <c>GeneratedCatalogWriterTests</c>/<c>Slice2CatalogWriterTests</c>.
/// A separate writer/test pair from slices 1 and 2's rather than a modification of either, so their
/// own count assertions (14, 8) are untouched.
/// </summary>
public class Slice3CatalogWriterTests
{
    [Fact]
    public void WriteAll_RealModel_WritesOneHundredTwentyNineFiles_EachContainingItsOwnKindStrings()
    {
        var model = MotifModelLoader.Load();
        var tempRoot = Path.Combine(Path.GetTempPath(), "motif-emit-slice3-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var written = Slice3CatalogWriter.WriteAll(model, tempRoot);

            // 78 Operations files (23 basic, 25 rel/atomic, 30 rel/col|seq) + 50 Snapshotting + 1 shared g.cs.
            Assert.Equal(129, written.Count);

            foreach (var file in written)
            {
                var fullPath = Path.Combine(tempRoot, file.RelativePath);
                Assert.True(File.Exists(fullPath), $"Expected {fullPath} to exist.");
            }

            var boolFile = Read(tempRoot, written, "PhSegmentRuleDisabled.g.cs");
            Assert.Contains("\"grammar/phSegmentRule/setDisabled\"", boolFile);
            Assert.Contains("\"grammar/phSegmentRule/clearDisabled\"", boolFile);
            Assert.Contains("GetRequiredBoolean", boolFile);

            var altFile = Read(tempRoot, written, "CmPossibilityAbbreviation.g.cs");
            Assert.Contains("\"lists/cmPossibility/setAbbreviation\"", altFile);
            Assert.Contains("\"lists/cmPossibility/clearAbbreviation\"", altFile);

            var atomicFile = Read(tempRoot, written, "LexSenseMorphoSyntaxAnalysis.g.cs");
            Assert.Contains("\"lexical/lexSense/setMorphoSyntaxAnalysis\"", atomicFile);
            Assert.Contains("\"lexical/lexSense/clearMorphoSyntaxAnalysis\"", atomicFile);

            var collectionFile = Read(tempRoot, written, "PhNCSegmentsSegments.g.cs");
            Assert.Contains("\"grammar/phNCSegments/addRefSegments\"", collectionFile);
            Assert.Contains("\"grammar/phNCSegments/removeRefSegments\"", collectionFile);
            // move is deliberately deferred, matching slice 2's rel/col|seq emitter.
            Assert.DoesNotContain("\"grammar/phNCSegments/moveSegments\"", collectionFile);

            // A class that needs both sibling snapshotter files (a basic field and a rel field).
            var moCompoundRuleSnapshotter = Read(tempRoot, written, "MoCompoundRuleSnapshotter.g.cs");
            Assert.Contains("class MoCompoundRuleSnapshotter", moCompoundRuleSnapshotter);
            var moCompoundRuleRelationsSnapshotter = Read(tempRoot, written, "MoCompoundRuleRelationsSnapshotter.g.cs");
            Assert.Contains("class MoCompoundRuleRelationsSnapshotter", moCompoundRuleRelationsSnapshotter);

            var snapshotFieldsFile = Read(tempRoot, written, "SnapshotFields.Generated3.g.cs");
            Assert.Contains("PhSegmentRuleDisabled", snapshotFieldsFile);
            Assert.Contains("LexSenseMorphoSyntaxAnalysis", snapshotFieldsFile);
            Assert.Contains("PhNCSegmentsSegments", snapshotFieldsFile);
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
        var tempRoot = Path.Combine(Path.GetTempPath(), "motif-emit-slice3-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var firstRun = Slice3CatalogWriter.WriteAll(model, tempRoot)
                .ToDictionary(f => f.RelativePath, f => File.ReadAllText(Path.Combine(tempRoot, f.RelativePath)));

            var secondRun = Slice3CatalogWriter.WriteAll(model, tempRoot)
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
