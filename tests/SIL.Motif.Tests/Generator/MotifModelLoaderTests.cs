using SIL.Motif.Generator;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// End-to-end exercise of <see cref="MotifModelLoader.Load"/> against the real
/// <c>MasterLCModel.xml</c>, proving the whole pipeline — resolve, parse, join, check, report —
/// fails closed on an injected manifest-side orphan, not just the unit-level
/// <see cref="Join.ModelManifestJoiner"/> tests: an injected extra (Class, Field) key on either side
/// must fail the build with a message naming the key.
/// </summary>
public class MotifModelLoaderTests
{
    [Fact]
    public void Load_DefaultManifest_SucceedsAgainstRealData()
    {
        var loaded = MotifModelLoader.Load();

        Assert.Equal(898, loaded.Rows.Count);
        Assert.Equal("7000072", loaded.Model.Version);
    }

    /// <summary>
    /// One extra, well-formed, 18-column row whose (Class, Field) key exists nowhere in
    /// <c>MasterLCModel.xml</c> — the manifest-side orphan the join must reject. The column count has to
    /// match the manifest exactly (pinned by `Parse_Fixture_RejectsWrongColumnCount`), or the parser
    /// rejects the row on shape before the join ever sees the key, and this test would pass for the wrong
    /// reason: it must fail loudly on the join, not silently on parse shape.
    /// </summary>
    [Fact]
    public void Load_ManifestWithInjectedOrphanRow_FailsClosedNamingTheKey()
    {
        var realManifestPath = RepoPaths.DefaultManifestPath();
        var realText = File.ReadAllText(realManifestPath);

        var injectedRow =
            "\"ZzzSyntheticInjectedClass\"\t\"CmObject\"\t\"false\"\t\"in\"\t\"synthetic test row\"\t" +
            "\"ZzzSyntheticInjectedField\"\t\"basic\"\t\"Unicode\"\t\"\"\t\"no\"\t\"x\"\t\"system\"\t" +
            "\"semantic-operation\"\t\"unordered\"\t\"set|clear\"\t\"no\"\t\"\"\t\"synthetic\"";

        var fixturePath = Path.Combine(Path.GetTempPath(), "motif-tests", Guid.NewGuid().ToString("N") + ".tsv");
        Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
        File.WriteAllText(fixturePath, realText.TrimEnd('\r', '\n') + "\r\n" + injectedRow + "\r\n");

        try
        {
            var ex = Assert.Throws<GeneratorException>(() => MotifModelLoader.Load(manifestPath: fixturePath));
            Assert.Contains("ZzzSyntheticInjectedClass.ZzzSyntheticInjectedField", ex.Message);
            Assert.Contains("manifest but not the model", ex.Message);
        }
        finally
        {
            File.Delete(fixturePath);
        }
    }
}
