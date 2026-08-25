using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Threading;
using SIL.Motif.Worker.PanGloss;
using Xunit;

namespace SIL.Motif.Tests.Worker;

[SupportedOSPlatform("windows")]
public sealed class MachinePanGlossQueueTests
{
    [RequiresWindowsFact]
    public async Task RunAsync_AdmitsThreeProjectsInSubmissionOrder()
    {
        var slotNames = UniqueSlotNames(2);
        using var queue = new MachinePanGlossQueue(slotNames);
        var admissionOrder = new ConcurrentQueue<string>();
        var releaseGates = new[] { "project-a", "project-b", "project-c" }
            .ToDictionary(id => id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        Task<int> Enqueue(string jobId) => queue.RunAsync(jobId, async (_, ct) =>
        {
            admissionOrder.Enqueue(jobId);
            await releaseGates[jobId].Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return 0;
        }, CancellationToken.None);

        var first = Enqueue("project-a");
        var second = Enqueue("project-b");
        var third = Enqueue("project-c");

        // Only two slots exist, so project-c cannot be admitted until one of the first two releases.
        await WaitUntilAsync(() => admissionOrder.Count >= 2, TimeSpan.FromSeconds(5));
        releaseGates["project-a"].SetResult();
        await WaitUntilAsync(() => admissionOrder.Count >= 3, TimeSpan.FromSeconds(5));
        releaseGates["project-b"].SetResult();
        releaseGates["project-c"].SetResult();

        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "project-a", "project-b", "project-c" }, admissionOrder.ToArray());
    }

    [RequiresWindowsFact]
    public async Task RunAsync_AcrossTwoUserNamespaces_NeverExceedsMachineCapacity()
    {
        var slotNames = UniqueSlotNames(2);
        // Two queues sharing the same machine-global slot names, standing in for two user sessions.
        using var userA = new MachinePanGlossQueue(slotNames);
        using var userB = new MachinePanGlossQueue(slotNames);

        var gate = new object();
        var currentlyRunning = 0;
        var peakRunning = 0;
        var observedRates = new ConcurrentBag<uint>();
        var releaseAll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> Enqueue(MachinePanGlossQueue queue, string jobId) => queue.RunAsync(jobId, async (cpuJob, ct) =>
        {
            observedRates.Add(cpuJob.QueryCpuRateControl().CpuRate);
            lock (gate) peakRunning = Math.Max(peakRunning, ++currentlyRunning);
            try { await releaseAll.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); }
            finally { lock (gate) currentlyRunning--; }
            return 0;
        }, CancellationToken.None);

        // Four admissions against two slots: the cap holds regardless of which queue's jobs get them.
        var tasks = new[]
        {
            Enqueue(userA, "a-1"), Enqueue(userA, "a-2"),
            Enqueue(userB, "b-1"), Enqueue(userB, "b-2"),
        };

        await WaitUntilAsync(() => Volatile.Read(ref peakRunning) >= 2, TimeSpan.FromSeconds(5));
        releaseAll.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(2, peakRunning);
        Assert.Equal(0, currentlyRunning);
        Assert.All(observedRates, rate => Assert.Equal((uint)WindowsCpuJob.CpuRateHardCapBasisPoints, rate));
    }

    [RequiresWindowsFact]
    public async Task RunAsync_CancelsAJobStillWaitingForASlotWithoutBlockingOthers()
    {
        var slotNames = UniqueSlotNames(1);
        using var queue = new MachinePanGlossQueue(slotNames);
        var holderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holderRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holderTask = queue.RunAsync("holder", async (_, ct) =>
        {
            holderStarted.SetResult();
            await holderRelease.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return 0;
        }, CancellationToken.None);
        await holderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var waitingJobCancellation = new CancellationTokenSource();
        var waitingTask = queue.RunAsync("waiting", (_, _) => Task.FromResult(0), waitingJobCancellation.Token);
        waitingJobCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingTask)
            .WaitAsync(TimeSpan.FromSeconds(5));

        holderRelease.SetResult();
        Assert.Equal(0, await holderTask.WaitAsync(TimeSpan.FromSeconds(5)));

        // The cancelled waiter must not have leaked the slot it never held.
        var afterTask = queue.RunAsync("after", (_, _) => Task.FromResult(7), CancellationToken.None);
        Assert.Equal(7, await afterTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [RequiresWindowsFact]
    public async Task MachinePanGlossQueue_RecoversASlotAbandonedByADeadOwner()
    {
        var abandonedSlotName = UniqueSlotNames(1)[0];
        AbandonMutex(abandonedSlotName);

        using var queue = new MachinePanGlossQueue(new[] { abandonedSlotName });

        var result = await queue.RunAsync("recovered", (_, _) => Task.FromResult(42), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(42, result);
    }

    private static string[] UniqueSlotNames(int count)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return Enumerable.Range(0, count).Select(i => $"Global\\MotifPanGlossSlotTest-{suffix}-{i}").ToArray();
    }

    // Simulates a dead owner: a Win32 mutex abandons when its owning thread exits without releasing.
    private static void AbandonMutex(string name)
    {
        var acquired = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            var mutex = new Mutex(false, name);
            mutex.WaitOne();
            acquired.Set();
        });
        thread.IsBackground = true;
        thread.Start();
        if (!acquired.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The victim thread did not acquire the mutex in time.");
        thread.Join(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
