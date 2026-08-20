using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SIL.Motif.Contract.Model;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Model.Receipts;
using SIL.Motif.Runner.Apply;
using SIL.Motif.Runner.DryRun;
using SIL.LCModel;
using DryRunModel = SIL.Motif.Model.DryRun.DryRun;
using BoundDryRunAnchor = SIL.Motif.Model.DryRun.BoundDryRunAnchor;

namespace SIL.Motif.Cli;

/// <summary>
/// A long-lived CLI session over one project: one live <see cref="LcmCache"/>, loaded once and held
/// for the session's whole life, and one pristine scratch that a Dry Run fans out from rather than
/// mutates directly. See ADR 0016.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see cref="Commands.DryRun"/> and <see cref="Commands.Apply"/> each load
/// and dispose their own cache, so an author-dry-run-inspect-revise loop pays a full project open on
/// every round trip. A session amortizes that open across as many dry runs and one apply as the caller
/// wants, while keeping every ADR 0016 guarantee intact: a Dry Run still mutates a fresh, single-use
/// <see cref="DryRunScratch"/>, never the live cache and never a scratch that already carries another
/// run's mutations.
/// </para>
/// <para>
/// <b>Two caches, two lifetimes.</b> <see cref="LiveCache"/> is opened once by <see cref="Open"/> and
/// stays open until <see cref="Dispose"/> (or a failed <see cref="Apply"/> discards it early — see
/// below). The pristine scratch is a dormant, never-mutated file-backed copy, rebuilt only when
/// <see cref="FootprintProbe.ComputeCurrentFootprintDigest"/> shows the live project has moved
/// relative to it (<see cref="PristineRebuildCount"/> counts these rebuilds). Every call to
/// <see cref="DryRun"/> spins a further disposable copy off the pristine scratch — never off the live
/// cache — runs one <see cref="ProposalDryRunner.Run"/> against it, and discards it, so the pristine
/// scratch itself is never consumed and can serve any number of dry runs unchanged.
/// </para>
/// <para>
/// <b>A failed apply ends the session's live cache, not the session.</b> <see cref="ProposalApplier.Apply"/>
/// rolls back on any exception, and a rollback is not an Undo (ADR 0016): the live cache is no longer
/// trustworthy. <see cref="Apply"/> discards it before rethrowing, and every subsequent call that needs
/// <see cref="LiveCache"/> fails clearly instead of silently reusing a suspect cache. The caller must
/// open a new session; <see cref="Dispose"/> still tears down whatever remains (the pristine scratch,
/// and the live cache if the discard above never ran). A failure in the <em>save</em> that follows a
/// successful apply is reported differently -- see <see cref="Apply"/>'s remarks: it is not a rollback,
/// so it throws <see cref="NeedsReconciliationException"/> rather than reusing the rollback wording.
/// </para>
/// <para>
/// <b>The Runner is unchanged.</b> A session only decides which cache <see cref="ProposalDryRunner.Run"/>
/// and <see cref="ProposalApplier.Apply"/> receive; both still operate on a cache they do not own.
/// </para>
/// </remarks>
public sealed class CliSession : IDisposable
{
    private readonly FwDataProjectLoader _loader;
    private readonly ScratchCacheFactory _scratchFactory;
    private readonly string _fwDataPath;
    private readonly string _sessionTempRoot;

    private LcmCache? _liveCache;
    private LcmCache? _pristineCache;
    private string? _pristineFwDataPath;
    private string? _pristineDirectory;
    private bool _disposed;

