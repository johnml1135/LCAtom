using SIL.Motif.Generator;
using SIL.Motif.Generator.Descriptions;
using SIL.Motif.Generator.Descriptions.Harvest;
using SIL.Motif.Generator.Tsv;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// The checked-in harvest is the seam that keeps a Windows-only <c>.chm</c> out of the build, so what
/// matters is that the file round-trips exactly, that the shipped one is consistent with the map that
/// produced it, and that every row it claims actually lands on a description row.
/// </summary>
public class FieldWorksHelpDescriptionTsvTests
{
    [Fact]
    public void WriteThenRead_RoundTripsIncludingQuotesAndTabsInTheProse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"motif-help-{Guid.NewGuid():N}.tsv");
        var rows = new[]
        {
            new HarvestedHelpDescription(
                "FsFeatStrucType", "Abbreviation", "Abbreviation field (Feature Types)",
                "Stores the abbreviation, such as \"II\" for \"2nd declension.\"",
                "Lists/Feature_Types_fields/abbreviation_field_feature_types.htm", "exact",
                "HelpTopicPaths.resx:680, khtpField-FsFeatStrucType-Abbreviation",
                "sha256:0000000000000000000000000000000000000000000000000000000000000000"),
        };

        try
        {
            FieldWorksHelpDescriptionTsv.Write(path, rows);
            Assert.Equal(rows, FieldWorksHelpDescriptionTsv.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AHeaderThatDoesNotMatch_IsRejectedRatherThanReadByPosition()
    {
        const string text =
            "\"Class\"\t\"Field\"\t\"Description\"\r\n" +
            "\"LexSense\"\t\"Gloss\"\t\"text\"\r\n";

        Assert.Throws<GeneratorException>(
            () => QuotedTsv.ReadText("test.tsv", text, FieldWorksHelpDescriptionTsv.Header));
    }

    [Fact]
    public void TheShippedHarvest_CoversExactlyTheMappedFields()
    {
        var harvested = FieldWorksHelpDescriptionTsv.Read(RepoPaths.DefaultHelpDescriptionsPath());

        Assert.Equal(
            FieldWorksHelpFieldMap.Entries.Select(e => $"{e.Class}.{e.Field}").OrderBy(k => k, StringComparer.Ordinal),
            harvested.Select(h => h.Key).OrderBy(k => k, StringComparer.Ordinal));

        Assert.All(harvested, h => Assert.False(string.IsNullOrWhiteSpace(h.Description)));
        Assert.All(harvested, h => Assert.False(string.IsNullOrWhiteSpace(h.PageTitle)));
    }

    /// <summary>
    /// Every harvested page must actually be cited by the descriptions file. A harvest row nothing uses is
    /// either a field that lost its description row or a mapping that was never wired through — both worth
    /// knowing about, and neither visible from the harvest alone.
    /// </summary>
    [Fact]
    public void EveryHarvestedPage_IsCitedByADescriptionRow()
    {
        var harvested = FieldWorksHelpDescriptionTsv.Read(RepoPaths.DefaultHelpDescriptionsPath());
        var descriptions = KindDescriptionTsvParser.Parse(RepoPaths.DefaultDescriptionsPath())
            .ToDictionary(d => (d.Class, d.Field));

        foreach (var page in harvested)
        {
            Assert.True(
                descriptions.TryGetValue((page.Class, page.Field), out var description),
                $"{page.Key} was harvested from the help file but has no description row.");

            Assert.Equal(KindDescriptionRefresher.FieldWorksHelpSourceName, description!.Source);
            Assert.Equal(page.Description, description.Description);
            Assert.Contains(page.HelpPage, description.SourceDetail, StringComparison.Ordinal);
        }
    }
}
