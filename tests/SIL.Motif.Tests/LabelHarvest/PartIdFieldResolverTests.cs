using SIL.Motif.Spikes.LabelHarvest;
using Xunit;

namespace SIL.Motif.Tests.LabelHarvest;

/// <summary>
/// Covers the part-id resolution table that bridges <c>.fwlayout</c> refs to the real field a composite
/// <c>Parts/*.xml</c> part wraps (e.g. <c>MoInflAffixSlot-Detail-NameAllA</c> -&gt; field <c>Name</c>).
/// </summary>
public class PartIdFieldResolverTests
{
    [Fact]
    public void BuildFieldMap_keys_by_class_and_the_suffix_after_the_second_hyphen()
    {
        const string fixture = """
            <PartInventory>
              <bin class="MoInflAffixSlot">
                <part id="MoInflAffixSlot-Detail-NameAllA" type="Detail">
                  <slice field="Name" label="Name"/>
                </part>
              </bin>
            </PartInventory>
            """;
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "MorphologyParts.xml"), fixture);

            var map = PartIdFieldResolver.BuildFieldMap(dir);

            Assert.Equal("Name", map[("MoInflAffixSlot", "NameAllA")]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildFieldMap_ignores_parts_with_no_nested_slice()
    {
        // "jtview" parts reference other parts rather than wrapping a <slice> — must not yield a mapping.
        const string fixture = """
            <PartInventory>
              <bin class="LexSense">
                <part id="LexSense-Jt-Summary" type="jtview">
                  <string field="HeadWord"/>
                </part>
              </bin>
            </PartInventory>
            """;
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "LexSenseParts.xml"), fixture);

            var map = PartIdFieldResolver.BuildFieldMap(dir);

            Assert.False(map.ContainsKey(("LexSense", "Summary")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
