using SIL.Motif.Runner.Caching;
using SIL.LCModel;

namespace SIL.Motif.Runner.Apply;

/// <summary>
/// Handles LibLCM's own stale derived caches after a rolled-back apply. See
/// docs/adr/0006-engine-reality-apply-readback-preflight.md, decision 3 ("Rollback is not Undo"):
/// <c>UndoStack.Rollback</c> skips <c>ClearCachesOnUndoRedo</c> and the forward-only setter hooks
/// <c>Undo</c>/<c>Redo</c> run, leaving <c>MoStemAllomorph</c> monomorphemic data, <c>LexEntry</c>
/// headword, and homograph indexes stale even though the object graph and identity map themselves
/// revert correctly.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this does NOT do, and why (verified against the liblcm source, not assumed):</b> an
/// earlier version of this method called <c>ILexEntryRepository.ResetHomographs</c>, reasoning that
/// it was the only non-<c>internal</c> invalidation entry point reachable from outside LibLCM's own
/// assembly (<c>ICmObjectRepositoryInternal.ClearCachesOnUndoRedo</c> and
/// <c>MoStemAllomorphRepository.ClearMonomorphemicMorphData</c> are both declared <c>internal</c> —
/// confirmed in <c>RepositoryAdditions.cs</c>: <c>CmObjectRepository</c>, which implements
/// <c>ClearCachesOnUndoRedo</c>, is itself an <c>internal class</c>, and
/// <c>MoStemAllomorphRepository.ClearMonomorphemicMorphData</c> is declared <c>internal</c>
/// directly). That reasoning is correct as far as it goes, but <c>ResetHomographs</c> is not
/// actually a cache-clear:
/// </para>
/// <para>
/// <c>LexEntryRepository.ResetHomographs</c> (liblcm <c>RepositoryAdditions.cs</c>, ~line 1252)
/// clears its own <c>m_homographInfo</c> dictionary (a real, harmless cache-clear) but then
/// unconditionally also calls <c>Cache.LanguageProject.LexDbOA.ResetHomographNumbers(progressBar)</c>.
/// <c>LexDb.ResetHomographNumbers</c> (liblcm <c>DomainImpl/OverridesLing_Lex.cs</c>, ~line 291) walks
/// every entry in the project and reassigns <c>HomographNumber</c>, and does so wrapped in
/// <c>UndoableUnitOfWorkHelper.DoUsingNewOrCurrentUOW(...)</c>. That helper's own implementation
/// (liblcm <c>Infrastructure/UndoableUnitOfWorkHelper.cs</c>, ~line 91) is:
/// <code>
/// if (actionHandler.CurrentDepth > 0) task(); else Do(undoText, redoText, actionHandler, task);
/// </code>
/// i.e. it only *joins* an already-open unit of work when one is open (<c>CurrentDepth &gt; 0</c>).
/// The documented, intended call site for rollback cleanup is exactly the state where no unit of
/// work is open (<c>CurrentDepth == 0</c> — the cleanup runs *after* the failed apply's own
/// <c>UndoableUnitOfWorkHelper.Dispose()</c> has already rolled back and closed the task). In that
/// state <c>DoUsingNewOrCurrentUOW</c> takes the <c>else</c> branch: it opens a brand-new
/// <c>UndoableUnitOfWorkHelper</c>, runs the full-project renumber, and calls <c>EndUndoTask()</c> —
/// a genuine, permanent, committed mutation added to the undo stack. Calling what looks like a
/// cache-invalidation helper would therefore silently commit a project-wide homograph renumbering as
/// a side effect of cleaning up after a *different*, already-failed change — exactly the defect this
/// type must not reintroduce.
/// </para>
/// <para>
/// <b>The fix:</b> do not call <c>ResetHomographs</c> (or anything else that can commit) at all. With
/// no non-committing invalidation reachable through LibLCM's public surface, the only honest option
/// is to mark the <see cref="LcmCache"/> instance itself as not safely reusable
/// (<see cref="CacheReusability.MarkPoisoned"/>) and surface that to the caller — see
/// <see cref="CacheReusability"/> and <see cref="CacheReusability.EnsureReusable"/>, which
/// <see cref="SIL.Motif.Runner.DryRun.ProposalDryRunner.Run"/> and
/// <see cref="SIL.Motif.Runner.Apply.ProposalApplier.Apply"/> both consult before doing any further
/// work against a cache instance. A caller whose apply failed and rolled back must discard this
/// cache and reload the project rather than trust or reuse it.
/// </para>
/// <para>
/// The in-scope <c>lexical/lexSense/setGloss</c> operation touches none of the three caches (a
/// MultiUnicode field has no headword/homograph/monomorphemic dependency), so today this call is a
/// no-op safety net rather than a load-bearing fix — it is wired in ahead of the operation kinds that
/// will need it (see also <see cref="SIL.Motif.Runner.DryRun.DerivedCachePoisoningOperationKinds"/>,
/// which wires the equivalent guard into Run).
/// </para>
/// </remarks>
public static class RollbackCacheInvalidator
{
    public static void InvalidateReachableCaches(LcmCache cache)
    {
        CacheReusability.MarkPoisoned(
            cache,
            "An apply failed and rolled back (docs/adr/0006, decision 3: 'Rollback is not Undo'); " +
            "LexEntry headword/homograph and MoStemAllomorph monomorphemic derived caches may now be " +
            "stale relative to the (correctly reverted) object graph, and no non-committing " +
            "invalidation of them is reachable through LibLCM's public surface from here.");
    }
}
