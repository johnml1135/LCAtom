using SIL.Motif.Generator;
using SIL.Motif.Generator.Checks;
using SIL.Motif.Generator.Descriptions;
using SIL.Motif.Generator.Descriptions.Harvest;
using SIL.Motif.Generator.Join;
using SIL.Motif.Generator.Manifest;
using SIL.Motif.Generator.Model;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// An exemption list is the thing that rots: an allowance granted for a real reason, kept long after the
/// reason stopped holding, read by the next person as evidence that somebody checked. These tests are about
/// the two mechanisms that stop that — the rule is re-derived rather than asserted, and the table and the
/// manifest have to agree in both directions.
/// </summary>
public class DescriptionExemptionTests
{
    private static JoinedRow Row(string cls, string field, string baseClass = "CmObject", string isAbstract = "false") =>
        new(
            DeclaringClass: cls,
            FieldName: field,
            Kind: FieldKind.Rel,
            Sig: "FsFeatDefn",
            Card: FieldCard.Atomic,
            Manifest: new ManifestRow(
                Class: cls, Base: baseClass, Abstract: isAbstract, Scope: "in", ScopeReason: "test",
                Field: field, Kind: "rel", Sig: "FsFeatDefn", Card: "atomic", HcReferenced: "no",
                Construct: "featureSpec", Group: "grammar", Classification: "semantic-operation",
                ComparisonClass: "unordered", Verbs: "set|clear", HcReachable: "no", EnumValues: "",
                Rationale: "test fixture"));

    [Fact]
    public void TheRuleHolds_WhenOnlyTheAbstractClassDeclaresTheField()
    {
        var rows = new[]
        {
            Row("FsFeatureSpecification", "Feature", isAbstract: "true"),
            Row("FsClosedValue", "Value", baseClass: "FsFeatureSpecification"),
        };

        Assert.Equal("", DescriptionExemptions.AbstractDeclarationOnly(rows, "FsFeatureSpecification", "Feature"));
    }

    /// <summary>
    /// The exemption's whole argument is that there is no per-class declaration for FieldWorks to have
    /// documented. A concrete subclass redeclaring the field destroys that argument, so the rule must stop
    /// holding by itself rather than waiting for someone to notice.
    /// </summary>
    [Fact]
    public void TheRuleBreaks_WhenAConcreteSubclassRedeclaresTheField()
    {
        var rows = new[]
        {
            Row("FsFeatureSpecification", "Feature", isAbstract: "true"),
            Row("FsClosedValue", "Feature", baseClass: "FsFeatureSpecification"),
        };

        var reason = DescriptionExemptions.AbstractDeclarationOnly(rows, "FsFeatureSpecification", "Feature");

        Assert.Contains("FsClosedValue", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRuleFindsARedeclarerThroughAnIntermediateClass()
    {
        var rows = new[]
        {
            Row("FsFeatureSpecification", "Feature", isAbstract: "true"),
            Row("FsComplexValue", "Value", baseClass: "FsFeatureSpecification", isAbstract: "true"),
            Row("FsNegatedValue", "Feature", baseClass: "FsComplexValue"),
        };

        Assert.Contains(
            "FsNegatedValue",
            DescriptionExemptions.AbstractDeclarationOnly(rows, "FsFeatureSpecification", "Feature"));
    }

    [Fact]
    public void TheRuleBreaks_WhenTheDeclaringClassStopsBeingAbstract()
    {
        var rows = new[] { Row("FsFeatureSpecification", "Feature", isAbstract: "false") };

        Assert.Contains(
            "no longer abstract",
            DescriptionExemptions.AbstractDeclarationOnly(rows, "FsFeatureSpecification", "Feature"));
    }

    /// <summary>An abstract sibling redeclaring it is not a counter-example: nothing is ever created as one,
    /// so there is still no dialog to have documented.</summary>
    [Fact]
    public void AnAbstractSubclassRedeclaringTheField_DoesNotBreakTheRule()
    {
        var rows = new[]
        {
            Row("FsFeatureSpecification", "Feature", isAbstract: "true"),
            Row("FsComplexValue", "Feature", baseClass: "FsFeatureSpecification", isAbstract: "true"),
        };

        Assert.Equal("", DescriptionExemptions.AbstractDeclarationOnly(rows, "FsFeatureSpecification", "Feature"));
    }

    [Fact]
    public void ARowClaimingNoSourceExists_WithoutBeingInTheTable_IsRejected()
    {
        var descriptions = new[]
        {
            new KindDescription(
                "LexSense", "Gloss", "Gloss", "Some text.", DescriptionExemptions.ReviewedValue,
                DescriptionExemptions.SourceValue, "looked everywhere, honest"),
        };

        var ex = Assert.Throws<GeneratorException>(
            () => DescriptionExemptionCheck.Check(RealRows(), Descriptions().Concat(descriptions).ToList()));

        Assert.Contains("LexSense.Gloss", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DescriptionExemptions.Entries", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExemptRowWithNoEvidence_IsRejected()
    {
        var descriptions = Descriptions()
            .Select(d => d.Class == "CmPossibilityList" && d.Field == "Abbreviation"
                ? d with { SourceDetail = "" }
                : d)
            .ToList();

        var ex = Assert.Throws<GeneratorException>(() => DescriptionExemptionCheck.Check(RealRows(), descriptions));

        Assert.Contains("CmPossibilityList.Abbreviation", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other direction: an exemption whose row has since found a source is a stale allowance, and the
    /// point of catching it is that the next reader would take it for a search somebody had done.
    /// </summary>
    [Fact]
    public void AnExemptionWhoseRowIsNowSourced_IsRejected()
    {
        var descriptions = Descriptions()
            .Select(d => d.Class == "FsFeatureSpecification" && d.Field == "Feature"
                ? d with { Reviewed = "sourced", Source = "liblcm/MasterLCModel.xml", SourceDetail = "line 1" }
                : d)
            .ToList();

        var ex = Assert.Throws<GeneratorException>(() => DescriptionExemptionCheck.Check(RealRows(), descriptions));

        Assert.Contains("delete the exemption", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The real manifest, the real descriptions file, and the real model: both exemptions still stand, and
    /// the derived one is re-derived from the model rather than trusted.
    /// </summary>
    [Fact]
    public void TheShippedExemptions_StillHoldAgainstTheRealModel()
    {
        DescriptionExemptionCheck.Check(RealRows(), Descriptions());
    }

    private static IReadOnlyList<JoinedRow> RealRows() => MotifModelLoader.Load().Rows;

    private static IReadOnlyList<KindDescription> Descriptions() =>
        KindDescriptionTsvParser.Parse(RepoPaths.DefaultDescriptionsPath());
}
