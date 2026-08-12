using SIL.Motif.Generator.Descriptions;
using SIL.Motif.Generator.Descriptions.Harvest;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// <see cref="KindDescriptionRefresher"/> attaches provenance to <c>manifest/kind-descriptions.tsv</c>
/// rows without ever writing new prose itself. These tests exercise its four branches directly, with
/// fake harvested data so they do not depend on the real liblcm/FieldWorks checkouts being present.
/// </summary>
public class KindDescriptionRefresherTests
{
    private static KindDescription Row(string cls, string field, string description = "Some draft text.", string reviewed = "unsourced") =>
        new(cls, field, "Label", description, reviewed);

    [Fact]
    public void AFieldWithASubstantiveLibLcmComment_GetsItsDescriptionReplacedAndCited()
    {
        var comments = new Dictionary<(string, string), LibLcmFieldComment>
        {
            [("LexSense", "Gloss")] = new("LexSense", "Gloss", "basic", "The short meaning shown for this sense.", 1, 42),
        };

        var result = KindDescriptionRefresher.Refresh(
            [Row("LexSense", "Gloss", "An old hand-paraphrased draft.")],
            comments,
            new Dictionary<string, ContextHelpEntry>());

        var row = Assert.Single(result.Rows);
        Assert.Equal("The short meaning shown for this sense.", row.Description);
        Assert.Equal("sourced", row.Reviewed);
        Assert.Equal(KindDescriptionRefresher.LibLcmSourceName, row.Source);
        Assert.Contains("line 42", row.SourceDetail);
        Assert.Equal(["LexSense.Gloss"], result.SourcedFromLibLcm);
    }

    [Fact]
    public void AFieldWithOnlyAPlaceholderLibLcmComment_FallsThroughToFieldWorksOrUnsourced()
    {
        var comments = new Dictionary<(string, string), LibLcmFieldComment>
        {
            [("PhCode", "Representation")] = new("PhCode", "Representation", "basic", "Put something here.", 1, 4452),
        };

        var result = KindDescriptionRefresher.Refresh(
            [Row("PhCode", "Representation", "kept as-is")],
            comments,
            new Dictionary<string, ContextHelpEntry>());

        var row = Assert.Single(result.Rows);
        Assert.Equal("kept as-is", row.Description);
        Assert.Equal("unsourced", row.Reviewed);
        Assert.Equal("", row.Source);
        Assert.Equal(["PhCode.Representation"], result.Unsourced);
    }

    [Fact]
    public void APlaceholderLibLcmComment_YieldsToAMappedFieldWorksContextHelpEntry()
    {
        var comments = new Dictionary<(string, string), LibLcmFieldComment>
        {
            [("PhCode", "Representation")] = new("PhCode", "Representation", "basic", "Put something here.", 1, 4452),
        };
        var contextHelp = new Dictionary<string, ContextHelpEntry>
        {
            ["Representation"] = new("Representation", "One way this phoneme surfaces.", 302),
        };

        var result = KindDescriptionRefresher.Refresh(
            [Row("PhCode", "Representation", "old text")],
            comments,
            contextHelp);

        var row = Assert.Single(result.Rows);
        Assert.Equal("One way this phoneme surfaces.", row.Description);
        Assert.Equal("sourced", row.Reviewed);
        Assert.Equal(KindDescriptionRefresher.FieldWorksSourceName, row.Source);
        Assert.Contains("line 302", row.SourceDetail);
        Assert.Equal(["PhCode.Representation"], result.SourcedFromFieldWorks);
    }

    [Fact]
    public void AHandCorrectedProdRestrictRow_KeepsItsTextVerbatimEvenWhenALibLcmCommentExists()
    {
        var original = Row("MoStemMsa", "ProdRestrict", "The hand-corrected, verified-against-source sentence.");
        var comments = new Dictionary<(string, string), LibLcmFieldComment>
        {
            // A substantive comment is available, but must not overwrite hand-corrected text — only cite it.
            [("MoStemMsa", "ProdRestrict")] = new("MoStemMsa", "ProdRestrict", "rel", "Some liblcm paragraph text.", 2, 2555),
        };

        var result = KindDescriptionRefresher.Refresh([original], comments, new Dictionary<string, ContextHelpEntry>());

        var row = Assert.Single(result.Rows);
        Assert.Equal("The hand-corrected, verified-against-source sentence.", row.Description);
        Assert.Equal("hand-corrected", row.Reviewed);
        Assert.Equal(KindDescriptionRefresher.LibLcmSourceName, row.Source);
        Assert.Contains("line 2555", row.SourceDetail);
        Assert.Equal(["MoStemMsa.ProdRestrict"], result.HandCorrected);
    }

