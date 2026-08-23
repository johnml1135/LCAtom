using System.Globalization;
using SIL.Motif.Contract.Jobs;

namespace SIL.Motif.Worker.Store;

/// <summary>Supplies the current worker lease state for one derived project workspace.</summary>
public interface IProjectWorkspaceLease
{
    bool HasLiveLease(string projectKey);
}

/// <summary>Supplies durable references that keep a derived workspace alive.</summary>
public interface IProjectWorkspaceReferences
{
    bool HasDurableReference(string projectKey);
}

/// <summary>Supplies the last durable use time for one derived project workspace.</summary>
public interface IProjectWorkspaceLastUsed
{
    DateTimeOffset? LastUsedUtc(string projectKey);
}

/// <summary>Reports exact project-key workspace eviction outcomes.</summary>
public sealed record WorkspaceEvictionResult(
    IReadOnlyList<string> EvictedPaths,
    IReadOnlyList<WorkspaceCleanupFailure> Failures);

/// <summary>Evicts unused derived workspaces without touching project-owned siblings.</summary>
public sealed class ProjectWorkspaceEvictor
{
    private readonly IWorkspaceOwnership _ownership;
    private readonly IProjectWorkspaceLease _leases;
    private readonly IProjectWorkspaceReferences _references;
    private readonly IProjectWorkspaceLastUsed _lastUsed;
    private readonly IWorkspaceFileSystem _fileSystem;
    private readonly IJobClock _clock;
    private readonly TimeSpan _disuse;

    public ProjectWorkspaceEvictor(
        IWorkspaceOwnership ownership,
        IProjectWorkspaceLease leases,
        IProjectWorkspaceReferences references,
        IProjectWorkspaceLastUsed lastUsed,
        IJobClock? clock = null,
        TimeSpan? disuse = null,
        IWorkspaceFileSystem? fileSystem = null)
    {
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _references = references ?? throw new ArgumentNullException(nameof(references));
        _lastUsed = lastUsed ?? throw new ArgumentNullException(nameof(lastUsed));
        _clock = clock ?? new SystemJobClock();
        _disuse = disuse ?? TimeSpan.FromDays(30);
        if (_disuse < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(disuse));
        _fileSystem = fileSystem ?? new ReflectionWorkspaceFileSystem();
    }

    public WorkspaceEvictionResult Evict(string projectKey)
    {
        var failures = new List<WorkspaceCleanupFailure>();
        var evicted = new List<string>();
        if (!IsSafeSegment(projectKey)) return new([], [new(_ownership.WorkerRoot, "Project key is not a safe segment.")]);
        var workspace = Path.Combine(_ownership.WorkerRoot, projectKey);
        if (!_ownership.IsOwned(workspace)) return new([], [new(workspace, "Workspace is outside the owned root.")]);
        if (_leases.HasLiveLease(projectKey) || _references.HasDurableReference(projectKey)) return new(evicted, failures);
        var last = _lastUsed.LastUsedUtc(projectKey);
        if (last is null || _clock.UtcNow < last.Value.ToUniversalTime().Add(_disuse)) return new(evicted, failures);
        try
        {
            if (!_fileSystem.Exists(workspace)) return new(evicted, failures);
            if ((_fileSystem.GetAttributes(workspace) & FileAttributes.ReparsePoint) != 0)
                return new([], [new(workspace, "Reparse-point workspace is refused.")]);
            _fileSystem.DeleteDirectory(workspace);
            evicted.Add(workspace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(new WorkspaceCleanupFailure(workspace, "Workspace eviction failed.", exception));
        }
        return new(evicted, failures);
    }

    private static bool IsSafeSegment(string value) => !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") &&
        value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) < 0;

    private sealed class ReflectionWorkspaceFileSystem : IWorkspaceFileSystem
    {
        public bool Exists(string path) => Directory.Exists(path) || File.Exists(path);
        public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
        public IReadOnlyList<string> EnumerateFileSystemEntries(string path) => Directory.GetFileSystemEntries(path);
        public void DeleteFile(string path) => File.Delete(path);
        public void DeleteDirectory(string path) => Directory.Delete(path, true);
    }
}
