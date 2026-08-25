using System.Threading;

namespace SIL.Motif.Worker.PanGloss;

/// <summary>
/// Holds one of the machine's fixed PanGloss capacity slots until disposed.
/// </summary>
/// <remarks>
/// Wraps a <see cref="WorkerMutexOwner"/> already bound to one slot's machine-global mutex name, so an
/// owning process that dies while holding the slot is recovered the same way a worker's ownership
/// mutex is: the next waiter receives it through <see cref="AbandonedMutexException"/> rather than
/// blocking forever (pinned by `MachinePanGlossQueue_RecoversASlotAbandonedByADeadOwner`).
/// </remarks>
internal sealed class MachineSlotLease : IDisposable
{
    private readonly WorkerMutexOwner _owner;
    private readonly Action<int>? _onDisposed;
    private bool _disposed;

    internal MachineSlotLease(WorkerMutexOwner owner, int slotIndex, string jobId, Action<int>? onDisposed)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        SlotIndex = slotIndex;
        JobId = jobId ?? throw new ArgumentNullException(nameof(jobId));
        _onDisposed = onDisposed;
    }

    /// <summary>Which of the machine's slots this lease holds.</summary>
    public int SlotIndex { get; }

    /// <summary>The job id recorded against this slot for as long as it is held.</summary>
    public string JobId { get; }

    /// <summary>Releases the underlying machine-global mutex so another job may be admitted.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.Release();
        _onDisposed?.Invoke(SlotIndex);
    }
}
