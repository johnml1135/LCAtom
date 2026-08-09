using SIL.Motif.Spikes.LabelHarvest;
using Xunit;

namespace SIL.Motif.Tests.LabelHarvest;

/// <summary>
/// Covers mechanism 2 (docs/research/2026-08-05-fieldworks-user-facing-names.md §1.2): the fixtures below
/// are trimmed-down reproductions of the exact shapes that file cites — the <c>Gloss</c> ref that means two
/// different classes' fields depending on the enclosing <c>&lt;layout class="…"&gt;</c>, and the
/// nested-<c>&lt;indent&gt;</c> case.
/// </summary>
public class SliceLabelHarvesterTests
{
    [Fact]
    public void FwLayout_keys_the_label_by_enclosing_layout_class_not_the_bare_ref()
    {
        // Reproduces LexEntry.fwlayout: two <layout> blocks both use ref="Gloss", but one is class="LexSense"
        // and the other class="LexEtymology" — a naive scrape keyed on the bare ref would conflate them.
        const string fixture = """
            <LayoutInventory>
              <layout class="LexSense" type="detail" name="Normal">
                <part ref="Gloss" label="Gloss"/>
              </layout>
              <layout class="LexEtymology" type="detail" name="Normal">
                <indent>
                  <part ref="Gloss" label="Gloss"/>
                  <part ref="Form" label="Source Form"/>
                </indent>
              </layout>
            </LayoutInventory>
            """;
        using var file = new TestFile("LexEntry.fwlayout", fixture);

        var result = SliceLabelHarvester.HarvestFwLayout(file.Path, new Dictionary<(string, string), string>());

        Assert.Contains(result, r => r.Class == "LexSense" && r.Field == "Gloss");
        Assert.Contains(result, r => r.Class == "LexEtymology" && r.Field == "Gloss");
        // The nested-under-<indent> part is still found (Descendants, not Elements).
        Assert.Contains(result, r => r.Class == "LexEtymology" && r.Field == "Form" && r.Label == "Source Form");
    }

    [Fact]
    public void FwLayout_resolves_a_composite_ref_to_its_real_field_via_the_supplied_map()
    {
        // Reproduces the MoInflAffixSlot.Name / "Slot Name" case ADR 0023 cites: the EditSlot layout's ref
        // is "NameAllA", a composite Parts.xml part id suffix, not the bare field "Name".
        const string fixture = """
            <LayoutInventory>
              <layout class="MoInflAffixSlot" type="detail" name="EditSlot">
                <part ref="NameAllA" label="Slot Name"/>
              </layout>
            </LayoutInventory>
            """;
        using var file = new TestFile("Morphology.fwlayout", fixture);
        var fieldMap = new Dictionary<(string, string), string> { [("MoInflAffixSlot", "NameAllA")] = "Name" };

        var result = SliceLabelHarvester.HarvestFwLayout(file.Path, fieldMap);

        var row = Assert.Single(result);
        Assert.Equal("Name", row.Field); // resolved, not the literal "NameAllA"
        Assert.Equal("Slot Name", row.Label);
        Assert.Contains("resolved via Parts/*.xml", row.SourceDetail);
    }

    [Fact]
    public void FwLayout_skips_structural_placeholder_refs()
    {
        const string fixture = """
            <LayoutInventory>
              <layout class="LexEntry" type="detail" name="Normal">
                <part ref="_CustomFieldPlaceholder"/>
                <part ref="LexemeForm" label="Lexeme Form"/>
              </layout>
            </LayoutInventory>
            """;
        using var file = new TestFile("LexEntry.fwlayout", fixture);

        var result = SliceLabelHarvester.HarvestFwLayout(file.Path, new Dictionary<(string, string), string>());

        var row = Assert.Single(result);
        Assert.Equal("LexemeForm", row.Field);
    }

    [Fact]
    public void PartsXml_captures_field_label_and_tooltip_keyed_by_the_enclosing_bin_class()
    {
        const string fixture = """
            <PartInventory>
              <bin class="LexSense">
                <part id="LexSense-Detail-GlossAllA" type="detail">
                  <slice field="Gloss" label="Gloss" tooltip="Short translation equivalent for this lexeme."/>
                </part>
              </bin>
            </PartInventory>
            """;
        using var file = new TestFile("LexSenseParts.xml", fixture);

        var result = SliceLabelHarvester.HarvestPartsXml(file.Path);

        var row = Assert.Single(result);
        Assert.Equal("LexSense", row.Class);
        Assert.Equal("Gloss", row.Field);
        Assert.Equal("Gloss", row.Label);
        Assert.Equal("Short translation equivalent for this lexeme.", row.Tooltip);
        Assert.Contains("part id=LexSense-Detail-GlossAllA", row.SourceDetail);
    }

    [Fact]
    public void PartsXml_tolerates_the_real_stray_angle_bracket_bug_in_CellarParts_xml()
    {
        // CellarParts.xml genuinely has <bin class="CmSemanticDomain>"> — a stray '>' baked into the
        // attribute value. The harvester must not silently lose this class's model coverage over a typo upstream.
        const string fixture = """
            <PartInventory>
              <bin class="CmSemanticDomain>">
                <part id="CmSemanticDomain-Jt-Abbr">
                  <slice field="Abbreviation" label="Abbr"/>
                </part>
              </bin>
            </PartInventory>
            """;
        using var file = new TestFile("CellarParts.xml", fixture);

        var result = SliceLabelHarvester.HarvestPartsXml(file.Path);

        var row = Assert.Single(result);
        Assert.Equal("CmSemanticDomain", row.Class);
    }
}
