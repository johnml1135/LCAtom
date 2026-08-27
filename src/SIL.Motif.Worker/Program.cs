using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SIL.LCModel;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Host.Store;
using SIL.Motif.LiveHost.Baselines;
using SIL.Motif.Worker.Baselines;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Worker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = RunnerOptions.Read(args);
        using var tracker = new WorkerWorkTracker();
        using var host = options.OwnerNamespace is { } isolated
            ? JobRunnerHost.ForNamespace(isolated, tracker)
            : new JobRunnerHost(tracker);
        var ownership = WorkspaceOwnership.Bootstrap(options.Root);
        host.ConfigureWorkspaces(ownership);
        var ownerId = "runner-" + Environment.ProcessId.ToString();
        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
        var runtimes = host.CreateRuntimeRegistry(catalog,
            (jobs, key) => new WorkerRecoveryCoordinator(
                new WorkerRecovery(jobs, ownerId: ownerId), new WorkspaceCleaner(ownership)));
        if (!host.TryAcquireOwnership())
        {
            Console.WriteLine("existing runner: " + host.OwnerName);
            return 0;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.WriteLine(host.OwnerName);
        host.Start();

        var project = ProjectFrom(args);
        if (project is not null)
            await DrainAsync(project, runtimes, options, ownerId, shutdown.Token).ConfigureAwait(false);

        await new WorkerLifetime().RunUntilIdleAsync(options.IdleTimeout, host, shutdown.Token)
            .ConfigureAwait(false);
        shutdown.Cancel();
        return 0;
    }

    /// Runs to empty the queue of the one project this runner was pointed at.
    private static async Task DrainAsync(ProjectLocator project, Projects.ProjectRuntimeRegistry runtimes,
        RunnerOptions options, string ownerId, CancellationToken cancellationToken)
    {
        var runtime = runtimes.GetOrOpen(project);
        var refresh = new BaselineRefreshJobHandler(
            new BaselineRefreshBarrier(locator => new FwDataProjectLoader().LoadCache(locator.FullFwDataPath)),
            (cache, token) => CaptureAsync(cache, options.Root, token));
        var loop = new JobRunnerLoop(runtime.Jobs, runtime.WorkspaceKey, ownerId: ownerId,
            lease: options.Lease, poll: TimeSpan.Zero,
            handlers: new Dictionary<string, JobRunnerLoop.Handler>(StringComparer.Ordinal)
            {
                ["baseline-refresh"] = (_, token) => refresh.RunAsync(project, token),
            });
        await loop.RunUntilIdleAsync(cancellationToken).ConfigureAwait(false);
        // The runtime holds a work lease until asked to re-check; without this the process never idles.
        runtime.RefreshWorkLease();
    }

    private static async Task CaptureAsync(LcmCache cache, string root, CancellationToken cancellationToken)
    {
        var transfers = Path.Combine(root, "captures");
        Directory.CreateDirectory(transfers);
        var bundle = Path.Combine(transfers, Guid.NewGuid().ToString("N") + ".zip");
        using var destination = File.Create(bundle);
        await new BaselineBundleWriter().WriteAsync(cache, destination, cancellationToken)
            .ConfigureAwait(false);
    }

    private static ProjectLocator? ProjectFrom(string[] args)
    {
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (!string.Equals(args[index], "--project", StringComparison.Ordinal)) continue;
            var full = Path.GetFullPath(args[index + 1]);
            if (!File.Exists(full)) return null;
            return new ProjectLocator(full, Path.GetFileNameWithoutExtension(full));
        }
        return null;
    }
}
