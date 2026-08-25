using System.Text;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Worker.PanGloss;

/// <summary>
/// One disposable directory that holds a candidate export and its Assessment output for exactly one
/// PanGloss attempt, and nothing durable behind it.
/// </summary>
/// <remarks>
/// <see cref="Create"/> also records a small marker file in a registry separate from the workspace
/// directory itself, so a locked or partially deleted payload can never erase the record that this
/// name was ours: <see cref="SweepStartup"/> only ever retires the marker once the payload directory it
/// names is confirmed gone, and otherwise leaves both in place for the next start to retry.
/// </remarks>
public sealed class PanGlossWorkspace : IDisposable
{
    private const string RootSegment = "pangloss";
    private const string WorkSegment = "work";
    private const string MarkersSegment = "markers";
    private const string MarkerExtension = ".marker";
    private const string MarkerValue = "SIL.Motif.PanGlossWorkspace.v1";

    private readonly IWorkspaceOwnership _ownership;
    private readonly string _name;
    private readonly string _markerPath;

    private PanGlossWorkspace(IWorkspaceOwnership ownership, string name, string root, string markerPath)
    {
        _ownership = ownership;
        _name = name;
        _markerPath = markerPath;
        Root = root;
    }

    /// <summary>The exact directory this attempt may write to; nothing else is ever touched.</summary>
    public string Root { get; }

    /// <summary>
    /// Allocates a fresh, empty, marked workspace directory for <paramref name="name"/> under
    /// <paramref name="ownership"/>'s worker root.
    /// </summary>
    /// <param name="ownership">The verified worker root every workspace is created under.</param>
    /// <param name="name">
    /// A caller-chosen identifier for this attempt (for example a job id). Validated as one safe path
    /// segment before it is combined into a path, so a crafted value cannot select a directory outside
    /// the worker root.
    /// </param>
    public static PanGlossWorkspace Create(IWorkspaceOwnership ownership, string name)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        if (!IsSafeSegment(name))
            throw new ArgumentException("A PanGloss workspace name must be one safe path segment.", nameof(name));

        var root = Path.Combine(ownership.WorkerRoot, RootSegment, WorkSegment, name);
        var marker = Path.Combine(ownership.WorkerRoot, RootSegment, MarkersSegment, name + MarkerExtension);
        if (!ownership.IsOwned(root) || !ownership.IsOwned(marker))
        {
            throw new ArgumentException(
                "The PanGloss workspace path is refused: it resolves outside the worker-owned root or " +
                "through a reparse point.", nameof(name));
        }
        if (Directory.Exists(root) || File.Exists(root))
            throw new InvalidOperationException($"A PanGloss workspace already exists at '{root}'.");

        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("A reparse-point workspace root is refused.");

        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, MarkerValue, new UTF8Encoding(false));
        return new PanGlossWorkspace(ownership, name, root, marker);
    }

    /// <summary>
    /// Deletes this workspace's payload now, then retires its marker only once the payload is
    /// confirmed gone. A failure — for example a file another process still has open — is left in
    /// place rather than thrown; the surviving marker lets <see cref="SweepStartup"/> find and finish
    /// removing it on the next start.
    /// </summary>
    public void CompleteAndDelete()
    {
        var result = new WorkspaceCleaner(_ownership).CleanupJob(RootSegment, _name);
        if (result.Succeeded) TryDeleteMarker(_markerPath, null);
    }

    /// <summary>Deletes this workspace if it was not already completed.</summary>
    public void Dispose() => CompleteAndDelete();

    /// <summary>
    /// Removes every marked PanGloss workspace under <paramref name="ownership"/>'s worker root. Meant
    /// to run once at startup, before any job is requeued, so a leftover payload from an attempt that
    /// skipped <see cref="CompleteAndDelete"/> — most notably a worker crash — does not persist past
    /// the next start. Pinned by `SweepStartup_RemovesAWorkspaceOrphanedByAWorkerCrash`.
    /// </summary>
    public static WorkspaceCleanupResult SweepStartup(IWorkspaceOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        var markersDirectory = Path.Combine(ownership.WorkerRoot, RootSegment, MarkersSegment);
        var deleted = new List<string>();
        var failures = new List<WorkspaceCleanupFailure>();
        if (!Directory.Exists(markersDirectory)) return new WorkspaceCleanupResult(deleted, failures);

        IReadOnlyList<string> markers;
        try
        {
            markers = Directory.GetFiles(markersDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(new WorkspaceCleanupFailure(
                markersDirectory, "Unable to enumerate PanGloss workspace markers.", exception));
            return new WorkspaceCleanupResult(deleted, failures);
        }

        var cleaner = new WorkspaceCleaner(ownership);
        foreach (var markerPath in markers)
        {
            var name = Path.GetFileNameWithoutExtension(markerPath);
            if (!string.Equals(Path.GetExtension(markerPath), MarkerExtension, StringComparison.OrdinalIgnoreCase) ||
                !IsSafeSegment(name) || !ownership.IsOwned(markerPath) || !HasValidMarkerContent(markerPath))
            {
                failures.Add(new WorkspaceCleanupFailure(markerPath, "Entry is not a recognized PanGloss workspace marker."));
                continue;
            }
            var workPath = Path.Combine(ownership.WorkerRoot, RootSegment, WorkSegment, name);
            if (!ownership.IsOwned(workPath))
            {
                failures.Add(new WorkspaceCleanupFailure(workPath, "Marked workspace path escapes the worker-owned root."));
                continue;
            }
            var result = cleaner.CleanupJob(RootSegment, name);
            deleted.AddRange(result.DeletedPaths);
            failures.AddRange(result.Failures);
            if (result.Succeeded) TryDeleteMarker(markerPath, failures);
        }
        return new WorkspaceCleanupResult(deleted, failures);
    }

    private static void TryDeleteMarker(string markerPath, List<WorkspaceCleanupFailure>? failures)
    {
        try
        {
            File.Delete(markerPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures?.Add(new WorkspaceCleanupFailure(
                markerPath, "The workspace was removed but its marker could not be deleted.", exception));
        }
    }

    private static bool HasValidMarkerContent(string markerPath)
    {
        try
        {
            return string.Equals(File.ReadAllText(markerPath, Encoding.UTF8).Trim(), MarkerValue, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSafeSegment(string value) => !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") &&
        value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) < 0;
}
