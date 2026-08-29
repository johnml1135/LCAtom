using System;
using SIL.LCModel;

namespace SIL.Motif.Runner.DryRun;

/// <summary>
/// A single-use, throwaway <see cref="LcmCache"/> that a Dry Run is allowed to mutate and nobody is
/// allowed to revert. See ADR 0016.
/// </summary>
/// <remarks>
/// <para>
/// This type exists to make one rule a compile-time fact rather than a comment: <b>a Dry Run does not
/// run against the live project.</b> Before this existed, <c>Run(cache, proposal)</c> accepted any cache
/// and protected the live project by mutating and then rolling back — and that rollback was the whole
/// source of derived-cache staleness, because <c>UndoStack.Rollback</c> skips the forward-only setter
/// hooks <c>Undo</c> runs. Requiring a scratch removes the rollback, and removing the rollback removes
/// the staleness. Everything the repository used to call "poisoning" was downstream of that one line.
/// </para>
/// <para>
/// <b>Single-use is enforced, and it is not fussiness.</b> A second Dry Run inside the same scratch
/// would read a baseline that already contains the first Dry Run's mutations, so its <c>before</c>
/// snapshots — and therefore its footprint digest — would describe a state the live project was never
/// in. Reusing a scratch is the same defect as rolling one back, just quieter. <see cref="Dispose"/>
/// is the discard.
/// </para>
/// <para>
/// <b>What this cannot check.</b> Nothing stops a caller from adopting the live cache and lying about
/// it, and no type system can: an <see cref="LcmCache"/> does not know whether anyone else cares about
/// it. What <see cref="Adopt"/> buys is that the lie has to be written down — a named call with a
/// stated provenance — instead of being the silent default. The Runner never creates a cache (cache
/// lifecycle is the Host's, <c>AGENTS.md</c> rule 3), so this is a handle over one the Host made,
/// normally <c>ScratchCacheFactory.CreateFromFileCopy</c>.
/// </para>
/// </remarks>
public sealed class DryRunScratch : IDisposable
{
    private readonly Action? _onDisposed;
    private LcmCache? _cache;
    private bool _consumed;

    private DryRunScratch(LcmCache cache, string provenance, Action? onDisposed)
    {
        _cache = cache;
        Provenance = provenance;
        _onDisposed = onDisposed;
    }

    /// <summary>
    /// Takes ownership of <paramref name="throwawayCache"/> as a Dry Run scratch.
    /// </summary>
    /// <param name="throwawayCache">
    /// A cache nobody else needs afterwards — normally the result of
    /// <c>ScratchCacheFactory.CreateFromFileCopy</c>. It will be mutated and then disposed, never
    /// reverted. A <see cref="BackendProviderType.kMemoryOnly"/> cache is rejected because it loses
    /// project-specific writing-system definitions. Passing a cache someone else is still using is
    /// a caller defect this cannot detect.
    /// </param>
    /// <param name="provenance">
    /// Short human-facing statement of where this copy came from, reported in the Dry Run's baseline
    /// note so a reviewer can see which state was measured. Required, because "some copy of something"
    /// is not an auditable baseline.
    /// </param>
    /// <param name="onDisposed">
    /// Runs after the cache is disposed — the hook for deleting the copied project directory. Optional
    /// only because a test-owned scratch may have nothing else to delete.
    /// </param>
    public static DryRunScratch Adopt(LcmCache throwawayCache, string provenance, Action? onDisposed = null)
    {
        if (throwawayCache is null) throw new ArgumentNullException(nameof(throwawayCache));
        if (string.IsNullOrWhiteSpace(provenance))
            throw new ArgumentException("A scratch must state where it came from.", nameof(provenance));
        if (throwawayCache.ProjectId.Type == BackendProviderType.kMemoryOnly)
        {
            throw new InvalidOperationException(
                "A memory-only cache cannot be a Dry Run scratch: it loses project-specific writing-system " +
                "fidelity, including collation and valid characters. Create a file-copy scratch instead.");
        }

        return new DryRunScratch(throwawayCache, provenance, onDisposed);
    }

    /// <summary>Where this copy came from, for the Dry Run's baseline note.</summary>
    public string Provenance { get; }

    /// <summary>
    /// Read-only access to the cache before <see cref="ConsumeForOneRun"/> hands it over for mutation.
    /// </summary>
    /// <remarks>
    /// Exists so a caller who must read something from this scratch first — the applied-proposal log,
    /// say — does not need a second open of the same project to do it. Peeking never marks this scratch
    /// used: it does not touch the single-use flag <see cref="ConsumeForOneRun"/> checks, so calling this
    /// any number of times, before or after that call, changes nothing about whether a run is still
    /// available. That flag is the only thing single-use ever rested on, so it is exactly as strict as
    /// before. As with <see cref="Adopt"/>, nothing stops a caller from mutating through the reference
    /// this returns; that would be the same lie <see cref="Adopt"/>'s remarks describe, just told here
    /// instead of there.
    /// </remarks>
    public LcmCache PeekCache()
    {
        if (_cache is null)
            throw new ObjectDisposedException(nameof(DryRunScratch), "This scratch has already been discarded.");

        return _cache;
    }

    /// <summary>
    /// Hands the cache to the one Dry Run this scratch is good for. Refuses a second time rather than
    /// returning a baseline that silently includes the first run's mutations (pinned by
    /// `DryRunScratch_RefusesASecondRun`).
    /// </summary>
    internal LcmCache ConsumeForOneRun()
    {
        if (_cache is null)
            throw new ObjectDisposedException(nameof(DryRunScratch), "This scratch has already been discarded.");

        if (_consumed)
        {
            throw new InvalidOperationException(
                "A Dry Run scratch is single-use and this one has already been used " +
                $"(provenance: {Provenance}). Its project now contains the previous run's mutations, so a " +
                "second Dry Run here would compute a baseline the live project was never in. Discard this " +
                "scratch and make another (ADR 0016).");
        }

        _consumed = true;
        return _cache;
    }

    /// <summary>
    /// Discards the scratch: disposes the cache and runs the caller's cleanup. This — not a rollback —
    /// is how a Dry Run's mutations are undone.
    /// </summary>
    public void Dispose()
    {
        var cache = _cache;
        _cache = null;

        if (cache is { IsDisposed: false }) cache.Dispose();
        _onDisposed?.Invoke();
    }
}
