using SIL.Motif.Generator;
using SIL.Motif.Generator.Checks;
using SIL.Motif.Generator.Descriptions;
using SIL.Motif.Generator.Join;
using SIL.Motif.Generator.Manifest;
using SIL.Motif.Generator.Model;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// ADR 0023 decision 5 as amended: a description is mandatory for every emitted kind, and one that merely
/// restates the label fails. The second half is what these tests are mostly about — presence alone was the
/// bar the original decision left unspecified, and the label harvest showed why that is not enough (768
/// labels, 20 with prose).
/// </summary>
public class DescriptionCheckTests
{
    private static JoinedRow Row(string cls, string field) => new(
        DeclaringClass: cls,
        FieldName: field,
        Kind: FieldKind.Basic,
        Sig: "MultiUnicode",
        Card: null,
        Manifest: Manifest(cls, field));

    private static ManifestRow Manifest(string cls, string field) => new(
        Class: cls, Base: "CmObject", Abstract: "false", Scope: "in",
        ScopeReason: "domain-reachable from lexical/grammar roots", Field: field,
        Kind: "basic", Sig: "MultiUnicode", Card: "", HcReferenced: "yes",
        Construct: "lexSense", Group: "lexical", Classification: "semantic-operation",
        ComparisonClass: "unordered", Verbs: "set|clear", HcReachable: "yes",
        EnumValues: "", Rationale: "test fixture");

    private static KindDescription Desc(string cls, string field, string label, string description) =>
        new(cls, field, label, description, "unsourced");

    [Fact]
    public void AUsableDescription_Passes()
    {
        DescriptionCheck.CheckEmittedKinds(
            new[] { Row("LexSense", "Gloss") },
            new[] { Desc("LexSense", "Gloss", "Gloss", "Set the short meaning shown for this sense.") });
    }

    [Fact]
    public void AMissingDescription_FailsNamingTheField()
    {
        var ex = Assert.Throws<GeneratorException>(() =>
            DescriptionCheck.CheckEmittedKinds(new[] { Row("LexSense", "Gloss") }, Array.Empty<KindDescription>()));

        Assert.Contains("LexSense.Gloss", ex.Message);
        Assert.Contains("no description", ex.Message);
    }

    [Fact]
    public void AnEmptyDescription_Fails()
    {
        var ex = Assert.Throws<GeneratorException>(() =>
            DescriptionCheck.CheckEmittedKinds(
                new[] { Row("LexSense", "Gloss") },
                new[] { Desc("LexSense", "Gloss", "Gloss", "   ") }));

        Assert.Contains("empty", ex.Message);
    }

    /// <summary>The bar the original presence-only check was missing.</summary>
    [Fact]
    public void ADescriptionThatOnlyRestatesTheLabel_Fails()
    {
        var ex = Assert.Throws<GeneratorException>(() =>
            DescriptionCheck.CheckEmittedKinds(
                new[] { Row("MoForm", "IsAbstract") },
                new[] { Desc("MoForm", "IsAbstract", "IsAbstract", "IsAbstract") }));

        Assert.Contains("only restates", ex.Message);
    }

    /// <summary>
    /// Punctuation and spacing must not disguise a restatement — "Is Abstract." is the label with a space
    /// and a full stop, and would pass a naive equality check.
    /// </summary>
    [Fact]
    public void ARestatementDisguisedByPunctuationOrSpacing_StillFails()
    {
        var ex = Assert.Throws<GeneratorException>(() =>
            DescriptionCheck.CheckEmittedKinds(
                new[] { Row("MoForm", "IsAbstract") },
                new[] { Desc("MoForm", "IsAbstract", "IsAbstract", "Is Abstract.") }));

        Assert.Contains("only restates", ex.Message);
    }

    /// <summary>A field with no harvested label must still not be described by its own name.</summary>
    [Fact]
    public void ADescriptionThatOnlyRestatesTheFieldName_FailsEvenWithNoLabel()
    {
        var ex = Assert.Throws<GeneratorException>(() =>
            DescriptionCheck.CheckEmittedKinds(
                new[] { Row("LexEntry", "DoNotUseForParsing") },
                new[] { Desc("LexEntry", "DoNotUseForParsing", "", "Do not use for parsing") }));

        Assert.Contains("only restates", ex.Message);
    }

    [Fact]
    public void EveryFailure_IsReportedAtOnce_NotJustTheFirst()
    {
        var ex = Assert.Throws<GeneratorException>(() =>
            DescriptionCheck.CheckEmittedKinds(
                new[] { Row("LexSense", "Gloss"), Row("MoForm", "Form"), Row("MoForm", "IsAbstract") },
                new[] { Desc("MoForm", "IsAbstract", "IsAbstract", "IsAbstract") }));

        Assert.Contains("LexSense.Gloss", ex.Message);
        Assert.Contains("MoForm.Form", ex.Message);
        Assert.Contains("MoForm.IsAbstract", ex.Message);
        Assert.Contains("3 kind(s)", ex.Message);
    }

