using System;
using System.Runtime.CompilerServices;
using SIL.LCModel;

namespace SIL.Motif.Runner.Caching;

/// <summary>
/// Tracks whether a given <see cref="LcmCache"/> *instance* remains safe to reuse for a subsequent
/// Run/Apply/pre-flight call, without adding any state to <see cref="LcmCache"/> itself (this
/// runner does not own that type). Two situations poison a cache for the remainder of its in-process
/// lifetime — both are consequences of docs/adr/0006-engine-reality-apply-readback-preflight.md,
/// decision 3 ("Rollback is not Undo"): <c>UndoStack.Rollback</c> skips the forward-only setter hooks
/// that <c>Undo</c>/<c>Redo</c> run, so <c>LexEntry</c> headword/homograph and
/// <c>MoStemAllomorph</c> monomorphemic derived caches can go stale relative to the (correctly
/// reverted) object graph and identity map:
/// </summary>
/// <remarks>
/// 1. A rolled-back <see cref="SIL.Motif.Runner.Apply.ProposalApplier.Apply"/> whose failure
///    could plausibly have touched one of those caches — see
///    <see cref="SIL.Motif.Runner.Apply.RollbackCacheInvalidator"/> for why the only public,
///    *supposedly* non-mutating invalidation path (<c>ILexEntryRepository.ResetHomographs</c>) turns
///    out not to be safe to call here, and why marking the cache non-reusable is the fix instead.
/// 2. A non-committing <see cref="SIL.Motif.Runner.DryRun.ProposalDryRunner.Run"/> of an
///    operation kind flagged by
///    <see cref="SIL.Motif.Runner.DryRun.DerivedCachePoisoningOperationKinds"/> as possibly
///    touching one of those same caches — Run always mutates-then-rolls-back, so running it over
///    such a kind hits the identical "rollback, not Undo" gap even though nothing failed.
///
/// This is process-local, instance-scoped state (a <see cref="ConditionalWeakTable{TKey,TValue}"/>
/// keyed by cache reference, not by any on-disk identity): a fresh <see cref="LcmCache"/> obtained by
/// reloading the project is never poisoned by a previous instance's history, matching "the cache
/// instance" language in the ADR precisely.
/// </remarks>
public static class CacheReusability
{
    private static readonly ConditionalWeakTable<LcmCache, PoisonRecord> PoisonedCaches = new();

    /// <summary>
    /// Marks <paramref name="cache"/> as no longer safe to reuse for a subsequent Run/Apply/
    /// pre-flight call. Idempotent: a second call overwrites the recorded reason rather than
    /// throwing or stacking records.
    /// </summary>
    public static void MarkPoisoned(LcmCache cache, string reason)
    {
        if (cache is null) throw new ArgumentNullException(nameof(cache));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A poison reason must not be empty.", nameof(reason));

        PoisonedCaches.Remove(cache);
        PoisonedCaches.Add(cache, new PoisonRecord(reason));
    }

    /// <summary>
    /// Returns <c>true</c> and the recorded reason when <paramref name="cache"/> has been marked
    /// poisoned (via <see cref="MarkPoisoned"/>) at any earlier point in this process; <c>false</c>
    /// and a <c>null</c> reason otherwise.
    /// </summary>
    public static bool IsPoisoned(LcmCache cache, out string? reason)
    {
        if (cache is null) throw new ArgumentNullException(nameof(cache));

        if (PoisonedCaches.TryGetValue(cache, out var record))
        {
            reason = record.Reason;
            return true;
        }

        reason = null;
        return false;
    }

    /// <summary>
    /// Hard-stops with a <see cref="CachePoisonedException"/> when <paramref name="cache"/> is
    /// already poisoned, rather than letting a caller silently Run/Apply against it and receive a
    /// possibly-different digest than a healthy cache would produce. Called at the top of both
    /// <see cref="SIL.Motif.Runner.DryRun.ProposalDryRunner.Run"/> and
    /// <see cref="SIL.Motif.Runner.Apply.ProposalApplier.Apply"/>.
    /// </summary>
    public static void EnsureReusable(LcmCache cache)
    {
        if (IsPoisoned(cache, out var reason))
            throw new CachePoisonedException(cache, reason!);
    }

    private sealed class PoisonRecord
    {
        public PoisonRecord(string reason) => Reason = reason;
        public string Reason { get; }
    }
}
