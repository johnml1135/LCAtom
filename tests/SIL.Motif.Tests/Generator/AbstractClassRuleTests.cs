using SIL.Motif.Generator;
using SIL.Motif.Generator.Checks;
using SIL.Motif.Generator.Model;
using SIL.Motif.Generator.ModelSource;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// ADR 0023 decision 2: a <c>create</c>/<c>delete</c> kind naming an abstract class fails, since no
/// such object can exist. <c>MoForm</c> is the plan's own live example
/// (<c>LexEntry.LexemeForm</c>, owning/atomic, <c>Sig = MoForm</c>).
/// </summary>
public class AbstractClassRuleTests
{
    private static readonly ModelClass AbstractMoForm = new("MoForm", "CmObject", Abstract: true);
    private static readonly ModelClass ConcreteMoStemAllomorph = new("MoStemAllomorph", "MoForm", Abstract: false);

    [Fact]
    public void Check_CreateTargetingAbstractClass_ThrowsNamingTheClass()
    {
        var ex = Assert.Throws<GeneratorException>(() =>
            AbstractClassRule.Check("LexEntry", "LexemeForm", "create", AbstractMoForm));

        Assert.Contains("LexEntry.LexemeForm", ex.Message);
        Assert.Contains("MoForm", ex.Message);
        Assert.Contains("abstract", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_DeleteTargetingAbstractClass_ThrowsNamingTheClass()
    {
        var ex = Assert.Throws<GeneratorException>(() =>
            AbstractClassRule.Check("LexEntry", "AlternateForms", "delete", AbstractMoForm));

        Assert.Contains("LexEntry.AlternateForms", ex.Message);
    }

    [Fact]
    public void Check_CreateTargetingConcreteClass_DoesNotThrow()
    {
        AbstractClassRule.Check("LexEntry", "LexemeForm", "create", ConcreteMoStemAllomorph);
    }

    [Theory]
    [InlineData("set")]
    [InlineData("clear")]
    [InlineData("addRef")]
    [InlineData("removeRef")]
    [InlineData("move")]
    [InlineData("reparent")]
    public void Check_NonCreateDeleteVerbs_NeverThrow_EvenAgainstAnAbstractClass(string verb)
    {
        AbstractClassRule.Check("LexEntry", "LexemeForm", verb, AbstractMoForm);
    }

    [Fact]
    public void Check_AgainstTheRealParsedModel_FiresForLexEntryLexemeForm()
    {
        // Runs against the real MasterLCModel.xml, not a hand-built fixture, to prove Abstract reads correctly.
        var model = MasterLcModelParser.Parse(ModelPathResolver.Resolve().Path);
        var moForm = model.Classes.Single(c => c.Id == "MoForm");
        Assert.True(moForm.Abstract);

        var lexemeForm = model.Fields.Single(f => f.DeclaringClass == "LexEntry" && f.FieldName == "LexemeForm");
        Assert.Equal("MoForm", lexemeForm.Sig);

        Assert.Throws<GeneratorException>(() =>
            AbstractClassRule.Check(lexemeForm.DeclaringClass, lexemeForm.FieldName, "create", moForm));
    }
}
