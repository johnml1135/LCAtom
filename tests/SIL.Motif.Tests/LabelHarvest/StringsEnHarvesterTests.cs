using SIL.Motif.Spikes.LabelHarvest;
using Xunit;

namespace SIL.Motif.Tests.LabelHarvest;

/// <summary>
/// Covers mechanism 1 (docs/research/2026-08-05-fieldworks-user-facing-names.md §1.1): the three
/// class/list-level groups <c>strings-en.xml</c> carries, and the two lookups the parser needs from other
/// sources to place each group's rows correctly.
/// </summary>
public class StringsEnHarvesterTests
{
    private const string Fixture = """
        <strings>
          <group id="ClassNames">
            <string id="LexEntry" txt="Entry"/>
            <string id="CmSemanticDomain" txt="Semantic Domain"/>
          </group>
          <group id="PossibilityListItemTypeNames">
            <string id="ConfidenceLevels" txt="Confidence Level"/>
            <string id="ProdRestrict" txt="Exception &quot;Feature&quot;"/>
          </group>
          <group id="AlternativeTitles">
            <string id="PartOfSpeech-Plural" txt="Categories (or Parts of Speech)"/>
            <string id="Concordance-Matches" txt="Concordance Results"/>
            <string id="PubSettings" txt="Publication Settings"/>
          </group>
        </strings>
        """;

    [Fact]
    public void ClassNames_produce_class_only_records()
    {
        using var file = new TestFile("strings-en.xml", Fixture);
        var ownerFieldToClass = new Dictionary<string, string> { ["ConfidenceLevels"] = "CmPossibility" };
        var knownClasses = new HashSet<string> { "PartOfSpeech" };

        var result = StringsEnHarvester.Harvest(file.Path, ownerFieldToClass, knownClasses);

        var lexEntry = Assert.Single(result, r => r.Class == "LexEntry");
        Assert.Equal("", lexEntry.Field);
        Assert.Equal("Entry", lexEntry.Label);
        Assert.Equal("strings-en", lexEntry.Source);
    }

    [Fact]
    public void PossibilityListItemTypeNames_resolves_owner_field_to_its_real_class()
    {
        using var file = new TestFile("strings-en.xml", Fixture);
        var ownerFieldToClass = new Dictionary<string, string> { ["ConfidenceLevels"] = "CmPossibility" };
        var knownClasses = new HashSet<string>();

        var result = StringsEnHarvester.Harvest(file.Path, ownerFieldToClass, knownClasses);

        var confidence = Assert.Single(result, r => r.Label == "Confidence Level");
        Assert.Equal("CmPossibility", confidence.Class);
        Assert.Equal("", confidence.Field); // class-level: a runtime fact about list membership, not a field
    }

    [Fact]
    public void PossibilityListItemTypeNames_drops_keys_with_no_resolvable_class_rather_than_guessing()
    {
        // ProdRestrict has no entry in ownerFieldToClass here (mirrors the real areaConfiguration.xml, where
        // it is genuinely absent) — the harvester must not default it to CmPossibility.
        using var file = new TestFile("strings-en.xml", Fixture);
        var ownerFieldToClass = new Dictionary<string, string> { ["ConfidenceLevels"] = "CmPossibility" };

        var result = StringsEnHarvester.Harvest(file.Path, ownerFieldToClass, new HashSet<string>());

        Assert.DoesNotContain(result, r => r.Label.StartsWith("Exception"));
    }

    [Fact]
    public void AlternativeTitles_keeps_only_ids_whose_prefix_is_a_known_class()
    {
        using var file = new TestFile("strings-en.xml", Fixture);
        var knownClasses = new HashSet<string> { "PartOfSpeech" }; // "Concordance" and "PubSettings" are not classes

        var result = StringsEnHarvester.Harvest(file.Path, new Dictionary<string, string>(), knownClasses);

        var plural = Assert.Single(result, r => r.Source == "strings-en" && r.Label.StartsWith("Categories"));
        Assert.Equal("PartOfSpeech", plural.Class);
        Assert.DoesNotContain(result, r => r.Label == "Concordance Results");
        Assert.DoesNotContain(result, r => r.Label == "Publication Settings");
    }
}
