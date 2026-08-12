// Adapted from languageforge-lexbox's FwDataMiniLcmBridge/LcmUtils/ProjectLoader.cs (SIL Global, MIT).

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using SIL.LCModel;
using SIL.LCModel.Core.WritingSystems;
using SIL.LCModel.Utils;
using SIL.WritingSystems;

namespace SIL.Motif.Host.LcmUtils;

/// <summary>
/// Opens an existing FieldWorks <c>.fwdata</c> project headlessly: performs the required ICU/SLDR
/// native initialization once per process, then loads the project via
/// <see cref="LcmCache.CreateCacheFromLocalProjectFile"/> with a non-interactive
/// <see cref="ILcmUI"/> and progress shim.
/// </summary>
public class FwDataProjectLoader
{
    private static bool _init;
    private static readonly object InitLock = new();

    /// <summary>
    /// Initializes ICU and SLDR. Idempotent — only runs once per process. Must happen before any
    /// <see cref="LcmCache"/> is created; this is the classic headless-load blocker if skipped or
    /// ordered wrong.
    /// </summary>
    public static void Init()
    {
        if (_init) return;

        lock (InitLock)
        {
            if (_init) return;

            Icu.Wrapper.Init();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Debug.Assert(Icu.Wrapper.IcuVersion == "72.1.0.3");
            }

            Sldr.Initialize();
            _init = true;
        }
    }

    /// <summary>
    /// Opens an existing <c>.fwdata</c> project.
    /// </summary>
    /// <param name="fwDataFilePath">
    /// Full path to the project's .fwdata file. Expected layout is
    /// <c>{projectsFolder}/{projectName}/{projectName}.fwdata</c>, matching FieldWorks' own
    /// project-folder convention.
    /// </param>
    /// <param name="templatesFolder">
    /// Folder LibLCM expects to exist for new-project templates. Its contents are not needed to
    /// open an existing project; defaults to a scratch folder under the temp path.
    /// </param>
    public virtual LcmCache LoadCache(string fwDataFilePath, string? templatesFolder = null)
    {
        Init();

        var projectFolder = Path.GetDirectoryName(Path.GetFullPath(fwDataFilePath))
            ?? throw new ArgumentException($"Could not determine project folder from '{fwDataFilePath}'.", nameof(fwDataFilePath));
        var projectsPath = Path.GetDirectoryName(projectFolder)
            ?? throw new ArgumentException($"Could not determine projects folder from '{fwDataFilePath}'.", nameof(fwDataFilePath));

        templatesFolder ??= Path.Combine(Path.GetTempPath(), "SIL.Motif.Templates");
        if (!Directory.Exists(projectsPath)) Directory.CreateDirectory(projectsPath);
        if (!Directory.Exists(templatesFolder)) Directory.CreateDirectory(templatesFolder);

        var lcmDirectories = new LcmDirectories(projectsPath, templatesFolder);
        var progress = new LcmThreadedProgress();
        return LcmCache.CreateCacheFromLocalProjectFile(
            fwDataFilePath,
            null,
            new HeadlessLcmUi(progress.SynchronizeInvoke),
            lcmDirectories,
            new LcmSettings(),
            progress);
    }

    /// <summary>
    /// Opens an existing <c>.fwdata</c> project the same way <see cref="LoadCache"/> does, except that
    /// disposing the returned cache cannot register a writing-system change in the machine-wide
    /// <c>%ProgramData%\SIL\WritingSystemRepository</c> store.
    /// </summary>
    /// <remarks>
    /// For a throwaway scratch (ADR 0016), that store is the wrong place to write to: a Dry Run scratch
    /// is a proposal being tried and discarded, and disposing it must not touch state shared with every
    /// other project on the machine. See <see cref="DiscardingGlobalWritingSystemRepository"/> for the
    /// mechanism. <see cref="LoadCache"/> itself is unchanged and remains what a real, persisted project
    /// open uses.
    /// </remarks>
    public virtual LcmCache LoadScratchCache(string fwDataFilePath, string? templatesFolder = null)
    {
        using (SuppressGlobalWritingSystemPersistence())
            return LoadCache(fwDataFilePath, templatesFolder);
    }

    // Key SingletonsContainer stores the shared writing-system repository under (BackendProvider.cs).
    private static readonly string GlobalWritingSystemRepositoryKey =
        typeof(CoreGlobalWritingSystemRepository).FullName!;

    // One decoy for the process: the base holds a GlobalMutex, so one per scratch would leak a handle.
    private static readonly DiscardingGlobalWritingSystemRepository SharedDecoy = new();

    // Swaps the decoy in for one cache open, so that cache is wired to it for life (see the decoy's remarks).
    private static IDisposable SuppressGlobalWritingSystemPersistence()
    {
        var restore = SingletonsContainer.Item(GlobalWritingSystemRepositoryKey) as CoreGlobalWritingSystemRepository;
        if (restore is not null) SingletonsContainer.Remove(GlobalWritingSystemRepositoryKey);

        SingletonsContainer.Add(GlobalWritingSystemRepositoryKey, SharedDecoy);
        return new RestoreGlobalWritingSystemRepository(restore);
    }

    // Undoes SuppressGlobalWritingSystemPersistence: later cache opens see the real repository again.
    private sealed class RestoreGlobalWritingSystemRepository : IDisposable
    {
        private readonly CoreGlobalWritingSystemRepository? _restore;

        public RestoreGlobalWritingSystemRepository(CoreGlobalWritingSystemRepository? restore)
        {
            _restore = restore;
        }

        public void Dispose()
        {
            SingletonsContainer.Remove(GlobalWritingSystemRepositoryKey);
            if (_restore is not null) SingletonsContainer.Add(GlobalWritingSystemRepositoryKey, _restore);
        }
    }

    /// <summary>
    /// Persists every committed change in <paramref name="cache"/> to its backing <c>.fwdata</c>
    /// file, and <b>does not return until the bytes are actually there</b>. The core does not save
    /// projects itself (the Change Set contract's Application Receipt semantics):
    /// <c>SIL.Motif.Runner.Apply.ProposalApplier.Apply</c> commits its unit of work but never calls
    /// this — it is the host's job, called only after every unit of work has closed (never while a
    /// task is open — ADR 0005 decision 3; ADR 0006 decision 4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="IActionHandler.Commit"/> call mirrors
    /// <c>FwDataMiniLcmBridge.Api.FwDataMiniLcmApi.Save()</c>
    /// (languageforge-lexbox/backend/FwLite/FwDataMiniLcmBridge/Api/FwDataMiniLcmApi.cs): for this
    /// backend, <c>Commit()</c> is both "close out the undo/redo bookkeeping since the last commit" and
    /// the save trigger — there is no separate LibLCM "save" verb to call instead.
    /// </para>
    /// <para>
    /// <b>But <c>Commit()</c> alone does not put anything on disk.</b>
    /// <c>XMLBackendProvider.PerformCommit</c> (liblcm
    /// <c>Infrastructure/Impl/XMLBackendProvider.cs:458-467</c>) only enqueues a <c>CommitWork</c> item
    /// on a background <c>ConsumerThread</c> and returns; the write lands whenever that thread gets to
    /// it. <c>IUndoStackManager.Save()</c> is no better — <c>UnitOfWorkService.SaveInternal</c> reaches
    /// the same <c>Commit</c>. Nothing in Motif noticed until the Dry Run started copying the project
    /// file immediately after saving and read a file that was still one operation behind, which
    /// surfaced as "footprint drift" on an apply where nothing had drifted.
    /// </para>
    /// <para>
    /// LibLCM's own answer is the <c>CompleteAllCommits()</c> barrier, which waits for that thread to go
    /// idle, and both places liblcm needs the file to be authoritative pair the two calls:
    /// <c>ProjectLockingService.UnlockCurrentProject</c> (<c>DomainServices/ProjectLockingService.cs:37-41</c>)
    /// and <c>ProjectBackupService</c> (<c>:61</c>). So this is the documented sequence, not an invention.
    /// </para>
    /// <para>
    /// <b>The one wart:</b> that barrier is not on Motif's side of the fence. It is declared on the
    /// <c>internal</c> <c>IDataStorer</c>, so the only reachable route is the public
    /// <c>ILcmServiceLocator.DataSetup</c> property — which returns the very same backend-provider
    /// instance (liblcm <c>LcmCache.cs:631</c> casts it: <c>var bep = (IDataStorer)m_serviceLocator.DataSetup;</c>)
    /// — and then its <c>public virtual CompleteAllCommits</c> by reflection. It is one call, resolved
    /// once, and it throws rather than degrading if a future liblcm renames it, because silently
    /// falling back to the asynchronous behaviour would restore a bug whose symptom points at the wrong
    /// component entirely. <b>Making a synchronous save publicly reachable is an upstream ask on
    /// liblcm</b> (ADR 0016), and this reflection is what should be
    /// deleted when it lands.
    /// </para>
    /// </remarks>
    public virtual void Save(LcmCache cache)
    {
        if (cache is null) throw new ArgumentNullException(nameof(cache));

        cache.ActionHandlerAccessor.Commit();
        WaitForPendingWritesToReachDisk(cache);
    }

    private static MethodInfo? _completeAllCommits;

    /// <summary>Blocks until the commit thread drains; see <see cref="Save"/>'s remarks for why.</summary>
    private static void WaitForPendingWritesToReachDisk(LcmCache cache)
    {
        var backend = cache.ServiceLocator.DataSetup;

        var flush = _completeAllCommits ??= backend.GetType().GetMethod(
            "CompleteAllCommits", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);

        if (flush is null)
        {
            throw new InvalidOperationException(
                $"This LibLCM's backend provider ({backend.GetType().FullName}) has no public " +
                "CompleteAllCommits() to wait on, so Save cannot guarantee the .fwdata file is current. " +
                "Refusing rather than returning from a save that has not saved: a Dry Run copies the " +
                "file immediately afterwards, and a stale copy produces a false 'footprint drift' " +
                "report on apply (ADR 0016).");
        }

        flush.Invoke(backend, null);
    }
}
