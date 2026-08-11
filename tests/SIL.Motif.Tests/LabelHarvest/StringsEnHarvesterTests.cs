using SIL.Motif.Spikes.LabelHarvest;
using Xunit;

namespace SIL.Motif.Tests.LabelHarvest;

/// <summary>
/// Covers FieldWorks' <c>strings-en.xml</c>, which carries class names and list-purpose names but no
/// per-field text, in its three groups: <c>ClassNames</c> (a class id maps directly to one English
/// string), <c>PossibilityListItemTypeNames</c> (keyed by a <c>CmPossibilityList</c>'s owning-field name
/// rather than by class, so a caller must resolve the owning field to its real class before the row
/// means anything), and <c>AlternativeTitles</c> (plurals and view-specific titles, prefixed with a
/// class id that must be checked against known classes before being trusted, since some prefixes —
/// <c>Concordance</c>, <c>PubSettings</c> — are not classes at all).
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
        // ProdRestrict is genuinely absent from areaConfiguration.xml; must not default to CmPossibility.
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
