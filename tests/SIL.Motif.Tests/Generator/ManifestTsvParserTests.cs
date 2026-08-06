using SIL.Motif.Generator;
using SIL.Motif.Generator.Manifest;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// Parses both a small synthetic fixture (to pin the exact quoting/escaping/CRLF rules) and the
/// real, read-only <c>manifest/liblcm-inventory.tsv</c> (899 lines: header + 898 rows,
/// manifest/README.md).
/// </summary>
public class ManifestTsvParserTests
{
    private static string WriteFixture(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "motif-tests", Guid.NewGuid().ToString("N") + ".tsv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private const string Header =
        "\"Class\"\t\"Base\"\t\"Abstract\"\t\"Scope\"\t\"ScopeReason\"\t\"Field\"\t\"Kind\"\t\"Sig\"\t\"Card\"\t" +
        "\"HcReferenced\"\t\"Construct\"\t\"Group\"\t\"Classification\"\t\"ComparisonClass\"\t\"Verbs\"\t" +
        "\"HcReachable\"\t\"AssessPoisonsCache\"\t\"EnumValues\"\t\"Rationale\"";

    [Fact]
    public void Parse_Fixture_UnquotesAndUnescapesEmbeddedQuotes()
    {
        var row =
            "\"LexSense\"\t\"CmObject\"\t\"false\"\t\"in\"\t\"reason\"\t\"Gloss\"\t\"basic\"\t\"MultiUnicode\"\t\"\"\t" +
            "\"name-referenced\"\t\"lexSense\"\t\"lexical\"\t\"semantic-operation\"\t\"unordered\"\t\"set|clear\"\t" +
            "\"yes\"\t\"no\"\t\"\"\t\"Has \"\"embedded\"\" quotes.\"";
        var path = WriteFixture(Header + "\r\n" + row + "\r\n");

        var rows = ManifestTsvParser.Parse(path);

        Assert.Single(rows);
        var parsed = rows[0];
        Assert.Equal("LexSense", parsed.Class);
        Assert.Equal("Gloss", parsed.Field);
        Assert.Equal("", parsed.Card); // basic rows carry no card, blank in the TSV
        Assert.Equal("Has \"embedded\" quotes.", parsed.Rationale);
    }

    [Fact]
    public void Parse_Fixture_RejectsWrongColumnCount()
    {
        var path = WriteFixture(Header + "\r\n" + "\"OnlyOneColumn\"\r\n");

        var ex = Assert.Throws<GeneratorException>(() => ManifestTsvParser.Parse(path));
        Assert.Contains("19 columns", ex.Message);
    }

    [Fact]
    public void Parse_Fixture_RejectsMissingHeader()
    {
        var path = WriteFixture("\"NotTheRealHeader\"\r\n");

        Assert.Throws<GeneratorException>(() => ManifestTsvParser.Parse(path));
    }

    [Fact]
    public void Parse_RealManifest_Has898Rows()
    {
        var rows = ManifestTsvParser.Parse(RepoPaths.DefaultManifestPath());
        Assert.Equal(898, rows.Count);
    }

    [Fact]
    public void Parse_RealManifest_HasNoDuplicateClassFieldKeys()
    {
        var rows = ManifestTsvParser.Parse(RepoPaths.DefaultManifestPath());
        var keys = rows.Select(r => (r.Class, r.Field)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Parse_RealManifest_Has494InScopeRows()
    {
        // docs/plan-motif.md, MOT-2/MOT-3: "494 in-scope of 898".
        var rows = ManifestTsvParser.Parse(RepoPaths.DefaultManifestPath());
        Assert.Equal(494, rows.Count(r => r.Scope == "in"));
    }
}
