using SIL.Motif.Generator;
using SIL.Motif.Generator.Descriptions.Harvest;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// FieldWorks' <c>ContextHelp.xml</c> is a fourth previously-unharvested source for
/// <c>manifest/kind-descriptions.tsv</c>. These tests pin the mechanical extraction on a small synthetic
/// document; the curated id-to-(Class,Field) mapping is <see cref="FieldWorksContextHelpFieldMap"/>,
/// tested separately.
/// </summary>
public class FieldWorksContextHelpHarvesterTests
{
    private const string Xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <strings>
          <item id="NoHelp" caption="No Help"></item>
          <item id="FirstAllomorph">The primary allomorph that the constraint is about.</item>
          <item id="Multiline">Line one
                text continues   here.</item>
          <item id="Duplicate">First wins.</item>
          <item id="Duplicate">Second is ignored.</item>
        </strings>
        """;

    private static IReadOnlyDictionary<string, ContextHelpEntry> Harvest() =>
        FieldWorksContextHelpHarvester.HarvestText("ContextHelp.xml", Xml);

    [Fact]
    public void AnEmptyItem_IsSkipped()
    {
        Assert.False(Harvest().ContainsKey("NoHelp"));
    }

    [Fact]
    public void ATextItem_IsHarvestedWithItsLineNumber()
    {
        var entry = Harvest()["FirstAllomorph"];

        Assert.Equal("The primary allomorph that the constraint is about.", entry.Text);
        Assert.True(entry.LineNumber > 0);
        Assert.Contains("FirstAllomorph", entry.Citation);
    }

    [Fact]
    public void EmbeddedNewlinesAndRunsOfWhitespace_AreCollapsedToSingleSpaces()
    {
        Assert.Equal("Line one text continues here.", Harvest()["Multiline"].Text);
    }

    [Fact]
    public void ADuplicateId_KeepsTheFirstOccurrence()
    {
        Assert.Equal("First wins.", Harvest()["Duplicate"].Text);
    }

    [Fact]
    public void MalformedXml_ThrowsGeneratorException()
    {
        Assert.Throws<GeneratorException>(() => FieldWorksContextHelpHarvester.HarvestText("bad.xml", "<not-closed>"));
    }
}