    /// <summary>
    /// The scoping decision, asserted so it cannot regress: the check looks only at what is being emitted.
    /// Most in-scope fields have no description yet by design, and checking all of them would fail the build
    /// permanently.
    /// </summary>
    [Fact]
    public void FieldsNotBeingEmitted_AreNotRequiredToHaveDescriptions()
    {
        DescriptionCheck.CheckEmittedKinds(
            new[] { Row("LexSense", "Gloss") },
            new[] { Desc("LexSense", "Gloss", "Gloss", "Set the short meaning shown for this sense.") });
        // MoForm.Form has no description here and is not being emitted — no throw.
    }

    [Fact]
    public void AnUnsourcedDescription_Passes_BecauseReviewTracksButDoesNotGate()
    {
        DescriptionCheck.CheckEmittedKinds(
            new[] { Row("LexSense", "Gloss") },
            new[] { new KindDescription("LexSense", "Gloss", "Gloss", "Set the sense's short meaning.", "unsourced") });
    }

    /// <summary>
    /// Presence was never the whole bar: a description that claims provenance it does not have is the
    /// exact failure mode a missing-source column exists to catch.
    /// </summary>
    [Fact]
    public void AnUnrecognizedReviewedValue_Fails()
    {
        var ex = Assert.Throws<GeneratorException>(() =>
            DescriptionCheck.CheckEmittedKinds(
                new[] { Row("LexSense", "Gloss") },
                new[] { new KindDescription("LexSense", "Gloss", "Gloss", "Set the sense's short meaning.", "draft") }));

        Assert.Contains("not one of sourced / hand-corrected / unsourced", ex.Message);
    }

    [Fact]
    public void SourcedWithNoCitation_Fails()
    {
        var ex = Assert.Throws<GeneratorException>(() =>
            DescriptionCheck.CheckEmittedKinds(
                new[] { Row("LexSense", "Gloss") },
                new[] { new KindDescription("LexSense", "Gloss", "Gloss", "Set the sense's short meaning.", "sourced") }));

        Assert.Contains("Source/SourceDetail is empty", ex.Message);
    }

    [Fact]
    public void HandCorrectedWithNoCitation_Fails()
    {
        var ex = Assert.Throws<GeneratorException>(() =>
            DescriptionCheck.CheckEmittedKinds(
                new[] { Row("LexSense", "Gloss") },
                new[] { new KindDescription("LexSense", "Gloss", "Gloss", "Set the sense's short meaning.", "hand-corrected") }));

        Assert.Contains("Source/SourceDetail is empty", ex.Message);
    }

    [Fact]
    public void SourcedWithACitation_Passes()
    {
        DescriptionCheck.CheckEmittedKinds(
            new[] { Row("LexSense", "Gloss") },
            new[]
            {
                new KindDescription(
                    "LexSense", "Gloss", "Gloss", "Set the sense's short meaning.", "sourced",
                    "liblcm/MasterLCModel.xml", "line 42, <basic id=\"Gloss\"> under <class id=\"LexSense\">"),
            });
    }

    /// <summary>
    /// The 14 shipped descriptions must all clear the bar. This is the real file, so it fails if someone
    /// adds a row that restates its label.
    /// </summary>
    [Fact]
    public void TheShippedDescriptionsFile_ParsesAndEveryRowClearsTheBar()
    {
        var descriptions = KindDescriptionTsvParser.Parse(RepoPaths.DefaultDescriptionsPath());

        Assert.NotEmpty(descriptions);

        var rows = descriptions.Select(d => Row(d.Class, d.Field)).ToList();
        DescriptionCheck.CheckEmittedKinds(rows, descriptions);
    }

    [Fact]
    public void DuplicateDescriptionsForOneField_AreRejectedByTheParser()
    {
        const string text =
            "\"Class\"\t\"Field\"\t\"Label\"\t\"Description\"\t\"Reviewed\"\t\"Source\"\t\"SourceDetail\"\t\"SourceHash\"\r\n" +
            "\"LexSense\"\t\"Gloss\"\t\"Gloss\"\t\"First sentence about the gloss.\"\t\"unsourced\"\t\"\"\t\"\"\t\"\"\r\n" +
            "\"LexSense\"\t\"Gloss\"\t\"Gloss\"\t\"Second, conflicting sentence.\"\t\"unsourced\"\t\"\"\t\"\"\t\"\"\r\n";

        var ex = Assert.Throws<GeneratorException>(() => KindDescriptionTsvParser.ParseText("test.tsv", text));

        Assert.Contains("already has a description", ex.Message);
    }
}
