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
/// quietly shrinking table (docs/plan-motif.md MOT-2: "silently losing one is a test failure rather
/// than a quieter grammar").
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
        // Five from ADR 0022's own prose, plus the two pooled-storage corrections found empirically
        // against the real manifest (class remarks on ComparisonClassDerivation).
        Assert.Equal(7, ComparisonClassDerivation.Exceptions.Count);
    }

    [Fact]
    public void Exceptions_SplitsIntoFiveOrderCarriesMeaningAndTwoPooledButPrivate()
    {
        // The two categories mean opposite things (class remarks on ComparisonClassDerivation), so
        // this asserts each one's size independently of the other — losing a row from either
        // category is a failure here even if some unrelated change kept the total at seven.
        // Category 1 ("order carries more than position") only ever produces "feeding" or
        // "index-as-identity"; category 2 ("order carries nothing despite card=seq") only ever
        // produces "unordered" — the two value sets don't overlap, so counting by value cleanly
        // separates them without reaching into the private per-category tables.
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
        // These are seq fields structurally, but the point of the test is that the exception's value
        // wins regardless of what the base rule on card would otherwise say.
        Assert.Equal(expected, ComparisonClassDerivation.Derive(key, FieldCard.Seq));
        Assert.Equal(expected, ComparisonClassDerivation.Derive(key, FieldCard.Atomic));
    }

    [Theory]
    [InlineData("PhPhonData", "Contexts")]
    [InlineData("PhPhonData", "FeatConstraints")]
    public void Derive_EachOfTheTwoPooledStorageExceptions_SurvivesByName(string cls, string field)
    {
        // Both are owning/seq in MasterLCModel.xml, so without the exception the base rule would say
        // "positional" — the whole point of these two rows is that the manifest's own cited
        // rationale overrides that to "unordered".
        Assert.Equal("unordered", ComparisonClassDerivation.Derive(new FieldKey(cls, field), FieldCard.Seq));
    }

    [Fact]
    public void Derive_AnEighthInjectedException_InNeitherCategory_IsNotHonoredByTheRealTable()
    {
        // Proves the table is exactly the seven cited rows (five order-carries-meaning, two
        // pooled-but-private), not "seven examples of a pattern" a new row can opt into by
        // resembling either category. A field not in the real table falls through to the base rule
        // even if a caller "expected" it to be an exception.
        var notReallyAnException = new FieldKey("LexEntry", "Etymology");
        Assert.False(ComparisonClassDerivation.Exceptions.ContainsKey(notReallyAnException));
        Assert.Equal("unordered", ComparisonClassDerivation.Derive(notReallyAnException, FieldCard.Atomic));
    }
}
