using SIL.Motif.Generator.Join;
using SIL.Motif.Generator.Model;

namespace SIL.Motif.Generator.Derivation;

/// <summary>
/// <c>ComparisonClass</c> is <c>card == seq</c> → <c>positional</c>, everything else →
/// <c>unordered</c>, with cited exceptions where the base rule gives the wrong answer for a
/// documented reason (ADR 0022 decision 2).
/// </summary>
/// <remarks>
/// <para>
/// <b>There are seven exceptions, in two categories that mean opposite things</b> — not the "exactly
/// five" ADR 0022's prose states (pinned by
/// <c>ManifestConsistencyCheckerTests.CheckVerbsAndComparisonClass_RealInScopeRows_AllAgreeWithDerivation</c>
/// in <c>tests/SIL.Motif.Tests/Generator</c>, which checks this derivation against the real, current
/// manifest). The check that enforces agreement with this table is fail-closed rather than advisory
/// for exactly this reason: a specification's stated count is not proof against the data disagreeing
/// with it, and an "informational" version would have logged a warning nobody reads.
/// </para>
/// <para>
/// <b>Category 1 — order carries <em>more</em> than position</b> (<see cref="OrderCarriesMeaningExceptions"/>,
/// 5 rows, ADR 0022 decision 2's own enumeration): a field whose base-rule answer would be
/// <c>unordered</c> or <c>positional</c> in the ordinary sense, but where the actual sequence
/// encodes something extra — disjunctive first-match order (<c>feeding</c>) or an item's index
/// serving as its identity (<c>index-as-identity</c>).
/// </para>
/// <para>
/// <b>Category 2 — order carries <em>nothing</em> despite <c>card=seq</c></b>
/// (<see cref="PooledButPrivateExceptions"/>, 2 rows): <c>PhPhonData.Contexts</c> and
/// <c>.FeatConstraints</c> are declared <c>seq</c> in <c>MasterLCModel.xml</c> because a sequence is
/// how LibLCM stores a <em>pool</em>, not because position means anything: "Pooled-but-private
/// objects ... are a rule's private interior but live in a shared pool." What matters is which rule
/// references which context (by identity, from
/// <c>Input</c>/<c>StrucDesc</c>), not where a context sits in the pool, so the base rule's
/// <c>seq → positional</c> direction is simply wrong for these two.
/// </para>
/// </remarks>
public static class ComparisonClassDerivation
{
    /// <summary>Category 1: each row's comment says *why* order carries meaning, not just *that* it does.</summary>
    private static readonly IReadOnlyDictionary<FieldKey, string> OrderCarriesMeaningExceptions = new Dictionary<FieldKey, string>
    {
        // Position -> Allomorph.Index, disjunctive block (LexEntry.cs:45-50, Allomorph.cs:127-152, Morpher.cs:361-369).
        [new FieldKey("LexEntry", "AlternateForms")] = "feeding",

        // Feeding/bleeding rule order (MasterLCModel.xml:4496-4498; docs/research/2026-08-03-manifest-trust-audit.md).
        [new FieldKey("PhPhonData", "PhonRules")] = "feeding",

        // Alpha-variable pools, assigned by first-appearance traversal: position *is* identity, not rank.
        [new FieldKey("PhSegRuleRHS", "LeftContext")] = "index-as-identity",
        [new FieldKey("PhSegRuleRHS", "RightContext")] = "index-as-identity",
        [new FieldKey("PhSegRuleRHS", "StrucChange")] = "index-as-identity",
    };

    /// <summary>Category 2: each row's comment says *why* order carries nothing despite card=seq.</summary>
    private static readonly IReadOnlyDictionary<FieldKey, string> PooledButPrivateExceptions = new Dictionary<FieldKey, string>
    {
        // "Pooled-but-private storage ... never by pool position" (liblcm-inventory.tsv, Contexts row).
        [new FieldKey("PhPhonData", "Contexts")] = "unordered",

        // "Correction E3: index-as-identity mislocated ... semantically inert" (liblcm-inventory.tsv, FeatConstraints).
        [new FieldKey("PhPhonData", "FeatConstraints")] = "unordered",
    };

    /// <summary>Every cited exception, both categories, merged for lookup. Kept as two separate
    /// tables above (see class remarks — the two mean opposite things) and only merged here so
    /// <see cref="Derive"/> has one dictionary to probe. Exactly seven entries; the table is closed,
    /// not a pattern a new row can opt into (see <c>ComparisonClassDerivationTests</c>).</summary>
    public static readonly IReadOnlyDictionary<FieldKey, string> Exceptions =
        OrderCarriesMeaningExceptions
            .Concat(PooledButPrivateExceptions)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

    public static string Derive(FieldKey key, FieldCard? card)
    {
        if (Exceptions.TryGetValue(key, out var exceptionValue))
            return exceptionValue;

        return card == FieldCard.Seq ? "positional" : "unordered";
    }
}
