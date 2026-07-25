using SIL.LCModel;

namespace SIL.LCAtom.Runner.Apply;

/// <summary>
/// Best-effort invalidation of LibLCM's own stale derived caches after a rolled-back apply. See
/// docs/adr/0006-engine-reality-apply-readback-preflight.md, decision 3 ("Rollback is not Undo"):
/// <c>UndoStack.Rollback</c> skips <c>ClearCachesOnUndoRedo</c> and the forward-only setter hooks
/// <c>Undo</c>/<c>Redo</c> run, leaving <c>MoStemAllomorph</c> monomorphemic data, <c>LexEntry</c>
/// headword, and homograph indexes stale even though the object graph and identity map themselves
/// revert correctly.
/// </summary>
/// <remarks>
/// Only <see cref="ILexEntryRepository.ResetHomographs"/> is reachable through LibLCM's public
/// surface from outside its own assembly: the general cache-clear entry point
/// (<c>ICmObjectRepositoryInternal.ClearCachesOnUndoRedo</c>) and the monomorphemic-morph-data clear
/// (<c>MoStemAllomorphRepository.ClearMonomorphemicMorphData</c>) are both declared <c>internal</c>
/// and unreachable here. Per ADR 0006/0005's "the cache is discarded per the rollback-failure
/// contract" language, a caller whose failed apply could plausibly have touched headword- or
/// monomorphemic-morph-dependent state must dispose and reopen the <see cref="LcmCache"/> rather
/// than trust it after a rollback — this method invalidates only what is reachable.
///
/// The in-scope <c>lexical/sense/setGloss</c> operation touches none of the three caches (a
/// MultiUnicode field has no headword/homograph/monomorphemic dependency), so today this call is a
/// no-op safety net rather than a load-bearing fix — it is wired in ahead of the operation kinds
/// that will need it.
/// </remarks>
internal static class RollbackCacheInvalidator
{
    public static void InvalidateReachableCaches(LcmCache cache)
    {
        var entryRepository = cache.ServiceLocator.GetInstance<ILexEntryRepository>();
        entryRepository.ResetHomographs(NullProgress.Instance);
    }
}
