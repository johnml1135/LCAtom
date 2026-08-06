using SIL.Motif.Generator;
using SIL.Motif.Generator.Derivation;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// <c>lowerFirst(DeclaringClass)</c>, verbatim, no prefix stripping (ADR 0023 decision 1).
/// </summary>
public class ConstructDerivationTests
{
    [Theory]
    [InlineData("LexSense", "lexSense")]
    [InlineData("LexEntry", "lexEntry")]
    [InlineData("CmPossibility", "cmPossibility")] // no prefix stripped, despite the "Cm"
    [InlineData("PhSegRuleRHS", "phSegRuleRHS")]
    [InlineData("MoForm", "moForm")]
    public void Derive_LowersOnlyTheFirstCharacter(string declaringClass, string expected)
    {
        Assert.Equal(expected, ConstructDerivation.Derive(declaringClass));
    }

    [Fact]
    public void Derive_EmptyClassName_Throws()
    {
        Assert.Throws<GeneratorException>(() => ConstructDerivation.Derive(""));
    }
}
