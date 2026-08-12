using SIL.Motif.Generator.Derivation;
using SIL.Motif.Generator.Join;

namespace SIL.Motif.Generator.Checks;

/// <summary>
/// ADR 0022 decision 3: derive <c>Verbs</c> and <c>ComparisonClass</c> for every in-scope row and
/// fail the build, naming the row, if the manifest disagrees and the row is not one of the cited
/// <see cref="ComparisonClassDerivation.Exceptions"/>. Also checks
/// <see cref="Derivation.GroupDerivation"/>'s prefix table is total over every declaring class
/// actually present (ADR 0024 decision 2) — <b>not</b> the same thing as agreeing with the
/// manifest's <c>Group</c>/domain column, which it deliberately never reads.
/// </summary>
/// <remarks>
/// ADR 0022's prose asserts "exactly five" <c>ComparisonClass</c> exceptions, but the manifest has
/// seven — <c>PhPhonData.Contexts</c> and <c>.FeatConstraints</c> are two more, a documented
/// pooled-storage case the ADR's count predates. This check is fail-closed rather than advisory
/// specifically because of that gap: a specification's stated count is not proof against the data
/// disagreeing with it. See the class remarks on <see cref="ComparisonClassDerivation"/> for the
/// full citation.
/// </remarks>
public static class ManifestConsistencyChecker
{
    /// <summary>
    /// Checks every in-scope row's <c>Verbs</c> and <c>ComparisonClass</c> against the derivation.
    /// <c>Scope != in</c> rows carry empty policy columns (the manifest README) and are skipped
    /// entirely; <c>Verbs == "n/a"</c> rows are unauthorable and skip only
    /// the verb half of the check — their <c>ComparisonClass</c> is still populated (order still
    /// means something even for a field nobody may author yet) and is still checked.
    /// </summary>
    public static void CheckVerbsAndComparisonClass(IReadOnlyList<JoinedRow> rows)
    {
        var mismatches = new List<string>();

        foreach (var row in rows)
        {
            if (row.Manifest.Scope != "in") continue;

            if (row.Manifest.Verbs != "n/a")
            {
                var derivedVerbs = VerbDerivation.Derive(row.Kind, row.Card);
                if (derivedVerbs != row.Manifest.Verbs)
                    mismatches.Add(
                        $"{row.Key}: Verbs mismatch — manifest says '{row.Manifest.Verbs}', derived '{derivedVerbs}'.");
            }

            var derivedComparisonClass = ComparisonClassDerivation.Derive(row.Key, row.Card);
            if (derivedComparisonClass != row.Manifest.ComparisonClass)
                mismatches.Add(
                    $"{row.Key}: ComparisonClass mismatch — manifest says '{row.Manifest.ComparisonClass}', " +
                    $"derived '{derivedComparisonClass}'.");
        }

        if (mismatches.Count > 0)
            throw new GeneratorException(
                "Manifest row(s) disagree with the ADR 0022 derivation and are not among the cited " +
                "exceptions (ComparisonClassDerivation.Exceptions):\n" + string.Join("\n", mismatches));
    }

    /// <summary>
    /// Every declaring class present in the joined model must resolve to a group — an unrecognized
    /// class-name prefix fails the build naming the class (ADR 0024 decision 2), independent of
    /// <c>Scope</c>: the table's totality is a fact about <c>GroupDerivation</c> covering LibLCM's
    /// class set, not about which fields are currently authorable.
    /// </summary>
    public static void CheckGroupIsTotal(IReadOnlyList<JoinedRow> rows)
    {
        var unresolved = new List<string>();
        foreach (var declaringClass in rows.Select(r => r.DeclaringClass).Distinct(StringComparer.Ordinal))
        {
            try
            {
                GroupDerivation.Derive(declaringClass);
            }
            catch (GeneratorException)
            {
                unresolved.Add(declaringClass);
            }
        }

        if (unresolved.Count > 0)
            throw new GeneratorException(
                "Class(es) with no entry in the closed group-prefix table (ADR 0024 decision 2): " +
                string.Join(", ", unresolved));
    }
}
