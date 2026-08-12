using System.Collections.Concurrent;
using SIL.LCModel;
using SIL.Motif.Host.LcmUtils;

namespace SIL.Motif.Tests.TestFixtures;

/// <summary>
/// One blank, seeded project built and saved once for the whole run, from which each test opens a
/// cheap private copy instead of paying to create its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists:</b> creating a project costs about 3.9 seconds; copying the saved 48 KB of it
/// and reopening costs about 35 milliseconds. xUnit builds a fresh instance of a test class per test
/// method, so a per-class cache makes every method pay the 3.9 seconds — minutes of a run spent
/// re-creating a project none of them modified.
/// </para>
/// <para>
/// <b>Each copy is file-backed, and that is the whole design.</b> The obvious cheaper move is
/// <see cref="ScratchCacheFactory.CreateInMemoryCopy"/>, which is just as fast — and wrong here twice
/// over. A <c>kMemoryOnly</c> cache has no <c>ProjectId.Path</c>, so any test performing a Dry Run
/// fails outright on the guard in <c>ScratchDryRun</c>; and it synthesizes writing systems from the
/// bare language tag, losing collation, fonts and valid characters. Copying files and reopening keeps
/// both properties, for the same cost.
/// </para>
/// <para>
/// Copies are isolated from each other and from the master, which is never reopened after it is saved.
/// Their directories are deleted when the collection finishes rather than per test, so a failing test
/// leaves its project on disk to inspect.
/// </para>
/// </remarks>
public sealed class PristineProjectFixture : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _masterFolder;
    private readonly ConcurrentBag<string> _scratchRoots = new();
    private int _next;

    public PristineProjectFixture()
    {
        GuardAgainstStaleWritingSystemStashFiles();

        _tempRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.Tests.Pristine", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        var master = NewLangProjFixture.CreateCache(_tempRoot);
        try
        {
            Seed = SeededProject.Seed(master);
            new FwDataProjectLoader().Save(master);
            _masterFolder = Path.GetDirectoryName(master.ProjectId.Path)!;
        }
        finally
        {
            master.Dispose();
        }
    }

    /// <summary>Identity of everything <see cref="SeededProject"/> wrote, valid in every copy.</summary>
    public SeededProject Seed { get; }

    // A leftover WS stash file silently adds ~1.85s to every LcmCache.Dispose() in the run (message below).
    private static void GuardAgainstStaleWritingSystemStashFiles()
    {
        string[] staleFiles;
        try
        {
            var repoDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SIL", "WritingSystemRepository", "3");
            if (!Directory.Exists(repoDir)) return;
            staleFiles = Directory.GetFiles(repoDir, "*.localrepoupdate");
        }
        catch
        {
            return; // an unreadable directory is not evidence of the fault this guard checks for
        }

        if (staleFiles.Length == 0) return;

        throw new InvalidOperationException(
            $"Found {staleFiles.Length} stale '*.localrepoupdate' file(s) in the machine-wide writing-system " +
            "repository:\n  " + string.Join("\n  ", staleFiles) + "\n\n" +
            "These are stash files GlobalWritingSystemRepository's WsStasher leaves behind when a process " +
            "dies between its constructor (which copies the live LDML aside) and its Dispose() (which moves " +
            "it back). Their mere presence makes every later WritingSystemManager.Save() — which every " +
            "LcmCache.Dispose() triggers — collide on the copy, retry 10 times at 200 ms apart, fail, and " +
            "have that failure swallowed silently. That is about 1.85 seconds added to EVERY cache disposal " +
            "in this run, with no error printed anywhere.\n\n" +
            "Delete the file(s) listed above, then re-run.");
    }

    /// <summary>
    /// Opens a private, file-backed copy of the seeded project. The caller disposes the cache; the
    /// directory behind it is cleaned up when the whole collection finishes.
    /// </summary>
    public LcmCache NewScratch()
    {
        var scratchRoot = Path.Combine(_tempRoot, "scratch-" + Interlocked.Increment(ref _next));
        var projectFolder = Path.Combine(scratchRoot, NewLangProjFixture.ProjectName);
        Directory.CreateDirectory(projectFolder);
        _scratchRoots.Add(scratchRoot);

        CopyDirectory(_masterFolder, projectFolder);

        return new FwDataProjectLoader().LoadCache(
            Path.Combine(projectFolder, NewLangProjFixture.ProjectName + ".fwdata"));
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);

        foreach (var subDir in Directory.GetDirectories(sourceDir))
            CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best effort: a locked native handle should not fail the run */ }
    }
}
