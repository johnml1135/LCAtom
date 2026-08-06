using SIL.Motif.Generator;
using SIL.Motif.Generator.Emit;
using SIL.Motif.Generator.Model;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// Proves MOT-4 slice 2's field lists are derived, not hardcoded — the same discipline
/// <c>BasicFieldSelectorTests</c> established for slice 1, extended to the three remaining shapes
/// docs/plan-motif.md's MOT-4 section names: <c>rel/atomic</c>, <c>rel/col</c>/<c>rel/seq</c>, and
/// <c>owning/atomic</c> <c>create|delete</c>.
/// </summary>
public class RelationFieldSelectorTests
{
    [Fact]
    public void SelectAtomicSetClear_RealModel_YieldsExactlyMoFormMorphType()
    {
        var model = MotifModelLoader.Load();

        var selected = RelationFieldSelector.SelectAtomicSetClear(model.Rows);

        var key = Assert.Single(selected);
        Assert.Equal("MoForm", key.DeclaringClass);
        Assert.Equal("MorphType", key.FieldName);
        Assert.Equal(FieldKind.Rel, key.Kind);
        Assert.Equal(FieldCard.Atomic, key.Card);
        Assert.Equal("set|clear", key.Manifest.Verbs);
    }

    [Fact]
    public void SelectCollectionAddRemove_RealModel_YieldsExactlyTheThreeDocumentedFields()
    {
        var model = MotifModelLoader.Load();

        var selected = RelationFieldSelector.SelectCollectionAddRemove(model.Rows);

        var keys = selected.Select(r => (r.DeclaringClass, r.FieldName)).OrderBy(k => k.ToString()).ToList();
        var expected = new[]
        {
            ("LexEntry", "DialectLabels"),
            ("LexEntry", "DoNotPublishIn"),
            ("LexEntry", "DoNotShowMainEntryIn"),
        }.OrderBy(k => k.ToString()).ToList();

        Assert.Equal(expected, keys);
        Assert.All(selected, row => Assert.Equal(FieldKind.Rel, row.Kind));
    }

    [Fact]
    public void SelectCollectionAddRemove_RealModel_DialectLabelsIsSeqTheOtherTwoAreCol()
    {
        var model = MotifModelLoader.Load();

        var selected = RelationFieldSelector.SelectCollectionAddRemove(model.Rows);

        Assert.Equal(FieldCard.Seq, selected.Single(r => r.FieldName == "DialectLabels").Card);
        Assert.Equal(FieldCard.Col, selected.Single(r => r.FieldName == "DoNotPublishIn").Card);
        Assert.Equal(FieldCard.Col, selected.Single(r => r.FieldName == "DoNotShowMainEntryIn").Card);
    }

    [Fact]
    public void SelectOwningAtomicCreateDelete_RealModel_YieldsExactlyLexEntryLexemeForm()
    {
        var model = MotifModelLoader.Load();

        var selected = RelationFieldSelector.SelectOwningAtomicCreateDelete(model.Rows);

        var key = Assert.Single(selected);
        Assert.Equal("LexEntry", key.DeclaringClass);
        Assert.Equal("LexemeForm", key.FieldName);
        Assert.Equal(FieldKind.Owning, key.Kind);
        Assert.Equal(FieldCard.Atomic, key.Card);
        Assert.Equal("MoForm", key.Sig);
    }

    [Fact]
    public void SelectCollectionAddRemove_RealModel_ExcludesLexSenseDialectLabels()
    {
        // LexSense.DialectLabels has the identical shape (rel/seq CmPossibility,
        // addRef|removeRef|move) but LexSense is not one of this slice's two classes
        // (docs/plan-motif.md, MOT-4) -- the class filter, not the shape filter, is what scopes it out.
        var model = MotifModelLoader.Load();

        var selected = RelationFieldSelector.SelectCollectionAddRemove(model.Rows);

        Assert.DoesNotContain(selected, r => r.DeclaringClass == "LexSense");
    }
}
