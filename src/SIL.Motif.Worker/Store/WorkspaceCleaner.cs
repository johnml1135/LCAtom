using System.Collections.ObjectModel;

namespace SIL.Motif.Worker.Store;

/// <summary>Verifies that a path is inside the worker-owned derived workspace.</summary>
public interface IWorkspaceOwnership
{
    string WorkerRoot { get; }
    bool IsOwned(string path);
}

/// <summary>Filesystem operations used by destructive cleanup, injectable for failure tests.</summary>
public interface IWorkspaceFileSystem
{
    bool Exists(string path);
    FileAttributes GetAttributes(string path);
    IReadOnlyList<string> EnumerateFileSystemEntries(string path);
    void DeleteFile(string path);
    void DeleteDirectory(string path);
}

/// <summary>Default ownership and filesystem boundaries for Motif derived work.</summary>
public sealed class WorkspaceOwnership : IWorkspaceOwnership
{
    public WorkspaceOwnership(string workerRoot)
    {
        WorkerRoot = Path.GetFullPath(workerRoot ?? throw new ArgumentNullException(nameof(workerRoot)))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(WorkerRoot)) throw new ArgumentException("A narrow worker root is required.", nameof(workerRoot));
    }

    public string WorkerRoot { get; }

    public bool IsOwned(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (IsForbidden(full)) return false;
        var relative = Path.GetRelativePath(WorkerRoot, full);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) ||
            Path.IsPathRooted(relative)) return false;
        var current = WorkerRoot;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return false;
        }
        return true;
    }

    private static bool IsForbidden(string path) =>
        path.EndsWith(".fwdata", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".motif.db", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reports each exact cleanup deletion and any failure that prevented it.</summary>
public sealed record WorkspaceCleanupFailure(string Path, string Message, Exception? Exception = null);

/// <summary>Result of a cleanup pass; failures are returned rather than hidden.</summary>
public sealed record WorkspaceCleanupResult(
    IReadOnlyList<string> DeletedPaths,
    IReadOnlyList<WorkspaceCleanupFailure> Failures)
{
    public bool Succeeded => Failures.Count == 0;
}

/// <summary>Deletes only exact worker-owned ephemeral work paths.</summary>
public sealed class WorkspaceCleaner
{
    private readonly IWorkspaceOwnership _ownership;
    private readonly IWorkspaceFileSystem _fileSystem;

    public WorkspaceCleaner(IWorkspaceOwnership ownership, IWorkspaceFileSystem? fileSystem = null)
    {
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        _fileSystem = fileSystem ?? new SystemWorkspaceFileSystem();
    }

    public WorkspaceCleanupResult CleanupJob(string projectKey, string jobId)
    {
        if (!IsSafeSegment(projectKey) || !IsSafeSegment(jobId))
            return Failure(Path.Combine(_ownership.WorkerRoot, "work"), "Project and job keys must be one safe path segment.");
        var workspace = Path.Combine(_ownership.WorkerRoot, projectKey);
        var work = Path.Combine(workspace, "work");
        var target = Path.Combine(work, jobId);
        if (!ValidateWorkTarget(work, target, out var error)) return Failure(target, error!);
        return DeleteExact(target);
    }

    public WorkspaceCleanupResult CleanupStartup(string projectKey)
    {
        if (!IsSafeSegment(projectKey))
            return Failure(_ownership.WorkerRoot, "Project key must be one safe path segment.");
        var workspace = Path.Combine(_ownership.WorkerRoot, projectKey);
        var work = Path.Combine(workspace, "work");
        if (!ValidateWorkTarget(work, work, out var error, allowRoot: true)) return Failure(work, error!);
        var deleted = new List<string>();
        var failures = new List<WorkspaceCleanupFailure>();
        try
        {
            if (!_fileSystem.Exists(work)) return new WorkspaceCleanupResult(deleted, failures);
            foreach (var child in _fileSystem.EnumerateFileSystemEntries(work))
            {
                var result = DeleteExact(child);
                deleted.AddRange(result.DeletedPaths);
                failures.AddRange(result.Failures);
            }
        }
        catch (Exception exception)
        {
            failures.Add(new WorkspaceCleanupFailure(work, "Unable to enumerate owned work.", exception));
        }
        return new WorkspaceCleanupResult(new ReadOnlyCollection<string>(deleted), new ReadOnlyCollection<WorkspaceCleanupFailure>(failures));
    }

    private WorkspaceCleanupResult DeleteExact(string target)
    {
        var deleted = new List<string>();
        var failures = new List<WorkspaceCleanupFailure>();
        try
        {
            if (!_fileSystem.Exists(target)) return new WorkspaceCleanupResult(deleted, failures);
            if ((_fileSystem.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            {
                failures.Add(new WorkspaceCleanupFailure(target, "Reparse-point cleanup target is refused."));
                return new WorkspaceCleanupResult(deleted, failures);
            }
            if ((_fileSystem.GetAttributes(target) & FileAttributes.Directory) != 0)
                _fileSystem.DeleteDirectory(target);
            else
                _fileSystem.DeleteFile(target);
            deleted.Add(target);
        }
        catch (UnauthorizedAccessException exception)
        {
            failures.Add(new WorkspaceCleanupFailure(target, "Cleanup target could not be deleted.", exception));
        }
        catch (IOException exception)
        {
            failures.Add(new WorkspaceCleanupFailure(target, "Cleanup target could not be deleted.", exception));
        }
        return new WorkspaceCleanupResult(deleted, failures);
    }

    private bool ValidateWorkTarget(string work, string target, out string? error, bool allowRoot = false)
    {
        error = null;
        if (!_ownership.IsOwned(work) || (!allowRoot && !_ownership.IsOwned(target)))
        {
            error = "The cleanup path is outside the worker-owned root.";
            return false;
        }
        var relative = Path.GetRelativePath(work, target);
        if (!allowRoot && (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative)))
        {
            error = "The cleanup target is not beneath the exact work root.";
            return false;
        }
        if (Path.GetFileName(work).Equals(".fwdata", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(work).Equals(".motif.db", StringComparison.OrdinalIgnoreCase))
        {
            error = "A project or database path cannot be a cleanup root.";
            return false;
        }
        return true;
    }

    private static bool IsSafeSegment(string value) => !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") &&
        value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) < 0;

    private static WorkspaceCleanupResult Failure(string path, string message) =>
        new([], [new WorkspaceCleanupFailure(path, message)]);

    private sealed class SystemWorkspaceFileSystem : IWorkspaceFileSystem
    {
        public bool Exists(string path) => Directory.Exists(path) || File.Exists(path);
        public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
        public IReadOnlyList<string> EnumerateFileSystemEntries(string path) => Directory.GetFileSystemEntries(path);
        public void DeleteFile(string path) => File.Delete(path);
        public void DeleteDirectory(string path) => Directory.Delete(path, true);
    }
}
