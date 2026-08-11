using SIL.Motif.Generator;
using SIL.Motif.Generator.Derivation;
using SIL.Motif.Generator.Model;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// The seven <c>(Kind, Card)</c> -> <c>Verbs</c> combinations, zero exceptions
/// (ADR 0022 decision 1).
/// </summary>
public class VerbDerivationTests
{
    [Theory]
    [InlineData(FieldKind.Basic, null, "set|clear")]
    [InlineData(FieldKind.Rel, FieldCard.Atomic, "set|clear")]
    [InlineData(FieldKind.Owning, FieldCard.Atomic, "create|delete")]
    [InlineData(FieldKind.Owning, FieldCard.Col, "create|delete")]
    [InlineData(FieldKind.Owning, FieldCard.Seq, "create|delete|move|reparent")]
    [InlineData(FieldKind.Rel, FieldCard.Col, "addRef|removeRef")]
    [InlineData(FieldKind.Rel, FieldCard.Seq, "addRef|removeRef|move")]
    public void Derive_AllSevenCombinations_ProduceExactlyTheDocumentedVerbs(FieldKind kind, FieldCard? card, string expected)
    {
        Assert.Equal(expected, VerbDerivation.Derive(kind, card));
    }

    [Fact]
    public void Derive_BasicWithNonNullCard_IsStructurallyImpossibleButThrowsRatherThanGuessing()
    {
        // <basic> never carries Card in practice, but Derive must still fail closed rather than guess a verb set.
        Assert.Throws<GeneratorException>(() => VerbDerivation.Derive(FieldKind.Basic, FieldCard.Atomic));
    }

    [Fact]
    public void Derive_RelWithNullCard_ThrowsRatherThanGuessing()
    {
        Assert.Throws<GeneratorException>(() => VerbDerivation.Derive(FieldKind.Rel, null));
    }

    [Theory]
    [InlineData("set|clear", 2)]
    [InlineData("create|delete", 2)]
    [InlineData("create|delete|move|reparent", 4)]
    [InlineData("addRef|removeRef", 2)]
    [InlineData("addRef|removeRef|move", 3)]
    public void EnumerateVerbs_SplitsPipeSeparatedString(string verbs, int expectedCount)
    {
        Assert.Equal(expectedCount, VerbDerivation.EnumerateVerbs(verbs).Count);
    }
}
