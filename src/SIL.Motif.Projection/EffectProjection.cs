using System.Collections.Generic;
using System.Linq;
using SIL.Motif.Model.Effects;

namespace SIL.Motif.Projection;

/// <summary>One writing-system alternative that actually moved, on one <see cref="EffectView"/>.</summary>
/// <param name="Before"><c>null</c> when the alternative was absent before the change.</param>
/// <param name="After"><c>null</c> when the alternative is absent after the change.</param>
public sealed record EffectChange(string Ws, string? Before, string? After);

/// <summary>
/// One identity-keyed field transition, shaped for display: only the writing-system alternatives
/// that actually differ, in place of <see cref="ExpectedEffect"/>'s full before/after maps.
/// </summary>
public sealed record EffectView(string CanonicalId, string Field, IReadOnlyList<EffectChange> Changes);

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
