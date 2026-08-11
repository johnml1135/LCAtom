using SIL.Motif.Generator.Descriptions;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// <see cref="KindDescriptionTsvWriter"/> must round-trip through <see cref="KindDescriptionTsvParser"/> —
/// the two are opposite ends of the same dialect (manifest/README.md, "Companion files").
/// </summary>
public class KindDescriptionTsvWriterTests
{
    [Fact]
    public void WrittenRows_RoundTripThroughTheParser()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kind-descriptions-{Guid.NewGuid():N}.tsv");
        var rows = new[]
        {
            new KindDescription("LexSense", "Gloss", "Gloss", "Set the short meaning.", "sourced",
                "liblcm/MasterLCModel.xml", "line 42, <basic id=\"Gloss\"> under <class id=\"LexSense\">"),
            new KindDescription("MoForm", "Form", "Form", "A description with an embedded \"quote\" in it.",
                "unsourced"),
        };

        try
        {
            KindDescriptionTsvWriter.Write(path, rows);
            var parsed = KindDescriptionTsvParser.Parse(path);

            Assert.Equal(2, parsed.Count);
            Assert.Equal(rows[0], parsed[0]);
            Assert.Equal(rows[1], parsed[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_UsesCrlfLineEndingsAndDoubleQuotesEveryField()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kind-descriptions-{Guid.NewGuid():N}.tsv");
        try
        {
            KindDescriptionTsvWriter.Write(path, [new KindDescription("A", "B", "C", "D", "unsourced")]);
            var text = File.ReadAllText(path);

            Assert.StartsWith(
                "\"Class\"\t\"Field\"\t\"Label\"\t\"Description\"\t\"Reviewed\"\t\"Source\"\t\"SourceDetail\"\t\"SourceHash\"\r\n",
                text);
            Assert.Contains("\"A\"\t\"B\"\t\"C\"\t\"D\"\t\"unsourced\"\t\"\"\t\"\"\t\"\"\r\n", text);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
