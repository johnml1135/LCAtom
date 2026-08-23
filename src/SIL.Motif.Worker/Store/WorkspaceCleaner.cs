using System.Collections.ObjectModel;
using System.Text;

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
    private const string MarkerName = ".motif-worker-root";
    private const string MarkerValue = "SIL.Motif.WorkerRoot.v1";

    public WorkspaceOwnership(string workerRoot)
    {
        WorkerRoot = Normalize(workerRoot);
        ValidateNarrowRoot(WorkerRoot);
        var marker = Path.Combine(WorkerRoot, MarkerName);
        if (!Directory.Exists(WorkerRoot) || !File.Exists(marker) ||
            !string.Equals(File.ReadAllText(marker, Encoding.UTF8).Trim(), MarkerValue, StringComparison.Ordinal))
            throw new ArgumentException("The worker root must be explicitly bootstrapped.", nameof(workerRoot));
        if ((File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("A reparse-point worker marker is refused.", nameof(workerRoot));
    }

    public static WorkspaceOwnership Bootstrap(string workerRoot)
    {
        var normalized = Normalize(workerRoot);
        ValidateNarrowRoot(normalized);
        Directory.CreateDirectory(normalized);
        var marker = Path.Combine(normalized, MarkerName);
        if (!File.Exists(marker))
        {
            try
            {
                using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(MarkerValue);
            }
            catch (IOException) when (File.Exists(marker)) { }
        }
        return new WorkspaceOwnership(normalized);
    }

    public string WorkerRoot { get; }

    public bool IsOwned(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return false; }
        var relative = Path.GetRelativePath(WorkerRoot, full);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) ||
            Path.IsPathRooted(relative)) return false;
        var current = WorkerRoot;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException) { return false; }
        }
        return true;
    }

    private static string Normalize(string workerRoot)
    {
        try
        {
            return Path.GetFullPath(workerRoot ?? throw new ArgumentNullException(nameof(workerRoot)))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new ArgumentException("A narrow worker root is required.", nameof(workerRoot), exception);
        }
    }

    private static void ValidateNarrowRoot(string path)
    {
        if (string.IsNullOrEmpty(path) ||
            string.Equals(path, Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) || IsForbidden(path) || File.Exists(path))
            throw new ArgumentException("A narrow worker root is required.", nameof(path));
        for (var current = path; !string.IsNullOrEmpty(current); current = Directory.GetParent(current)?.FullName)
        {
            if (!Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("A reparse-point ancestor cannot contain a worker root.", nameof(path));
        }
        if (!Directory.Exists(path)) return;
        if (Directory.EnumerateFiles(path, "*.fwdata", SearchOption.TopDirectoryOnly).Any() ||
            Directory.EnumerateFiles(path, "*.motif.db", SearchOption.TopDirectoryOnly).Any())
            throw new ArgumentException("A project or database directory cannot be a worker root.", nameof(path));
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
                if (!IsLexicallyContained(work, child) || !_ownership.IsOwned(child))
                {
                    failures.Add(new WorkspaceCleanupFailure(child, "A startup entry is outside the exact work root."));
                    continue;
                }
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
            DeleteTree(target, deleted);
        }
        catch (UnauthorizedAccessException exception)
        {
            failures.Add(new WorkspaceCleanupFailure(target, "Cleanup target could not be deleted.", exception));
        }
        catch (IOException exception)
        {
            failures.Add(new WorkspaceCleanupFailure(target, "Cleanup target could not be deleted.", exception));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            failures.Add(new WorkspaceCleanupFailure(target, "Cleanup target could not be inspected.", exception));
        }
        catch (Exception exception)
        {
            failures.Add(new WorkspaceCleanupFailure(target, "Cleanup target could not be deleted.", exception));
        }
        return new WorkspaceCleanupResult(deleted, failures);
    }

    private void DeleteTree(string target, List<string> deleted)
    {
        if (!_ownership.IsOwned(target))
            throw new InvalidOperationException("A cleanup entry is outside the worker-owned root.");
        if (!_fileSystem.Exists(target)) return;
        var attributes = _fileSystem.GetAttributes(target);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("A reparse cleanup entry is refused.");
        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (var entry in _fileSystem.EnumerateFileSystemEntries(target))
            {
                if (!IsLexicallyContained(target, entry) || !_ownership.IsOwned(entry))
                    throw new InvalidOperationException("A cleanup entry is outside the exact work root.");
                DeleteTree(entry, deleted);
            }
            attributes = _fileSystem.GetAttributes(target);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A reparse cleanup entry is refused.");
            _fileSystem.DeleteDirectory(target);
        }
        else
        {
            attributes = _fileSystem.GetAttributes(target);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A reparse cleanup entry is refused.");
            _fileSystem.DeleteFile(target);
        }
        deleted.Add(target);
    }

    private static bool IsLexicallyContained(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative != "." && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) &&
            !Path.IsPathRooted(relative);
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
        public void DeleteDirectory(string path) => Directory.Delete(path, false);
    }
}