    [Fact]
    public void AllFiveHandCorrectedProdRestrictFields_ArePreserved()
    {
        var rows = HandCorrectedFields.ProdRestrictFamily
            .Select(k => Row(k.Class, k.Field, $"hand text for {k.Class}.{k.Field}"))
            .ToList();

        var result = KindDescriptionRefresher.Refresh(rows, new Dictionary<(string, string), LibLcmFieldComment>(), new Dictionary<string, ContextHelpEntry>());

        Assert.Equal(5, result.HandCorrected.Count);
        Assert.All(result.Rows, r => Assert.Equal("hand-corrected", r.Reviewed));
        foreach (var row in result.Rows)
            Assert.Equal($"hand text for {row.Class}.{row.Field}", row.Description);
    }

    [Fact]
    public void AFieldWithNoSourceAnywhere_IsMarkedUnsourced_KeepingItsExistingDescription()
    {
        var result = KindDescriptionRefresher.Refresh(
            [Row("MoInflAffMsa", "PartOfSpeech", "an existing, unverified claim")],
            new Dictionary<(string, string), LibLcmFieldComment>(),
            new Dictionary<string, ContextHelpEntry>());

        var row = Assert.Single(result.Rows);
        Assert.Equal("an existing, unverified claim", row.Description);
        Assert.Equal("unsourced", row.Reviewed);
        Assert.Equal("", row.Source);
        Assert.Equal("", row.SourceDetail);
    }

    [Fact]
    public void AFieldWithAHarvestedHelpPage_IsSourcedFromIt_WhenNoEarlierTierMatches()
    {
        var helpPages = new Dictionary<(string, string), HarvestedHelpDescription>
        {
            [("LexEtymology", "Gloss")] = new(
                "LexEtymology", "Gloss", "Gloss field (Etymology)",
                "This field stores the gloss of the form in the Source Form field.",
                "User_Interface/Field_Descriptions/.../Gloss_field_Etymology.htm", "exact",
                "HelpTopicPaths.resx:314", "sha256:aaaa"),
        };

        var result = KindDescriptionRefresher.Refresh(
            [Row("LexEtymology", "Gloss", "an old draft")],
            new Dictionary<(string, string), LibLcmFieldComment>(),
            new Dictionary<string, ContextHelpEntry>(),
            helpPages);

        var row = Assert.Single(result.Rows);
        Assert.Equal("This field stores the gloss of the form in the Source Form field.", row.Description);
        Assert.Equal("sourced", row.Reviewed);
        Assert.Equal(KindDescriptionRefresher.FieldWorksHelpSourceName, row.Source);
        Assert.Contains("Gloss_field_Etymology.htm", row.SourceDetail, StringComparison.Ordinal);
        Assert.Equal(["LexEtymology.Gloss"], result.SourcedFromHelp);
    }

    /// <summary>
    /// A help page is a topic <em>about</em> a field; balloon help is what the application shows <em>in</em>
    /// it. When both exist, the one the user actually sees wins.
    /// </summary>
    [Fact]
    public void ContextHelp_BeatsAHarvestedHelpPage()
    {
        var contextHelp = new Dictionary<string, ContextHelpEntry>
        {
            ["Representation"] = new("Representation", "One way this phoneme surfaces.", 302),
        };
        var helpPages = new Dictionary<(string, string), HarvestedHelpDescription>
        {
            [("PhCode", "Representation")] = new(
                "PhCode", "Representation", "Grapheme field", "A different sentence from a help page.",
                "x.htm", "exact", "test", "sha256:bbbb"),
        };

        var result = KindDescriptionRefresher.Refresh(
            [Row("PhCode", "Representation")],
            new Dictionary<(string, string), LibLcmFieldComment>(),
            contextHelp,
            helpPages);

        Assert.Equal("One way this phoneme surfaces.", Assert.Single(result.Rows).Description);
    }

