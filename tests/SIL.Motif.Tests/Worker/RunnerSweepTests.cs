using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Scheduling;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Worker;

/// <summary>
/// Covers <see cref="SIL.Motif.Worker.Program.SweepOnceAsync"/>: the runner reading every Known project
/// each tick and claiming the globally first job across all of them, rather than draining one project
/// before looking at the next.
/// </summary>
public sealed class RunnerSweepTests : IDisposable
{
    private const string OwnerId = "sweep-test-runner";
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-sweep-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectRuntimeRegistry _runtimes;
    private readonly ProjectLaneRegistry _lanes =
        new(_ => throw new InvalidOperationException("No Dry Run handler runs in this suite."));
    private readonly RunnerOptions _options;

    public RunnerSweepTests()
    {
        Directory.CreateDirectory(_root);
        _options = new RunnerOptions { Root = _root };
        var ownership = WorkspaceOwnership.Bootstrap(Path.Combine(_root, "owned"));
        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
        _runtimes = new ProjectRuntimeRegistry(catalog,
            (jobs, key) => new WorkerRecoveryCoordinator(new WorkerRecovery(jobs), new WorkspaceCleaner(ownership)),
            new ProjectRuntimeActivity());
    }

    [Fact]
    public async Task TheSweepClaimsAcrossProjectsInGlobalQueueOrderRatherThanProjectByProject()
    {
        using var machine = MachineDatabase.Open(_options.Root);
        var known = new KnownProjectRegistry(machine);
        var a = SeedProject(known, "project-a");
        var b = SeedProject(known, "project-b");

        // Interleaved: a1, then b1, then a2 — project b's single job sorts between project a's two.
        SeedJob(a, "a1", queueOrder: 1.0);
        SeedJob(b, "b1", queueOrder: 2.0);
        SeedJob(a, "a2", queueOrder: 3.0);

        var order = await DrainAsync(known);

        Assert.Equal(new[] { "a1", "b1", "a2" }, order);
    }

    [Fact]
    public async Task AKnownProjectWhoseFwdataWasDeletedIsForgottenAndDoesNotBreakTheSweep()
    {
        using var machine = MachineDatabase.Open(_options.Root);
        var known = new KnownProjectRegistry(machine);
        var missingPath = Path.Combine(_root, "missing.fwdata");
        known.Record(FakeWorkspaceKey(missingPath), missingPath, DateTimeOffset.UtcNow);
        var present = SeedProject(known, "still-here");
        SeedJob(present, "job-1", queueOrder: 1.0);

        var order = await DrainAsync(known);

        Assert.Equal(new[] { "job-1" }, order);
        Assert.DoesNotContain(known.List(), record => record.FullFwDataPath == missingPath);
    }

    [Fact]
    public async Task AKnownProjectWhosePairedDatabaseCannotBeOpenedIsSkippedRatherThanForgotten()
    {
        using var machine = MachineDatabase.Open(_options.Root);
        var known = new KnownProjectRegistry(machine);
        var corruptPath = Path.Combine(_root, "corrupt.fwdata");
        File.WriteAllText(corruptPath, "placeholder");
        var corruptDatabasePath = ProjectDatabaseCatalog.DatabasePathFor(
            new ProjectLocator(corruptPath, Path.GetFileNameWithoutExtension(corruptPath)));
        File.WriteAllBytes(corruptDatabasePath, new byte[] { 1, 2, 3, 4 });
        var corruptKey = FakeWorkspaceKey(corruptPath);
        known.Record(corruptKey, corruptPath, DateTimeOffset.UtcNow);

        var healthy = SeedProject(known, "healthy");
        SeedJob(healthy, "job-1", queueOrder: 1.0);

        var order = await DrainAsync(known);

        Assert.Equal(new[] { "job-1" }, order);
        // Logged, not forgotten: a corrupt store silently vanishing would leave queued work with no explanation.
        Assert.Contains(known.List(), record => record.WorkspaceKey == corruptKey);
    }

    private ProjectRuntime SeedProject(KnownProjectRegistry known, string identity)
    {
        var path = Path.Combine(_root, identity + ".fwdata");
        File.WriteAllText(path, "placeholder");
        var project = new ProjectLocator(path, identity);
        known.Record(ProjectWorkspaceKey.Compute(project), path, DateTimeOffset.UtcNow);
        return _runtimes.GetOrOpen(project);
    }

    private static void SeedJob(ProjectRuntime runtime, string jobId, double queueOrder)
    {
        runtime.Jobs.Create(jobId, runtime.WorkspaceKey, "probe", "{}",
            DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        using var connection = runtime.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Jobs SET QueueOrder = $order WHERE JobId = $id;";
        command.Parameters.AddWithValue("$order", queueOrder);
        command.Parameters.AddWithValue("$id", jobId);
        command.ExecuteNonQuery();
    }

    private static string FakeWorkspaceKey(string fwDataPath) =>
        ProjectWorkspaceKey.Compute(new ProjectLocator(fwDataPath, Path.GetFileNameWithoutExtension(fwDataPath)));

    /// Repeatedly ticks the sweep — exactly what the runner's own loop does — until nothing is claimable.
    private async Task<IReadOnlyList<string>> DrainAsync(KnownProjectRegistry known)
    {
        var claimed = new List<string>();
        var guard = 0;
        while (true)
        {
            var outcome = await SIL.Motif.Worker.Program.SweepOnceAsync(known, _runtimes, _lanes, _options, OwnerId,
                CancellationToken.None);
            if (outcome.JobId is not { } next) break;
            claimed.Add(next);
            if (++guard > 50) throw new InvalidOperationException("The sweep did not converge.");
        }
        return claimed;
    }

    public void Dispose()
    {
        _runtimes.Dispose();
        _lanes.Dispose();
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}
