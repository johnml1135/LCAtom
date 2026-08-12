using SIL.LCModel.Core.WritingSystems;

namespace SIL.Motif.Host.LcmUtils;

/// <summary>
/// A <see cref="CoreGlobalWritingSystemRepository"/> whose writes are no-ops, so any
/// <see cref="SIL.LCModel.LcmCache"/> wired to it cannot register a writing-system change in the
/// machine-wide <c>%ProgramData%\SIL\WritingSystemRepository</c> store.
/// </summary>
/// <remarks>
/// <para>
/// LibLCM has no setting for "open this file-backed project without touching the shared store." The
/// one flag that comes close, <c>IDataSetup.UseMemoryWritingSystemManager</c> (reached by constructing
/// an <see cref="SIL.LCModel.IProjectIdentifier"/> with
/// <see cref="SIL.LCModel.BackendProviderType.kXMLWithMemoryOnlyWsMgr"/>), also skips the project's own
/// local LDML store: <c>BackendProvider.InitializeWritingSystemManager</c> returns before wiring either
/// repository, so every writing system is re-synthesized from its bare language tag. That reproduces
/// the exact loss ADR 0016 measured and rejected for <c>kMemoryOnly</c> — 0 of 4 writing systems
/// value-equal on a ~152k-object project, collation gone — for the sake of skipping a save that a throwaway scratch
/// should skip far more cheaply.
/// </para>
/// <para>
/// This takes the narrower cut. Left wired to the project's real local LDML store, a cache keeps every
/// writing system it would have had anyway; only the shared store it is paired with decides whether a
/// write ever leaves the project folder. Every write to the shared store on this path originates in
/// <c>SIL.WritingSystems.LocalWritingSystemRepositoryBase{T}.OnChangeNotifySharedStore</c>, which reaches
/// the shared store through <see cref="Set"/> or <see cref="Replace"/> and nothing else, so overriding
/// just those two members is sufficient. Every read member (<c>AllWritingSystems</c>, <c>TryGet</c>, …)
/// still falls through to the base implementation, which reads the real shared store, so a scratch
/// backed by this type is indistinguishable from an ordinary one for anything that only reads.
/// </para>
/// <para>
/// <b>One instance serves the whole process, and it is never disposed.</b> A cache opened while this
/// type is swapped in keeps the reference until its own <c>Dispose()</c>, long after the swap has been
/// undone, so the instance has to outlive every scratch. The base type holds a <c>GlobalMutex</c>;
/// constructing one per scratch would therefore leak a named system handle per Dry Run — unnoticeable
/// across one test run, unbounded in a host that stays open. Sharing is safe because the two overrides
/// hold no state and every read still reaches the real store through the base.
/// </para>
/// <para>
/// <b>The swap that installs it is process-wide.</b> A cache opened on another thread during that window
/// would also be wired here and would silently stop persisting writing systems. Every caller today is
/// sequential — the CLI's Dry Run, and a test collection that disables parallelism outright — so this is
/// a constraint on future callers rather than a live defect.
/// </para>
/// </remarks>
internal sealed class DiscardingGlobalWritingSystemRepository : CoreGlobalWritingSystemRepository
{
    /// <inheritdoc />
    public override void Set(CoreWritingSystemDefinition ws)
    {
        // Discarded: this instance exists only to keep a scratch's writes out of the shared store.
    }

    /// <inheritdoc />
    public override void Replace(string languageTag, CoreWritingSystemDefinition newWs)
    {
        // Discarded: this instance exists only to keep a scratch's writes out of the shared store.
    }
}
