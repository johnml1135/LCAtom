using System;
using System.Collections.Generic;
using SIL.LCAtom.Contract.Ids;
using SIL.LCAtom.Contract.Model;
using SIL.LCAtom.Model.Effects;
using SIL.LCAtom.Model.Snapshot;
using SIL.LCAtom.Runner.Resolution;
using SIL.LCAtom.Runner.Snapshotting;
using SIL.LCModel;

namespace SIL.LCAtom.Runner.Operations;

/// <summary>
/// Resolves, snapshots, lowers, and re-snapshots one <see cref="LexicalSenseOperationKinds.SetGloss"/>
/// operation, producing the one <see cref="ExpectedEffect"/> it causes. Shared by
/// <see cref="SIL.LCAtom.Runner.Assessment.ChangeSetAssessor"/> (which rolls the surrounding unit of
/// work back afterward) and <see cref="SIL.LCAtom.Runner.Apply.ChangeSetApplier"/> (which commits
/// it) — both read back the true post-mutation state the same way (docs/adr/0006, decision 1), so
/// the resolve/snapshot/lower/snapshot sequence is identical; only what the caller does with the
/// surrounding unit of work differs.
/// </summary>
/// <remarks>
/// Must run inside an already-open unit of work; never opens or closes one itself, matching
/// <see cref="SetGlossLowering"/>'s own contract (docs/adr/0006, decision 5).
/// </remarks>
public static class SetGlossOperationHandler
{
    public static ExpectedEffect ApplyAndCaptureEffect(
        LcmCache cache, OperationEnvelope operation, List<CanonicalId> touchedTargets)
    {
        if (operation.Target is not { } targetId)
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId.Value}' of kind '{LexicalSenseOperationKinds.SetGloss}' " +
                "requires 'target'.");
        }

        if (operation.After is not { } after)
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId.Value}' of kind '{LexicalSenseOperationKinds.SetGloss}' " +
                "requires 'after'.");
        }

        var (writingSystemTag, text) = SetGlossPayload.Parse(after);

        var target = CanonicalIdResolver.Resolve(cache, targetId);
        if (target is not ILexSense sense)
        {
            throw new InvalidOperationException(
                $"Target '{targetId.Value}' is not a LexSense (it is a {target.GetType().Name}).");
        }

        touchedTargets.Add(targetId);

        var before = GlossAlternatives(LexSenseSnapshotter.Snapshot(cache, sense));

        SetGlossLowering.Apply(cache, sense, writingSystemTag, text);

        var after_ = GlossAlternatives(LexSenseSnapshotter.Snapshot(cache, sense));

        return new ExpectedEffect(targetId, SnapshotFields.LexSenseGloss, before, after_);
    }

    private static IReadOnlyDictionary<string, string> GlossAlternatives(ObjectSnapshot snapshot) =>
        snapshot.MultiUnicodeFields.TryGetValue(SnapshotFields.LexSenseGloss, out var alternatives)
            ? alternatives
            : new Dictionary<string, string>();
}
