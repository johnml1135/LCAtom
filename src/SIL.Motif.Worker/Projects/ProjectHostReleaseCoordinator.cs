using System.Collections.Concurrent;

namespace SIL.Motif.Worker.Projects;

/// <summary>Provides sticky project-scoped authority-release notifications to waiting work.</summary>
internal sealed class ProjectHostReleaseCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<string, ReleaseState> _states = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    internal long Observe(string workspaceKey) => State(workspaceKey).Generation;

    internal Task WaitForReleaseAsync(string workspaceKey, long observedGeneration,
        CancellationToken cancellationToken)
    {
        var state = State(workspaceKey);
        lock (state.Gate)
        {
            if (state.Generation != observedGeneration) return Task.CompletedTask;
            return state.Released.Task.WaitAsync(cancellationToken);
        }
    }

    internal void NotifyReleased(string workspaceKey)
    {
        // Dispose already cancelled every waiter, so a post-shutdown notification has nothing to signal.
        if (Volatile.Read(ref _disposed) != 0) return;
        var state = _states.GetOrAdd(workspaceKey, static _ => new ReleaseState());
        TaskCompletionSource released;
        lock (state.Gate)
        {
            state.Generation = checked(state.Generation + 1);
            released = state.Released;
            state.Released = NewSignal();
        }
        released.TrySetResult();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        foreach (var state in _states.Values)
        {
            lock (state.Gate) state.Released.TrySetCanceled(_shutdown.Token);
        }
        _states.Clear();
        _shutdown.Dispose();
    }

    private ReleaseState State(string workspaceKey)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(ProjectHostReleaseCoordinator));
        return _states.GetOrAdd(workspaceKey, static _ => new ReleaseState());
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ReleaseState
    {
        internal object Gate { get; } = new();
        internal long Generation { get; set; }
        internal TaskCompletionSource Released { get; set; } = NewSignal();
    }
}
