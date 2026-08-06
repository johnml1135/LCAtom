using SIL.Motif.Contract.Model;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Runner.DryRun;
using SIL.LCModel;
using DryRunModel = SIL.Motif.Model.DryRun.DryRun;

namespace SIL.Motif.Tests.TestFixtures;

/// <summary>
/// Runs a Dry Run the way a host must: save, copy the project's files, open the copy, mutate the copy,
/// discard it. See <c>docs/adr/0016-scratch-cache-copy-not-undo.md</c> as amended 2026-08-06.
/// </summary>
/// <remarks>
/// <para>
/// Tests used to call <c>ProposalDryRunner.Run(_cache, proposal)</c> against their own cache, which was
/// safe only because Run rolled back — and that rollback was the thing that left LibLCM's derived caches
/// stale. Now Run demands a <see cref="DryRunScratch"/> and never reverts it, so a test that wants both
/// a Dry Run and a subsequent Apply against the same project needs the two to happen on different
/// caches. This helper is that, and it is deliberately the same three steps production performs rather
/// than a shortcut, so the tests exercise the real sequence.
/// </para>
/// <para>
/// <b>Why it saves first.</b> The copy is a copy of the file, so anything a test arranged in memory and
/// did not commit would be invisible to it — the Dry Run would read a baseline the caller's cache is
/// not in, and Apply would then reject the resulting anchor as drift. Saving is the host precondition
/// ADR 0016 names, and having the tests obey it is what proves it is sufficient.
/// </para>
/// <para>
/// It costs one project open per call. That is the honest price of not rolling back, and it buys the
/// cross-instance guarantee <see cref="Apply.AnchorsAreCrossCachePortableTests"/> pins down: because
/// the footprint digest is built from canonical ids and field values rather than hvos, an anchor bound
/// on the copy still matches the original.
/// </para>
/// </remarks>
internal static class ScratchDryRun
{
    /// <summary>
    /// Dry-runs <paramref name="proposal"/> on a single-use file copy of whatever project
    /// <paramref name="projectCache"/> has open, leaving that cache untouched and appliable.
    /// </summary>
    public static DryRunModel Of(LcmCache projectCache, Proposal proposal)
    {
        if (projectCache is null) throw new ArgumentNullException(nameof(projectCache));

        var fwDataPath = projectCache.ProjectId.Path;
        if (string.IsNullOrWhiteSpace(fwDataPath) || !File.Exists(fwDataPath))
        {
            throw new InvalidOperationException(
                $"Cannot build a scratch from this cache: its ProjectId.Path is '{fwDataPath}', which is " +
                "not a file on disk. Only file-backed projects can be copied (a kMemoryOnly cache has no " +
                "path, and its writing systems are degraded anyway — ADR 0016).");
        }

        // The host precondition, exercised rather than assumed: the copy is of the file, so commit first.
        new FwDataProjectLoader().Save(projectCache);

        var scratchRoot = Path.Combine(
            Path.GetTempPath(), "SIL.Motif.Tests.Scratch", Guid.NewGuid().ToString("N"));

        using var scratch = DryRunScratch.Adopt(
            new ScratchCacheFactory().CreateFromFileCopy(fwDataPath, scratchRoot),
            $"test file copy of {fwDataPath}",
            onDisposed: () =>
            {
                try { Directory.Delete(scratchRoot, recursive: true); }
                catch { /* best effort: a locked handle must not fail the test */ }
            });

        return ProposalDryRunner.Run(scratch, proposal);
    }
}
