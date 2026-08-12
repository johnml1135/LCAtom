using SIL.Motif.Generator;
using SIL.Motif.Generator.Emit;
using SIL.Motif.Generator.Model;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// Proves slice 3's field lists are derived from <c>Scope=in</c> + <c>HcReachable=yes</c> +
/// shape, not hardcoded to a class list — the widening <see cref="Slice3FieldSelector"/>'s own
/// remarks describe relative to <see cref="BasicFieldSelector"/>/<see cref="RelationFieldSelector"/>.
/// </summary>
public class Slice3FieldSelectorTests
{
    [Fact]
    public void SelectBasicSetClear_RealModel_OnlyMultiUnicodeMultiStringOrBooleanSigs()
    {
        var model = MotifModelLoader.Load();

        var selected = Slice3FieldSelector.SelectBasicSetClear(model.Rows);

        Assert.NotEmpty(selected);
        Assert.All(selected, row => Assert.Equal(FieldKind.Basic, row.Kind));
        Assert.All(selected, row => Assert.Equal("set|clear", row.Manifest.Verbs));
        Assert.All(selected, row => Assert.Contains(row.Sig, new[] { "MultiUnicode", "MultiString", "Boolean" }));
        Assert.All(selected, row => Assert.Equal("yes", row.Manifest.HcReachable));
    }

    [Fact]
    public void SelectBasicSetClear_RealModel_ExcludesFieldsSlice1AlreadyEmits()
    {
        var model = MotifModelLoader.Load();

        var selected = Slice3FieldSelector.SelectBasicSetClear(model.Rows);

        Assert.DoesNotContain(selected, r => r.DeclaringClass == "MoForm" && r.FieldName == "Form");
        Assert.DoesNotContain(selected, r => r.DeclaringClass == "MoForm" && r.FieldName == "IsAbstract");
        Assert.DoesNotContain(selected, r => r.DeclaringClass == "LexSense" && r.FieldName == "Gloss");
    }

    [Fact]
    public void SelectBasicSetClear_RealModel_ContainsARepresentativeGrammarField()
    {
        var model = MotifModelLoader.Load();

        var selected = Slice3FieldSelector.SelectBasicSetClear(model.Rows);

        Assert.Contains(selected, r => r.DeclaringClass == "PhSegmentRule" && r.FieldName == "Disabled");
        Assert.Contains(selected, r => r.DeclaringClass == "CmPossibility" && r.FieldName == "Abbreviation");
    }

    [Fact]
    public void SelectAtomicSetClear_RealModel_ExcludesMoFormMorphType()
    {
        var model = MotifModelLoader.Load();

        var selected = Slice3FieldSelector.SelectAtomicSetClear(model.Rows);

        Assert.NotEmpty(selected);
        Assert.All(selected, row => Assert.Equal(FieldKind.Rel, row.Kind));
        Assert.All(selected, row => Assert.Equal(FieldCard.Atomic, row.Card));
        Assert.All(selected, row => Assert.Equal("yes", row.Manifest.HcReachable));
        Assert.DoesNotContain(selected, r => r.DeclaringClass == "MoForm" && r.FieldName == "MorphType");
        Assert.Contains(selected, r => r.DeclaringClass == "LexSense" && r.FieldName == "MorphoSyntaxAnalysis");
    }

    [Fact]
    public void SelectCollectionAddRemove_RealModel_ExcludesLexEntrysAlreadyEmittedFields()
    {
        var model = MotifModelLoader.Load();

        var selected = Slice3FieldSelector.SelectCollectionAddRemove(model.Rows);

        Assert.NotEmpty(selected);
        Assert.All(selected, row => Assert.Equal(FieldKind.Rel, row.Kind));
        Assert.All(selected, row => Assert.True(row.Card == FieldCard.Col || row.Card == FieldCard.Seq));
        Assert.All(selected, row => Assert.Equal("yes", row.Manifest.HcReachable));
        Assert.DoesNotContain(selected, r => r.DeclaringClass == "LexEntry" && r.FieldName == "DialectLabels");
        Assert.Contains(selected, r => r.DeclaringClass == "PhNCSegments" && r.FieldName == "Segments");
    }

    [Fact]
    public void AllThreeSelectors_RealModel_TogetherYieldSeventyEightRows()
    {
        var model = MotifModelLoader.Load();

        var total =
            Slice3FieldSelector.SelectBasicSetClear(model.Rows).Count +
            Slice3FieldSelector.SelectAtomicSetClear(model.Rows).Count +
            Slice3FieldSelector.SelectCollectionAddRemove(model.Rows).Count;

        Assert.Equal(78, total);
    }
}
