using SIL.Motif.Generator.Model;
using SIL.Motif.Generator.ModelSource;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// Parses the real, pinned <c>MasterLCModel.xml</c> and checks it against the verified facts
/// docs/plan-motif.md MOT-2 records: version <c>7000072</c>, 445 <c>&lt;basic&gt;</c> + 235
/// <c>&lt;owning&gt;</c> + 218 <c>&lt;rel&gt;</c> = 898 field declarations, 193 classes.
/// </summary>
public class MasterLcModelParserTests
{
    private static ParsedModel ParseRealModel() =>
        MasterLcModelParser.Parse(ModelPathResolver.Resolve().Path);

    [Fact]
    public void Parse_RealModel_ReportsDocumentedVersion()
    {
        Assert.Equal("7000072", ParseRealModel().Version);
    }

    [Fact]
    public void Parse_RealModel_HasDocumentedClassCount()
    {
        Assert.Equal(193, ParseRealModel().Classes.Count);
    }

    [Fact]
    public void Parse_RealModel_HasDocumentedFieldCountsByKind()
    {
        var model = ParseRealModel();

        Assert.Equal(445, model.Fields.Count(f => f.Kind == FieldKind.Basic));
        Assert.Equal(235, model.Fields.Count(f => f.Kind == FieldKind.Owning));
        Assert.Equal(218, model.Fields.Count(f => f.Kind == FieldKind.Rel));
        Assert.Equal(898, model.Fields.Count);
    }

    [Fact]
    public void Parse_RealModel_BasicFieldsCarryNoCard()
    {
        var model = ParseRealModel();
        Assert.All(model.Fields.Where(f => f.Kind == FieldKind.Basic), f => Assert.Null(f.Card));
    }

    [Fact]
    public void Parse_RealModel_OwningAndRelFieldsAlwaysCarryCard()
    {
        var model = ParseRealModel();
        Assert.All(
            model.Fields.Where(f => f.Kind is FieldKind.Owning or FieldKind.Rel),
            f => Assert.NotNull(f.Card));
    }

    [Fact]
    public void Parse_RealModel_MoFormIsAbstract()
    {
        // The live example the plan names for the abstract-class rule (ADR 0023 decision 2,
        // Checks/AbstractClassRule.cs).
        var moForm = ParseRealModel().Classes.Single(c => c.Id == "MoForm");
        Assert.True(moForm.Abstract);
    }

    [Fact]
    public void Parse_RealModel_LexEntryLexemeFormIsOwningAtomicMoForm()
    {
        var field = ParseRealModel().Fields.Single(f => f.DeclaringClass == "LexEntry" && f.FieldName == "LexemeForm");
        Assert.Equal(FieldKind.Owning, field.Kind);
        Assert.Equal(FieldCard.Atomic, field.Card);
        Assert.Equal("MoForm", field.Sig);
    }

    [Fact]
    public void Parse_RealModel_NoDuplicateClassFieldKeys()
    {
        var model = ParseRealModel();
        var keys = model.Fields.Select(f => (f.DeclaringClass, f.FieldName)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