    [Fact]
    public void AnExemptField_KeepsItsTextAndCitesTheSearchRatherThanASource()
    {
        var result = KindDescriptionRefresher.Refresh(
            [Row("FsFeatureSpecification", "Feature", "text nobody found a source for")],
            new Dictionary<(string, string), LibLcmFieldComment>(),
            new Dictionary<string, ContextHelpEntry>());

        var row = Assert.Single(result.Rows);
        Assert.Equal("text nobody found a source for", row.Description);
        Assert.Equal(DescriptionExemptions.ReviewedValue, row.Reviewed);
        Assert.Equal(DescriptionExemptions.SourceValue, row.Source);
        Assert.Contains("Searched and not found", row.SourceDetail, StringComparison.Ordinal);
        Assert.Equal(["FsFeatureSpecification.Feature"], result.Exempt);
        Assert.Empty(result.Unsourced);
    }

    /// <summary>
    /// The drift report, which is the reason the sources are pinned at all: an already-sourced row whose
    /// upstream sentence has been reworded is reported, because a reworded sentence still reads fluently and
    /// nothing else downstream would notice it changed.
    /// </summary>
    [Fact]
    public void AnAlreadySourcedRowWhoseUpstreamTextChanged_IsReportedAsDrift()
    {
        var comments = new Dictionary<(string, string), LibLcmFieldComment>
        {
            [("LexSense", "Gloss")] = new("LexSense", "Gloss", "basic", "The reworded upstream sentence.", 1, 42),
        };
        var alreadySourced = new KindDescription(
            "LexSense", "Gloss", "Gloss", "The sentence a reviewer read.", "sourced",
            KindDescriptionRefresher.LibLcmSourceName, "line 42",
            SourceDigest.OfText("The sentence a reviewer read."));

        var result = KindDescriptionRefresher.Refresh(
            [alreadySourced], comments, new Dictionary<string, ContextHelpEntry>());

        var drift = Assert.Single(result.Drifted);
        Assert.Equal("LexSense.Gloss", drift.Key);
        Assert.Equal("The sentence a reviewer read.", drift.PreviousText);
        Assert.Equal(SourceDigest.OfText("The reworded upstream sentence."), drift.CurrentHash);
        Assert.Equal("The reworded upstream sentence.", drift.CurrentSourceText);
        Assert.False(drift.OurTextDiffersFromSource);
    }

    /// <summary>
    /// A row moving from an unverified draft to a real citation is a source being found, not a sentence
    /// changing under someone — reporting it as drift would bury the real signal in noise the first time
    /// any family gets sourced.
    /// </summary>
    [Fact]
    public void AnUnsourcedRowGainingItsFirstCitation_IsNotDrift()
    {
        var comments = new Dictionary<(string, string), LibLcmFieldComment>
        {
            [("LexSense", "Gloss")] = new("LexSense", "Gloss", "basic", "The upstream sentence.", 1, 42),
        };

        var result = KindDescriptionRefresher.Refresh(
            [Row("LexSense", "Gloss", "an AI-written draft")], comments, new Dictionary<string, ContextHelpEntry>());

        Assert.Empty(result.Drifted);
    }

    /// <summary>
    /// A new field's row can appear in the TSV with no matching source data anywhere. It must not error or
    /// drop the row — it should fall through to the unsourced branch exactly like any other field this
    /// refresher has no source for, preserving whatever text was there.
    /// </summary>
    [Fact]
    public void AnUnrecognizedAppendedRow_PassesThroughUnchangedRatherThanErroringOrBeingDropped()
    {
        var appended = Row("WfiWordform", "SpellingStatus", "The current status of a wordform.", "draft");

        var result = KindDescriptionRefresher.Refresh(
            [appended],
            new Dictionary<(string, string), LibLcmFieldComment>(),
            new Dictionary<string, ContextHelpEntry>());

        var row = Assert.Single(result.Rows);
        Assert.Equal("WfiWordform", row.Class);
        Assert.Equal("SpellingStatus", row.Field);
        Assert.Equal("The current status of a wordform.", row.Description);
        Assert.Equal("unsourced", row.Reviewed); // re-labelled from the old "draft" vocabulary, per manifest/README.md
    }

    [Fact]
    public void RefreshingTwice_IsIdempotent()
    {
        var comments = new Dictionary<(string, string), LibLcmFieldComment>
        {
            [("LexSense", "Gloss")] = new("LexSense", "Gloss", "basic", "The short meaning shown for this sense.", 1, 42),
        };

        var once = KindDescriptionRefresher.Refresh([Row("LexSense", "Gloss", "draft text")], comments, new Dictionary<string, ContextHelpEntry>());
        var twice = KindDescriptionRefresher.Refresh(once.Rows, comments, new Dictionary<string, ContextHelpEntry>());

        Assert.Equal(once.Rows, twice.Rows);
    }
}
