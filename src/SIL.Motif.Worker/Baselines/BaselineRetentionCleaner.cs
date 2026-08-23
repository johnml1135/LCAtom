using SIL.Motif.Contract.Jobs;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Worker.Baselines;

/// <summary>One published Baseline file and its retention eligibility facts.</summary>
public sealed record PublishedBaseline(
    string Path,
    DateTimeOffset PublishedUtc,
    bool Superseded,
    bool RetentionEligible,
    BaselinePinSources PinSources = BaselinePinSources.None);

[Flags]
public enum BaselinePinSources
{
    None = 0,
    ActiveJob = 1,
    DryRun = 2,
    Decision = 4,
    Receipt = 8
}

/// <summary>Provides the durable source categories that pin a Baseline.</summary>
public interface IBaselinePinQuery
{
    BaselinePinSources GetPinSources(string baselinePath);
}

/// <summary>Supplies exact published Baseline files eligible for retention evaluation.</summary>
public interface IPublishedBaselineQuery
{
    IReadOnlyList<PublishedBaseline> ListPublished(string projectKey);
}

/// <summary>Reports exact Baseline deletions and surfaced failures.</summary>
public sealed record BaselineRetentionResult(
    IReadOnlyList<string> DeletedPaths,
    IReadOnlyList<WorkspaceCleanupFailure> Failures);

/// <summary>Deletes only superseded, unpinned, retention-eligible Baseline files.</summary>
public sealed class BaselineRetentionCleaner
{
    private readonly IWorkspaceOwnership _ownership;
    private readonly IPublishedBaselineQuery _published;
    private readonly IBaselinePinQuery _references;
    private readonly IJobClock _clock;
    private readonly ArchivePolicy _policy;
    private readonly IWorkspaceFileSystem _fileSystem;

    public BaselineRetentionCleaner(
        IWorkspaceOwnership ownership,
        IPublishedBaselineQuery published,
        IBaselinePinQuery references,
        ArchivePolicy? policy = null,
        IJobClock? clock = null,
        IWorkspaceFileSystem? fileSystem = null)
    {
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        _published = published ?? throw new ArgumentNullException(nameof(published));
        _references = references ?? throw new ArgumentNullException(nameof(references));
        _policy = policy ?? ArchivePolicy.Default;
        _clock = clock ?? new SystemJobClock();
        _fileSystem = fileSystem ?? new LocalFileSystem();
    }

    public BaselineRetentionResult Clean(string projectKey)
    {
        var deleted = new List<string>();
        var failures = new List<WorkspaceCleanupFailure>();
        if (!IsSafeSegment(projectKey))
            return new([], [new WorkspaceCleanupFailure(_ownership.WorkerRoot, "Project key is not a safe segment.")]);
        foreach (var baseline in _published.ListPublished(projectKey))
        {
            if (!baseline.Superseded || !baseline.RetentionEligible ||
                !_policy.ShouldPurge(baseline.PublishedUtc, _clock.UtcNow) ||
                baseline.PinSources != BaselinePinSources.None ||
                (_references.GetPinSources(baseline.Path) != BaselinePinSources.None))
                continue;
            if (!IsExactPublishedPath(projectKey, baseline.Path))
            {
                failures.Add(new WorkspaceCleanupFailure(baseline.Path, "Baseline path is outside the exact published directory."));
                continue;
            }
            try
            {
                if (_fileSystem.Exists(baseline.Path))
                {
                    if ((_fileSystem.GetAttributes(baseline.Path) & FileAttributes.ReparsePoint) != 0)
                    {
                        failures.Add(new WorkspaceCleanupFailure(baseline.Path, "Reparse-point Baseline is refused."));
                        continue;
                    }
                    _fileSystem.DeleteFile(baseline.Path);
                    deleted.Add(baseline.Path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
            {
                failures.Add(new WorkspaceCleanupFailure(baseline.Path, "Baseline deletion failed.", exception));
            }
            catch (Exception exception)
            {
                failures.Add(new WorkspaceCleanupFailure(baseline.Path, "Baseline deletion failed.", exception));
            }
        }
        return new BaselineRetentionResult(deleted, failures);
    }

    private bool IsExactPublishedPath(string projectKey, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !_ownership.IsOwned(path)) return false;
        var expected = Path.Combine(_ownership.WorkerRoot, projectKey, "baseline");
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return false; }
        var relative = Path.GetRelativePath(expected, full);
        return relative != "." && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) &&
            !Path.IsPathRooted(relative) && relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;
    }

    private static bool IsSafeSegment(string value) => !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") &&
        value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) < 0;

    private sealed class LocalFileSystem : IWorkspaceFileSystem
    {
        public bool Exists(string path) => Directory.Exists(path) || File.Exists(path);
        public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
        public IReadOnlyList<string> EnumerateFileSystemEntries(string path) => Directory.GetFileSystemEntries(path);
        public void DeleteFile(string path) => File.Delete(path);
        public void DeleteDirectory(string path) => Directory.Delete(path, true);
    }
}
