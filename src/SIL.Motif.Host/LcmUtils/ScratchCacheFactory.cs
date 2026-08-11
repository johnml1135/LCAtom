using SIL.LCModel;

namespace SIL.Motif.Host.LcmUtils;

/// <summary>
/// Creates the throwaway <see cref="LcmCache"/> a Dry Run mutates, by either of the two candidate
/// strategies, so they can be compared rather than assumed
/// (<c>docs/adr/0016-scratch-cache-copy-not-undo.md</c>, grill item <c>A1</c>).
/// </summary>
/// <remarks>
/// <para>
/// Creating a cache is <b>lifecycle</b>, so it belongs to the Host: <c>SIL.Motif.Runner</c> operates on an
/// already-loaded cache it does not own and must stay that way (see <c>AGENTS.md</c> rule 3 and the
/// Runner's own csproj comment). A Dry Run therefore receives a scratch cache; it never makes one.
/// </para>
/// <para>
/// The two strategies are not interchangeable, and the differences are the point:
/// </para>
/// <list type="table">
///   <listheader><term>Aspect</term><description>In-memory copy vs. file copy</description></listheader>
///   <item><term>Unsaved edits</term><description>In-memory reads the source's identity map, so it captures
///   them. A file copy reads what is on disk, so it <b>loses</b> them.</description></item>
///   <item><term>Writing systems</term><description>In-memory synthesizes them from the bare language tag,
///   losing collation, valid characters, fonts and keyboards. A file copy loads the real
///   definitions.</description></item>
///   <item><term><c>cache.Initialize()</c></term><description><c>CreateCacheCopy</c> passes a null
///   initializer, so the in-memory copy skips both the default-writing-system check and
///   <c>DataStoreInitializationServices.PrepareCache</c>. A file load runs both.</description></item>
///   <item><term>Provenance</term><description>The file path is what every project open already does. The
///   in-memory path has one caller in all of liblcm and FieldWorks — a blank-project
///   test.</description></item>
/// </list>
/// </remarks>
public class ScratchCacheFactory
{
    private readonly FwDataProjectLoader _loader;

    public ScratchCacheFactory(FwDataProjectLoader? loader = null)
    {
        _loader = loader ?? new FwDataProjectLoader();
    }

    /// <summary>
    /// <b>NOT THE CANONICAL PATH — measured lossy. Do not use for a Dry Run.</b> Clones
    /// <paramref name="source"/> in memory via <c>LcmCache.CreateCacheCopy</c> into a
    /// <see cref="BackendProviderType.kMemoryOnly"/> target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retained as the comparison point that settled ADR 0016, and because it is the only way to capture the
    /// source's <i>uncommitted</i> edits. It is fast — 78–200 ms from a cold source at Sena-3 scale, against
    /// ~600 ms for <see cref="CreateFromFileCopy"/> — and cost is O(objects), each either a byte-array
    /// reference copy (source surrogate never reconstituted) or a full <c>ToXmlString()</c> (source object
    /// already fluffed), so a hot source costs ~4.2 s.
    /// </para>
    /// <para>
    /// <b>But every <c>kMemoryOnly</c> cache loses its writing-system definitions.</b> Measured on Sena 3:
    /// 0 of 4 writing systems value-equal — valid-character sets 2 → 0, fonts replaced, and the vernacular
    /// <c>seh</c> stripped of its collation rules. This is a property of the target backend, not of the
    /// source: <c>useMemoryWsManager</c> is hardwired for it, so copying from a file-loaded scratch with
    /// perfect writing systems still yields 0 of 4. Collation is what ordering, homograph numbering, and
    /// form-comparison rest on, so a Dry Run here can be silently wrong rather than slow.
    /// </para>
    /// <para>
    /// Using this for a Dry Run requires reopening ADR 0016.
    /// </para>
    /// </remarks>
    public virtual LcmCache CreateInMemoryCopy(LcmCache source, string scratchName = "motif-scratch")
    {
        if (source is null) throw new ArgumentNullException(nameof(source));

        FwDataProjectLoader.Init();

        var projectId = new MemoryOnlyProjectId(scratchName);

        // A memory-only backend never touches these, but LcmCache still requires a directories service.
        var templates = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SIL.Motif.Templates");
        System.IO.Directory.CreateDirectory(templates);
        var dirs = new LcmDirectories(templates, templates);

        var progress = new LcmThreadedProgress();
        return LcmCache.CreateCacheCopy(
            projectId,
            userWsIcuLocale: "en",
            ui: new HeadlessLcmUi(progress.SynchronizeInvoke),
            dirs: dirs,
            settings: new LcmSettings(),
            sourceCache: source);
    }

    /// <summary>
    /// <b>THE CANONICAL PATH</b> (ADR 0016). Copies the project's files to
    /// <paramref name="destinationRoot"/> and opens the copy normally: ~50 ms to copy plus ~550 ms to open at
    /// Sena-3 scale, and the result was equivalent to the live cache on every axis measured — objects,
    /// entries, text, custom-field flids, and all four writing systems value-equal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This reads the project as it exists on disk.</b> Anything the source cache has in memory but has
    /// not saved is absent from the result.
    /// </para>
    /// <para>
    /// <b>So the caller must save first.</b> A save commits what the user already authored; it does not add
    /// anything, while validating against a state the live cache has already left produces an anchor that
    /// is stale on arrival and an apply that reports drift which never happened. ADR 0016 makes
    /// save-before-copy the host precondition. Use <see cref="FwDataProjectLoader.Save"/>, which waits for
    /// the write to actually reach disk — <c>Commit()</c> alone does not, and that asynchrony was found the
    /// hard way.
    /// </para>
    /// </remarks>
    /// <returns>The opened scratch cache. Deleting <paramref name="destinationRoot"/> is the caller's job.</returns>
    public virtual LcmCache CreateFromFileCopy(string sourceFwDataPath, string destinationRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceFwDataPath)) throw new ArgumentException("Required.", nameof(sourceFwDataPath));
        if (string.IsNullOrWhiteSpace(destinationRoot)) throw new ArgumentException("Required.", nameof(destinationRoot));

        var sourceFolder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(sourceFwDataPath))
            ?? throw new ArgumentException($"Could not determine the project folder from '{sourceFwDataPath}'.", nameof(sourceFwDataPath));

        // Preserves FieldWorks' {projectsFolder}/{projectName}/{projectName}.fwdata layout the loader expects.
        var projectName = System.IO.Path.GetFileNameWithoutExtension(sourceFwDataPath);
        var destFolder = System.IO.Path.Combine(destinationRoot, projectName);
        CopyDirectory(sourceFolder, destFolder);

        var destFwData = System.IO.Path.Combine(destFolder, System.IO.Path.GetFileName(sourceFwDataPath));
        if (!System.IO.File.Exists(destFwData))
            throw new System.IO.FileNotFoundException($"Copy did not produce the expected project file.", destFwData);

        return _loader.LoadCache(destFwData);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        System.IO.Directory.CreateDirectory(destDir);
        foreach (var file in System.IO.Directory.GetFiles(sourceDir))
            System.IO.File.Copy(file, System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(file)), overwrite: true);

        foreach (var subDir in System.IO.Directory.GetDirectories(sourceDir))
            CopyDirectory(subDir, System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(subDir)));
    }
}
