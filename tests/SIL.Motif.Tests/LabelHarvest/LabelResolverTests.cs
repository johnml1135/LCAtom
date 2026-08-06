using SIL.Motif.Spikes.LabelHarvest;
using Xunit;

namespace SIL.Motif.Tests.LabelHarvest;

/// <summary>
/// Covers the merge-and-confidence step: repeated occurrences of the same (class, field, label) collapse
/// to one row (the deliverable's "one row per distinct label, not one per field" rule), and Confidence is
/// resolved by whether any other, disagreeing label exists for the same (class, field) pair — regardless of
/// which of the three mechanisms produced it.
/// </summary>
public class LabelResolverTests
{
    [Fact]
    public void Field_level_label_with_no_conflict_is_exact()
    {
        var raw = new[] { new RawLabel("LexEntry", "LexemeForm", "Lexeme Form", "", "slice", "LexEntry.fwlayout") };

        var rows = LabelResolver.Resolve(raw);

        var row = Assert.Single(rows);
        Assert.Equal("exact", row.Confidence);
    }

    [Fact]
    public void Class_only_label_with_no_conflict_is_class_only()
    {
        var raw = new[] { new RawLabel("LexEntry", "", "Entry", "", "strings-en", "strings-en.xml") };

        var rows = LabelResolver.Resolve(raw);

        var row = Assert.Single(rows);
        Assert.Equal("class-only", row.Confidence);
    }

    [Fact]
    public void Disagreeing_labels_for_the_same_pair_are_all_marked_ambiguous()
    {
        // Reproduces MoInflAffixSlot.Name: "Name" in one layout, "Slot Name" in another.
        var raw = new[]
        {
            new RawLabel("MoInflAffixSlot", "Name", "Name", "", "slice", "MorphologyParts.xml, part A"),
            new RawLabel("MoInflAffixSlot", "Name", "Slot Name", "", "slice", "Morphology.fwlayout, layout EditSlot"),
        };

        var rows = LabelResolver.Resolve(raw);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("ambiguous", r.Confidence));
    }

    [Fact]
    public void Repeated_occurrences_of_the_same_label_collapse_to_one_row()
    {
        var raw = new[]
        {
            new RawLabel("CmPossibility", "Name", "Name", "", "slice", "CmPossibilityParts.xml, part A"),
            new RawLabel("CmPossibility", "Name", "Name", "", "slice", "CmPossibilityParts.xml, part B"),
            new RawLabel("CmPossibility", "Name", "Name", "", "slice", "CmPossibilityParts.xml, part C"),
        };

        var rows = LabelResolver.Resolve(raw);

        var row = Assert.Single(rows);
        Assert.Equal("exact", row.Confidence); // only one distinct label for this pair, despite three sightings
        Assert.Contains("part A", row.SourceDetail);
        Assert.Contains("part B", row.SourceDetail);
        Assert.Contains("part C", row.SourceDetail);
    }

    [Fact]
    public void Merge_keeps_the_first_non_empty_tooltip_and_unions_sources()
    {
        var raw = new[]
        {
            new RawLabel("LexSense", "Gloss", "Gloss", "", "slice", "fileA"),
            new RawLabel("LexSense", "Gloss", "Gloss", "Short translation equivalent.", "slice", "fileB"),
        };

        var rows = LabelResolver.Resolve(raw);

        var row = Assert.Single(rows);
        Assert.Equal("Short translation equivalent.", row.Tooltip);
    }

    [Fact]
    public void Many_occurrences_of_the_same_label_are_capped_with_a_more_count_rather_than_listed_in_full()
    {
        var raw = Enumerable.Range(0, 9)
            .Select(i => new RawLabel("CmPossibility", "Name", "Name", "", "slice", $"file{i}"))
            .ToArray();

        var rows = LabelResolver.Resolve(raw);

        var row = Assert.Single(rows);
        Assert.Contains("+4 more", row.SourceDetail);
    }
}
