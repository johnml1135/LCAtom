using System.Collections.Concurrent;
using SIL.Motif.Contract.Baselines;

namespace SIL.Motif.Worker.Scheduling;

/// <summary>Owns exactly one ordered work lane for each canonical project workspace key.</summary>
public sealed class ProjectLaneRegistry : IDisposable
{
    private readonly Func<string, BaselineToken> _initialBaseline;
    private readonly ConcurrentDictionary<string, ProjectLane> _lanes = new(StringComparer.Ordinal);
    private int _disposed;

    public ProjectLaneRegistry(Func<string, BaselineToken> initialBaseline) =>
        _initialBaseline = initialBaseline ?? throw new ArgumentNullException(nameof(initialBaseline));

    public ProjectLane GetOrCreate(string workspaceKey)
    {
        if (string.IsNullOrWhiteSpace(workspaceKey))
            throw new ArgumentException("A workspace key is required.", nameof(workspaceKey));
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(ProjectLaneRegistry));
        return _lanes.GetOrAdd(workspaceKey, key => new ProjectLane(_initialBaseline(key)));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var lane in _lanes.Values) lane.Dispose();
        _lanes.Clear();
    }
}
