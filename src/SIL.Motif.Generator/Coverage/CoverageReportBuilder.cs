using SIL.Motif.Generator.Derivation;
using SIL.Motif.Generator.Join;
using SIL.Motif.Generator.Model;
using SIL.Motif.Generator.ModelSource;

namespace SIL.Motif.Generator.Coverage;

/// <summary>Builds a <see cref="CoverageReport"/> from an already-joined model.</summary>
public static class CoverageReportBuilder
{
    public static CoverageReport Build(ModelPathResult pathResult, ParsedModel model, IReadOnlyList<JoinedRow> rows)
    {
        var countsByKindAndCard = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            var key = KindCardKey(row.Kind, row.Card);
            countsByKindAndCard[key] = countsByKindAndCard.GetValueOrDefault(key) + 1;
        }

        var inScope = rows.Where(r => r.Manifest.Scope == "in").ToList();
        var outOfScopeCount = rows.Count - inScope.Count;

        // "How many kinds would be emitted per group" — one kind per verb, per in-scope
        // authorable field, per docs/plan-motif.md MOT-2/MOT-3. Verbs == "n/a" rows are
        // unauthorable and contribute zero kinds (manifest/README.md: blank/n/a for anything the
        // Classification column marks non-authorable).
        var kindsPerGroup = new Dictionary<string, int>();
        foreach (var row in inScope)
        {
            if (row.Manifest.Verbs == "n/a") continue;

            var group = GroupDerivation.Derive(row.DeclaringClass);
            var verbCount = VerbDerivation.EnumerateVerbs(VerbDerivation.Derive(row.Kind, row.Card)).Count;
            kindsPerGroup[group] = kindsPerGroup.GetValueOrDefault(group) + verbCount;
        }

        return new CoverageReport(
            pathResult.Path,
            pathResult.Source,
            model.Version,
            rows.Count,
            inScope.Count,
            outOfScopeCount,
            countsByKindAndCard,
            kindsPerGroup);
    }

    private static string KindCardKey(FieldKind kind, FieldCard? card)
    {
        var kindText = kind.ToString().ToLowerInvariant();
        return card is null ? kindText : $"{kindText}/{card.Value.ToString().ToLowerInvariant()}";
    }
}
