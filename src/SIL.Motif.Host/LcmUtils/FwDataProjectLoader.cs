// Adapted from FwDataMiniLcmBridge, Copyright (c) SIL Global, licensed under the MIT License.
// Source: languageforge-lexbox/backend/FwLite/FwDataMiniLcmBridge/LcmUtils/ProjectLoader.cs
// https://github.com/sillsdev/languageforge-lexbox
//
// Simplified for Motif Stage A: no DI/config plumbing, and only the "open an existing project"
// path (no NewProject). Reintroduce those only if a later stage needs them.

using System.Diagnostics;
using System.Runtime.InteropServices;
using SIL.LCModel;
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
    /// Persists every committed change in <paramref name="cache"/> to its backing <c>.fwdata</c>
    /// file. The core does not save projects itself (docs/change-set-contract.md, "Application
    /// Receipt"): <c>SIL.Motif.Runner.Apply.ProposalApplier.Apply</c> commits its unit of work but
    /// never calls this — it is the host's job, called only after every unit of work has closed
    /// (never while a task is open — docs/adr/0005, decision 3; docs/adr/0006, decision 4).
    /// </summary>
    /// <remarks>
    /// Mirrors <c>FwDataMiniLcmBridge.Api.FwDataMiniLcmApi.Save()</c>
    /// (languageforge-lexbox/backend/FwLite/FwDataMiniLcmBridge/Api/FwDataMiniLcmApi.cs), which
    /// calls the same <see cref="IActionHandler.Commit"/> to flush the XML/.fwdata backend
    /// provider's pending writes to disk: for this backend, <c>Commit()</c> is both "close out the
    /// undo/redo bookkeeping since the last commit" and the save trigger — there is no separate
    /// LibLCM "save" verb to call instead.
    /// </remarks>
    public virtual void Save(LcmCache cache)
    {
        if (cache is null) throw new ArgumentNullException(nameof(cache));

        cache.ActionHandlerAccessor.Commit();
    }
}
