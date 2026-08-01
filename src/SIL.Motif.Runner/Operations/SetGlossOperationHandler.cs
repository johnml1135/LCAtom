using System;
using System.Collections.Generic;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Model.Effects;
using SIL.Motif.Model.Snapshot;
using SIL.Motif.Runner.Resolution;
using SIL.Motif.Runner.Snapshotting;
using SIL.LCModel;

namespace SIL.Motif.Runner.Operations;

/// <summary>
/// Resolves, snapshots, lowers, and re-snapshots one <see cref="LexicalSenseOperationKinds.SetGloss"/>
/// operation, producing the one <see cref="ExpectedEffect"/> it causes. Shared by
/// <see cref="SIL.Motif.Runner.DryRun.ProposalDryRunner"/> (which rolls the surrounding unit of
/// work back afterward) and <see cref="SIL.Motif.Runner.Apply.ProposalApplier"/> (which commits
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

    /// <summary>
    /// Reads the CURRENT, pre-mutation footprint of one <see cref="LexicalSenseOperationKinds.SetGloss"/>
    /// operation's target — the live gloss alternatives, exactly as they stand right now — without
    /// applying the lowering or opening any unit of work. A plain getter chain, legal at any
    /// transaction state (docs/adr/0006-engine-reality-apply-readback-preflight.md, decision 1).
    /// </summary>
    /// <remarks>
    /// Used by <see cref="SIL.Motif.Runner.Apply.FootprintProbe"/> for Apply's pre-flight drift
    /// check (docs/adr/0004, decision 3): the returned <see cref="ExpectedEffect"/> carries the same
    /// value in <see cref="ExpectedEffect.Before"/> and <see cref="ExpectedEffect.After"/> because
    /// nothing is mutated here — only <c>Before</c> participates in
    /// <see cref="SIL.Motif.Model.Effects.FootprintDigest"/>.
    /// </remarks>
    public static ExpectedEffect ReadCurrentFootprint(LcmCache cache, OperationEnvelope operation)
    {
        if (operation.Target is not { } targetId)
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId.Value}' of kind '{LexicalSenseOperationKinds.SetGloss}' " +
                "requires 'target'.");
        }

        var target = CanonicalIdResolver.Resolve(cache, targetId);
        if (target is not ILexSense sense)
        {
            throw new InvalidOperationException(
                $"Target '{targetId.Value}' is not a LexSense (it is a {target.GetType().Name}).");
        }

        var current = GlossAlternatives(LexSenseSnapshotter.Snapshot(cache, sense));
        return new ExpectedEffect(targetId, SnapshotFields.LexSenseGloss, current, current);
    }

    private static IReadOnlyDictionary<string, string> GlossAlternatives(ObjectSnapshot snapshot) =>
        snapshot.MultiUnicodeFields.TryGetValue(SnapshotFields.LexSenseGloss, out var alternatives)
            ? alternatives
            : new Dictionary<string, string>();
}