    private CliSession(string fwDataPath, FwDataProjectLoader loader, LcmCache liveCache)
    {
        _fwDataPath = fwDataPath;
        _loader = loader;
        _scratchFactory = new ScratchCacheFactory(loader);
        _liveCache = liveCache;
        _sessionTempRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.CliSession", Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Opens <paramref name="fwDataPath"/> once and returns a session holding it live for the caller's
    /// whole loop. The pristine scratch is not built here; the first <see cref="DryRun"/> call builds it.
    /// </summary>
    /// <param name="loader">
    /// Injectable so a test can observe how many times the live project is actually opened; defaults to
    /// a fresh <see cref="FwDataProjectLoader"/>.
    /// </param>
    public static CliSession Open(string fwDataPath, FwDataProjectLoader? loader = null)
    {
        if (string.IsNullOrWhiteSpace(fwDataPath))
            throw new ArgumentException("Required.", nameof(fwDataPath));

        var fullPath = Path.GetFullPath(fwDataPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Project file not found: '{fullPath}'.", fullPath);

        var effectiveLoader = loader ?? new FwDataProjectLoader();
        var liveCache = effectiveLoader.LoadCache(fullPath);
        return new CliSession(fullPath, effectiveLoader, liveCache);
    }

    /// <summary>
    /// The session's live, exclusively-locked project cache. Throws once a failed <see cref="Apply"/>
    /// has discarded it (see the class remarks) or the session has been disposed.
    /// </summary>
    public LcmCache LiveCache =>
        _liveCache ?? throw new ObjectDisposedException(
            nameof(CliSession),
            "This session's live cache is gone -- either a prior Apply failed and discarded it (a " +
            "rolled-back apply is not trustworthy, ADR 0016), or the session was disposed. Open a new " +
            "session rather than reusing this one.");

    /// <summary>How many times the pristine scratch has been rebuilt from the live project.</summary>
    public int PristineRebuildCount { get; private set; }

    /// <summary>
    /// Dry-runs <paramref name="proposal"/> against a fresh, single-use copy fanned out from this
    /// session's pristine scratch, rebuilding that scratch first only if the live project's footprint
    /// for this Proposal has moved since the scratch was last built.
    /// </summary>
    public DryRunModel DryRun(Proposal proposal)
    {
        return DryRun(Array.Empty<Proposal>(), proposal);
    }

    /// <summary>
    /// Prepares one fresh scratch with <paramref name="prerequisites"/> and evaluates
    /// <paramref name="proposal"/> against that state. The pristine freshness check covers every
    /// Proposal whose operations will execute on the scratch.
    /// </summary>
    public DryRunModel DryRun(IReadOnlyCollection<Proposal> prerequisites, Proposal proposal)
    {
        if (prerequisites is null) throw new ArgumentNullException(nameof(prerequisites));
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));
        ThrowIfDisposed();

        EnsurePristineIsFresh(prerequisites, proposal);

        var fanOutRoot = Path.Combine(_sessionTempRoot, "run-" + Guid.NewGuid().ToString("N"));
        var fanOutCache = _scratchFactory.CreateFromFileCopy(_pristineFwDataPath!, fanOutRoot);
        var scratch = DryRunScratch.Adopt(
            fanOutCache,
            $"session fan-out copy of the pristine scratch for '{_fwDataPath}'",
            onDisposed: () => TryDeleteDirectory(fanOutRoot));

        try
        {
            return ProposalDryRunner.Run(scratch, prerequisites, proposal);
        }
        finally
        {
            // Discard, never revert: the same rule DryRunScratch enforces for every other caller.
            scratch.Dispose();
        }
    }

