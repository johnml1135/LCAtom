using SIL.Motif.Generator.Derivation;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// <c>{group}/{construct}/{verb}{FieldName}</c> (docs/plan-motif.md MOT-2; ADR 0023 decision 1).
/// </summary>
public class KindNameDerivationTests
{
    [Fact]
    public void DeriveOne_AssemblesTheThreeSegments()
    {
        Assert.Equal("lexical/lexSense/setGloss", KindNameDerivation.DeriveOne("lexical", "lexSense", "set", "Gloss"));
    }

    [Fact]
    public void DeriveAll_OneKindPerVerb_ForASeqOwningField()
    {
        var kinds = KindNameDerivation.DeriveAll("lexical", "lexEntry", "create|delete|move|reparent", "AlternateForms");

        Assert.Equal(
            new[]
            {
                "lexical/lexEntry/createAlternateForms",
                "lexical/lexEntry/deleteAlternateForms",
                "lexical/lexEntry/moveAlternateForms",
                "lexical/lexEntry/reparentAlternateForms",
            },
            kinds);
    }

    [Fact]
    public void DeriveAll_OneKindPerVerb_ForABasicField()
    {
        var kinds = KindNameDerivation.DeriveAll("lexical", "lexSense", "set|clear", "Gloss");

        Assert.Equal(new[] { "lexical/lexSense/setGloss", "lexical/lexSense/clearGloss" }, kinds);
    }
}
