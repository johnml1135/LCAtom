using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Worker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var idleTimeout = ReadIdleTimeout(args);
        using var tracker = new WorkerWorkTracker();
        await using var server = new WorkerServer(workTracker: tracker);
        var workerRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SIL", "Motif");
        var ownership = WorkspaceOwnership.Bootstrap(workerRoot);
        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
        server.CreateRuntimeRegistry(catalog,
            (jobs, key) => new WorkerRecoveryCoordinator(new WorkerRecovery(jobs),
                new WorkspaceCleaner(ownership)));
        if (!server.TryAcquireOwnership())
        {
            Console.WriteLine("existing endpoint: " + server.EndpointName);
            return 0;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.WriteLine(server.EndpointName);
        var run = server.StartAsync(shutdown.Token);
        await new WorkerLifetime().RunUntilIdleAsync(idleTimeout, server, shutdown.Token)
            .ConfigureAwait(false);
        shutdown.Cancel();
        await run.ConfigureAwait(false);
        return 0;
    }

    private static TimeSpan ReadIdleTimeout(string[] args)
    {
        for (var index = 0; index + 1 < args.Length; index++)
            if (string.Equals(args[index], "--idle-ms", StringComparison.Ordinal) &&
                int.TryParse(args[index + 1], out var milliseconds) && milliseconds > 0)
                return TimeSpan.FromMilliseconds(milliseconds);
        return TimeSpan.FromMinutes(5);
    }
}