    /// <summary>
    /// Applies <paramref name="proposal"/> to the live cache and saves on success. On failure the live
    /// cache is discarded before the exception propagates -- see the class remarks.
    /// </summary>
    /// <remarks>
    /// Two distinct boundaries can fail here, and they recover differently, so this does not report
    /// them alike. A throw from <see cref="ProposalApplier.Apply"/> itself rolls back its own unit of
    /// work (ADR 0016) -- nothing is durable, so discarding this cache and opening a new session is a
    /// complete, safe recovery. A throw from the <em>save</em> that follows a successful, non-idempotent
    /// apply is different: the mutation already committed, and <c>Save</c>'s own remarks document that
    /// its background commit thread can fail partway, leaving the <c>.fwdata</c> file's on-disk state
    /// unconfirmed. That is not a rollback, and reporting it as one would tell a caller it is safe to
    /// just re-open the project when the file itself might not be intact -- so it surfaces as
    /// <see cref="NeedsReconciliationException"/> instead, naming <see cref="ReconciliationBoundary.Save"/>.
    /// </remarks>
    public Receipt Apply(
        Proposal proposal, BoundDryRunAnchor anchor, string applierIdentity, string description = "")
    {
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));
        ThrowIfDisposed();

        Receipt receipt;
        try
        {
            receipt = ProposalApplier.Apply(LiveCache, proposal, anchor, applierIdentity, description);
        }
        catch
        {
            DiscardLiveCache();
            throw;
        }

        if (receipt.AlreadyApplied)
            return receipt;

        try
        {
            _loader.Save(LiveCache);
        }
        catch (Exception ex)
        {
            DiscardLiveCache();
            throw new NeedsReconciliationException(
                ReconciliationBoundary.Save,
                $"Proposal {proposal.ProposalId.Value} committed to the live project, but saving it to " +
                "the .fwdata file failed partway through. This is not a rollback: the file's on-disk " +
                "state is not guaranteed intact. Do not retry automatically -- inspect the project file " +
                "before doing anything else with it.",
                ex);
        }

        return receipt;
    }

    private void EnsurePristineIsFresh(IReadOnlyCollection<Proposal> prerequisites, Proposal proposal)
    {
        if (_pristineCache is null)
        {
            RebuildPristine();
            return;
        }

        foreach (var candidate in prerequisites.Concat(new[] { proposal }))
        {
            var liveFootprint = FootprintProbe.ComputeCurrentFootprintDigest(LiveCache, candidate);
            var pristineFootprint = FootprintProbe.ComputeCurrentFootprintDigest(_pristineCache, candidate);
            if (!string.Equals(liveFootprint, pristineFootprint, StringComparison.Ordinal))
            {
                RebuildPristine();
                return;
            }
        }
    }

    private void RebuildPristine()
    {
        // Save first: the scratch copies the FILE, so an uncommitted live edit would be invisible to it (ADR 0016).
        _loader.Save(LiveCache);

        var newRoot = Path.Combine(_sessionTempRoot, "pristine-" + Guid.NewGuid().ToString("N"));
        LcmCache newCache;
        try
        {
            newCache = _scratchFactory.CreateFromFileCopy(_fwDataPath, newRoot);
        }
        catch
        {
            TryDeleteDirectory(newRoot);
            throw;
        }

        var oldCache = _pristineCache;
        var oldDirectory = _pristineDirectory;

        _pristineCache = newCache;
        _pristineFwDataPath = newCache.ProjectId.Path;
        _pristineDirectory = newRoot;
        PristineRebuildCount++;

        SafeDispose(oldCache);
        if (oldDirectory is not null) TryDeleteDirectory(oldDirectory);
    }

    private void DiscardLiveCache()
    {
        var cache = _liveCache;
        _liveCache = null;
        SafeDispose(cache);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CliSession));
    }

    /// <summary>
    /// Tears the session down: disposes the pristine scratch, then the live cache, deleting temp
    /// directories along the way. Every step is attempted even if an earlier one throws, so a failure
    /// disposing the pristine scratch can never leave the live project's lock held.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SafeDispose(_pristineCache);
        _pristineCache = null;
        if (_pristineDirectory is not null) TryDeleteDirectory(_pristineDirectory);

        SafeDispose(_liveCache);
        _liveCache = null;

        TryDeleteDirectory(_sessionTempRoot);
    }

    private static void SafeDispose(LcmCache? cache)
    {
        try
        {
            if (cache is { IsDisposed: false }) cache.Dispose();
        }
        catch
        {
            // Swallowed so a pristine-dispose failure can never skip the live cache's dispose below.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A still-locked native handle must not fail teardown that already released the project lock.
        }
    }
}
