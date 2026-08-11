using SIL.Motif.Generator.Derivation;
using SIL.Motif.Generator.Join;
using SIL.Motif.Generator.Model;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// <c>card == seq</c> -> <c>positional</c>, everything else -> <c>unordered</c>, with cited
/// exceptions (ADR 0022 decision 2). ADR 0022's prose cites exactly five; checking the derivation
/// against the real, current manifest (<see cref="ManifestConsistencyCheckerTests"/>) surfaced two
/// more — <c>PhPhonData.Contexts</c> and <c>.FeatConstraints</c> — that are just as cited (the
/// manifest's own <c>Rationale</c> column explains both), just not in the ADR's enumeration. See the
/// class-level remarks on <see cref="ComparisonClassDerivation"/> for the full citation. This test
/// file asserts all seven survive by name, so silently losing one is a test failure rather than a
/// quietly shrinking table.
/// </summary>
public class ComparisonClassDerivationTests
{
    [Theory]
    [InlineData(FieldCard.Atomic, "unordered")]
    [InlineData(FieldCard.Col, "unordered")]
    [InlineData(FieldCard.Seq, "positional")]
    [InlineData(null, "unordered")]
    public void Derive_NonExceptionField_FollowsTheBaseRule(FieldCard? card, string expected)
    {
        var key = new FieldKey("SomeOrdinaryClass", "SomeOrdinaryField");
        Assert.Equal(expected, ComparisonClassDerivation.Derive(key, card));
    }

    [Fact]
    public void Exceptions_HasExactlySevenEntries()
    {
        // 5 from ADR 0022's own prose + 2 pooled-storage corrections found against the real manifest.
        Assert.Equal(7, ComparisonClassDerivation.Exceptions.Count);
    }

    /// <summary>
    /// The two categories mean opposite things: losing a row from either is a failure here even if the
    /// total stays seven. Category 1 ("order carries more than position") only ever produces "feeding" or
    /// "index-as-identity"; category 2 ("order carries nothing despite card=seq") only ever produces
    /// "unordered" — the two value sets don't overlap, so counting by value cleanly separates them.
    /// </summary>
    [Fact]
    public void Exceptions_SplitsIntoFiveOrderCarriesMeaningAndTwoPooledButPrivate()
    {
        var orderCarriesMeaningCount = ComparisonClassDerivation.Exceptions.Values
            .Count(v => v is "feeding" or "index-as-identity");
        var pooledButPrivateCount = ComparisonClassDerivation.Exceptions.Values
            .Count(v => v == "unordered");

        Assert.Equal(5, orderCarriesMeaningCount);
        Assert.Equal(2, pooledButPrivateCount);
        Assert.Equal(ComparisonClassDerivation.Exceptions.Count, orderCarriesMeaningCount + pooledButPrivateCount);
    }

    [Theory]
    [InlineData("LexEntry", "AlternateForms", "feeding")]
    [InlineData("PhPhonData", "PhonRules", "feeding")]
    [InlineData("PhSegRuleRHS", "LeftContext", "index-as-identity")]
    [InlineData("PhSegRuleRHS", "RightContext", "index-as-identity")]
    [InlineData("PhSegRuleRHS", "StrucChange", "index-as-identity")]
    public void Derive_EachOfTheFiveAdrCitedExceptions_SurvivesByName(string cls, string field, string expected)
    {
        var key = new FieldKey(cls, field);
        // These are seq fields structurally; the exception's value must win over what card alone would say.
        Assert.Equal(expected, ComparisonClassDerivation.Derive(key, FieldCard.Seq));
        Assert.Equal(expected, ComparisonClassDerivation.Derive(key, FieldCard.Atomic));
    }

    [Theory]
    [InlineData("PhPhonData", "Contexts")]
    [InlineData("PhPhonData", "FeatConstraints")]
    public void Derive_EachOfTheTwoPooledStorageExceptions_SurvivesByName(string cls, string field)
    {
        // Both are owning/seq; without the exception the base rule says "positional", but rationale overrides it.
        Assert.Equal("unordered", ComparisonClassDerivation.Derive(new FieldKey(cls, field), FieldCard.Seq));
    }

    [Fact]
    public void Derive_AnEighthInjectedException_InNeitherCategory_IsNotHonoredByTheRealTable()
    {
        // The table is exactly 7 cited rows (5+2), not a pattern a new row can opt into by resembling either.
        var notReallyAnException = new FieldKey("LexEntry", "Etymology");
        Assert.False(ComparisonClassDerivation.Exceptions.ContainsKey(notReallyAnException));
        Assert.Equal("unordered", ComparisonClassDerivation.Derive(notReallyAnException, FieldCard.Atomic));
    }
}
