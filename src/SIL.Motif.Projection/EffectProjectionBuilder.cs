using SIL.Motif.Contract.Responses;
using SIL.Motif.Model.Effects;
using System.Collections.Generic;
using System.Linq;

namespace SIL.Motif.Projection;

/// <summary>Shapes a DryRun's or a Receipt's effect set into <see cref="EffectView"/>s for a report.</summary>
public static class EffectProjectionBuilder
{
    public static IReadOnlyList<EffectView> Build(IReadOnlyList<ExpectedEffect> effects) =>
        effects.Select(BuildOne).ToList();

    private static EffectView BuildOne(ExpectedEffect effect)
    {
        var wsKeys = effect.Before.Keys.Union(effect.After.Keys).OrderBy(k => k, System.StringComparer.Ordinal);

        var changes = new List<EffectChange>();
        foreach (var ws in wsKeys)
        {
            var before = effect.Before.TryGetValue(ws, out var b) ? b : null;
            var after = effect.After.TryGetValue(ws, out var a) ? a : null;
            if (before == after)
                continue;

            changes.Add(new EffectChange(ws, before, after));
        }

        return new EffectView(effect.CanonicalId.Value, effect.Field, changes);
    }
}
