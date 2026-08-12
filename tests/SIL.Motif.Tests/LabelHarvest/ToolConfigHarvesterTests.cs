using SIL.Motif.Spikes.LabelHarvest;
using Xunit;

namespace SIL.Motif.Tests.LabelHarvest;

/// <summary>
/// Covers FieldWorks' tool/area configuration: a tool's label is keyed by the tool id in
/// <c>toolConfiguration.xml</c>, and the tool id is in turn keyed by <c>(ownerClass, ownerField)</c> in
/// the separate <c>areaConfiguration.xml</c> — whose own <c>className</c> attribute is often the
/// generic base class, not the field's real owning class, so a harvester must join through the tool id
/// rather than trust <c>className</c> directly.
/// </summary>
public class ToolConfigHarvesterTests
{
    private const string ToolConfigFixture = """
        <ToolInventory>
          <tool label="Confidence Levels" value="confidenceEdit" icon="SideBySideView"/>
        </ToolInventory>
        """;

    private const string AreaConfigFixture = """
        <AreaInventory>
          <parameters tool="confidenceEdit" className="CmPossibility" ownerClass="LangProject" ownerField="ConfidenceLevels"/>
          <parameters tool="lexRefEdit" className="LexRefType" ownerClass="LexDb" ownerField="References"/>
        </AreaInventory>
        """;

    [Fact]
    public void Harvest_joins_tool_label_to_its_areaConfig_class_key()
    {
        using var toolFile = new TestFile("toolConfiguration.xml", ToolConfigFixture);
        using var areaFile = new TestFile("areaConfiguration.xml", AreaConfigFixture);

        var result = ToolConfigHarvester.Harvest(toolFile.Path, areaFile.Path);

        var row = Assert.Single(result);
        Assert.Equal("CmPossibility", row.Class);
        Assert.Equal("", row.Field); // className is the generic base class, not a field
        Assert.Equal("Confidence Levels", row.Label);
        Assert.Equal("tool-config", row.Source);
        Assert.Contains("ownerField=ConfidenceLevels", row.SourceDetail);
    }

    [Fact]
    public void Harvest_drops_parameters_with_no_matching_tool_label()
    {
        using var toolFile = new TestFile("toolConfiguration.xml", ToolConfigFixture);
        using var areaFile = new TestFile("areaConfiguration.xml", AreaConfigFixture);

        var result = ToolConfigHarvester.Harvest(toolFile.Path, areaFile.Path);

        // "lexRefEdit" has no <tool> entry in the fixture, so it must not appear.
        Assert.DoesNotContain(result, r => r.Class == "LexRefType");
    }

    [Fact]
    public void HarvestOwnerFieldToClass_reads_the_ownerField_to_className_table_directly()
    {
        using var areaFile = new TestFile("areaConfiguration.xml", AreaConfigFixture);

        var map = ToolConfigHarvester.HarvestOwnerFieldToClass(areaFile.Path);

        Assert.Equal("CmPossibility", map["ConfidenceLevels"]);
        Assert.Equal("LexRefType", map["References"]);
    }
}
