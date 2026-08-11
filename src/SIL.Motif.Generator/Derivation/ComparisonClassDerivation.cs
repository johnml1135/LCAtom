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
/// five" ADR 0022's prose and docs/plan-motif.md's MOT-2 section state. Checking the derivation
/// against the real, current manifest is what found the other two (see
/// <c>ManifestConsistencyCheckerTests.CheckVerbsAndComparisonClass_RealInScopeRows_AllAgreeWithDerivation</c>
/// in <c>tests/SIL.Motif.Tests/Generator</c>) — this is the fail-closed check earning its keep on the
/// very first run against real data, catching a case the specification asserted did not exist. That
/// is the argument for keeping this check fail-closed rather than advisory — an "informational"
/// version would have logged a warning nobody reads.
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
/// how LibLCM stores a <em>pool</em>, not because position means anything — issue B9 in
/// docs/issues.md: "Pooled-but-private objects ... are a rule's private interior but live in a
/// shared pool." What matters is which rule references which context (by identity, from
/// <c>Input</c>/<c>StrucDesc</c>), not where a context sits in the pool, so the base rule's
/// <c>seq → positional</c> direction is simply wrong for these two.
/// </para>
/// </remarks>
public static class ComparisonClassDerivation
{
    /// <summary>
    /// Category 1 (see class remarks): each row commented with *why* order carries meaning rather
    /// than just *that* it does, per ADR 0022 decision 2.
    /// </summary>
    private static readonly IReadOnlyDictionary<FieldKey, string> OrderCarriesMeaningExceptions = new Dictionary<FieldKey, string>
    {
        // Allomorph order is disjunctive elsewhere-blocking, verified against HermitCrab itself on
        // 2026-08-11 rather than against the manifest's own prose. List position becomes HC's
        // Allomorph.Index (LexEntry.cs:45-50 reassigns it from position on every mutation), and Index
        // drives the elsewhere-block at Allomorph.cs:127-152: a candidate built from a later-indexed
        // allomorph is rejected when an earlier-indexed one also matches, unless the two free-fluctuate.
        // HC's own comment: allomorphs "are applied disjunctively within a morpheme" (Allomorph.cs:9-11).
        // Mechanically it generates from all of them and then filters (Morpher.cs:361-369), rather than
        // stopping at the first match -- the earliest match still wins, so the classification holds while
        // the older "stops at the first that matches" wording described the effect, not the code.
        [new FieldKey("LexEntry", "AlternateForms")] = "feeding",

        // Phonological rule order encodes feeding and bleeding between rules — a neighbour's content
        // edit changes what a later rule in the list produces (MasterLCModel.xml:4496-4498, quoted in
        // docs/research/2026-08-03-manifest-trust-audit.md).
        [new FieldKey("PhPhonData", "PhonRules")] = "feeding",

        // The three PhSegRuleRHS alpha-variable pools: assigned by first-appearance traversal
        // (MOT-8), so an item's position in the sequence *is* its identity, not merely its rank.
        [new FieldKey("PhSegRuleRHS", "LeftContext")] = "index-as-identity",
        [new FieldKey("PhSegRuleRHS", "RightContext")] = "index-as-identity",
        [new FieldKey("PhSegRuleRHS", "StrucChange")] = "index-as-identity",
    };

    /// <summary>
    /// Category 2 (see class remarks): each row commented with *why* order carries nothing, despite
    /// <c>card=seq</c>, per issue B9 in docs/issues.md.
    /// </summary>
    private static readonly IReadOnlyDictionary<FieldKey, string> PooledButPrivateExceptions = new Dictionary<FieldKey, string>
    {
        // "Pooled-but-private storage ... members are addressed by reference from Input/StrucDesc,
        // never by pool position" (manifest/liblcm-inventory.tsv Rationale, PhPhonData.Contexts row;
        // issue B9, docs/issues.md).
        [new FieldKey("PhPhonData", "Contexts")] = "unordered",

        // "Correction E3: index-as-identity was mislocated here; a move on this pool is semantically
        // inert (pooled-but-private storage, not the traversal HCLoader walks)" (manifest Rationale,
        // PhPhonData.FeatConstraints row; issue B9, docs/issues.md).
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
