using SIL.Motif.Contract.Projects;

namespace SIL.Motif.Worker.Projects;

/// <summary>Identifies one connection currently serving a project as its live host.</summary>
internal sealed record ProjectHostRegistration(
    string ConnectionId,
    string HostSessionId,
    int ProtocolVersion,
    Stream Stream,
    SemaphoreSlim WriteGate);

/// <summary>Routes live-host registrations by canonical project workspace key.</summary>
internal interface IProjectHostRegistry : IDisposable
{
    void Register(ProjectLocator project, ProjectHostRegistration registration);
    void Unregister(ProjectLocator project, string connectionId);
    bool TryGet(ProjectLocator project, out ProjectHostRegistration registration);
    bool HasRegistration(string workspaceKey);
}

/// <summary>Ensures one live host owns a project route and stale connections cannot unregister a successor.</summary>
internal sealed class ProjectHostRegistry : IProjectHostRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ProjectHostRegistration> _registrations = new(StringComparer.Ordinal);
    private bool _disposed;

    public void Register(ProjectLocator project, ProjectHostRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(registration);
        if (string.IsNullOrWhiteSpace(registration.ConnectionId))
            throw new ArgumentException("A connection id is required.", nameof(registration));
        var key = ProjectWorkspaceKey.Compute(project);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_registrations.ContainsKey(key))
                throw new ProjectHostBusyException(key);
            _registrations.Add(key, registration);
        }
    }

    public void Unregister(ProjectLocator project, string connectionId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(connectionId)) return;
        var key = ProjectWorkspaceKey.Compute(project);
        lock (_sync)
        {
            if (_disposed || !_registrations.TryGetValue(key, out var current) ||
                !StringComparer.Ordinal.Equals(current.ConnectionId, connectionId)) return;
            _registrations.Remove(key);
        }
    }

    public bool TryGet(ProjectLocator project, out ProjectHostRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(project);
        return TryGet(ProjectWorkspaceKey.Compute(project), out registration);
    }

    internal bool TryGet(string workspaceKey, out ProjectHostRegistration registration)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                registration = null!;
                return false;
            }
            return _registrations.TryGetValue(workspaceKey, out registration!);
        }
    }

    public bool HasRegistration(string workspaceKey)
    {
        if (string.IsNullOrWhiteSpace(workspaceKey)) return false;
        lock (_sync) return !_disposed && _registrations.ContainsKey(workspaceKey);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _registrations.Clear();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProjectHostRegistry));
    }
}

/// <summary>Reports that another live host already owns the requested project route.</summary>
public sealed class ProjectHostBusyException : InvalidOperationException
{
    public ProjectHostBusyException(string workspaceKey)
        : base("A live host is already registered for this project workspace.") => WorkspaceKey = workspaceKey;

    /// <summary>Gets the canonical workspace key whose host route is busy.</summary>
    public string WorkspaceKey { get; }
}
