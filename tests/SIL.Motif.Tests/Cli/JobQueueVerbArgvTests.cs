using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SIL.Motif.Cli;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Responses;
using SIL.Motif.Generator;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Scheduling;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// Covers the cross-project queue verbs against the real executable: <c>jobs list --all</c>, <c>jobs
/// move</c>, <c>jobs cancel</c> and <c>jobs requeue</c>.
/// </summary>
/// <remarks>
/// Every invocation here shares one isolated worker root, so <c>jobs list --all</c> sees exactly what
/// this test wrote and nothing a developer's own machine happens to have queued. What is deliberately
/// not covered here is a runner actually observing a cancellation flag mid-handler — that mechanism is
/// pinned in-process by <c>JobRunnerLoopTests</c>, since there is no controllable slow job kind to drive
/// through the real executable within a reasonable bound. This suite pins only the CLI's own half: what
/// each verb writes.
/// </remarks>
public sealed class JobQueueVerbArgvTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-job-queue-" + Guid.NewGuid().ToString("N"));

    public JobQueueVerbArgvTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(ProjectDir("p1"));
        Directory.CreateDirectory(ProjectDir("p2"));
        File.WriteAllText(ProjectPath("p1", "a"), string.Empty);
        File.WriteAllText(ProjectPath("p2", "b"), string.Empty);
    }

    private string ProjectA => ProjectPath("p1", "a");
    private string ProjectB => ProjectPath("p2", "b");

    [Fact]
    public void ListAllOrdersAcrossProjectsByQueueOrderAndMatchesTheClaimOrder()
    {
        var jobA1 = Enqueue(ProjectA);
        var jobB1 = Enqueue(ProjectB);
        var jobA2 = Enqueue(ProjectA);
        SetQueueOrder(DatabasePath("p1", "a"), jobA1, 1.0);
        SetQueueOrder(DatabasePath("p2", "b"), jobB1, 2.0);
        SetQueueOrder(DatabasePath("p1", "a"), jobA2, 3.0);

        var listed = ListAllJobIds();

        Assert.Equal(new[] { jobA1, jobB1, jobA2 }, listed);
        // The list must show the order a real claim actually takes, not merely a plausible one.
        Assert.Equal(listed, DrainByRealClaim());
    }

    [Fact]
    public void MoveToTopChangesWhichJobARealClaimTakesNext()
    {
        var jobA1 = Enqueue(ProjectA);
        var jobB1 = Enqueue(ProjectB);
        SetQueueOrder(DatabasePath("p1", "a"), jobA1, 1.0);
        SetQueueOrder(DatabasePath("p2", "b"), jobB1, 2.0);
        Assert.Equal(new[] { jobA1, jobB1 }, ListAllJobIds());

        var moved = Run($"jobs move {jobB1} --project \"{ProjectB}\" --to-top");

        Assert.Equal(0, moved.ExitCode);
        Assert.Equal(new[] { jobB1, jobA1 }, ListAllJobIds());
        Assert.Equal(jobB1, DrainByRealClaim()[0]);
    }

    [Fact]
    public void MoveToTopWritesOnlyTheMoversRowEvenWhenTheCurrentHeadIsTied()
    {
        var headA = Enqueue(ProjectA);
        var headB = Enqueue(ProjectB);
        var mover = Enqueue(ProjectA);
        SetQueueOrder(DatabasePath("p1", "a"), headA, 500.0);
        SetQueueOrder(DatabasePath("p2", "b"), headB, 500.0);
        SetQueueOrder(DatabasePath("p1", "a"), mover, 999.0);

        var result = Run($"jobs move {mover} --project \"{ProjectA}\" --to-top --json");

        Assert.Equal(0, result.ExitCode);
        var response = ProjectionJson.Deserialize<JobStatusResponse>(result.Output)!;
        Assert.True(response.QueueOrder < 500.0);
        // Neither tied head was touched: only the mover's own row changed anywhere.
        Assert.Equal(500.0, ReadQueueOrder(DatabasePath("p1", "a"), headA));
        Assert.Equal(500.0, ReadQueueOrder(DatabasePath("p2", "b"), headB));
        Assert.Equal(mover, ListAllJobIds()[0]);
        Assert.Equal(mover, DrainByRealClaim()[0]);
    }

    [Fact]
    public void MoveBeforeATiedPairWritesOnlyTheMoversRowAndLandsAheadOfBoth()
    {
        var predecessor = Enqueue(ProjectA);
        var target = Enqueue(ProjectA);
        var mover = Enqueue(ProjectB);
        SetQueueOrder(DatabasePath("p1", "a"), predecessor, 500.0);
        SetQueueOrder(DatabasePath("p1", "a"), target, 500.0);
        SetQueueOrder(DatabasePath("p2", "b"), mover, 999.0);
        // The tie is broken by JobId; find which of the two sorts first without assuming either literal.
        var tiedOrder = ListAllJobIds().Where(id => id == predecessor || id == target).ToArray();
        var first = tiedOrder[0];
        var second = tiedOrder[1];

        var result = Run($"jobs move {mover} --project \"{ProjectB}\" --before {second} --json");

        Assert.Equal(0, result.ExitCode);
        var response = ProjectionJson.Deserialize<JobStatusResponse>(result.Output)!;
        Assert.Equal(499.5, response.QueueOrder);
        // Exactly one row changed anywhere: both tied rows are exactly as they were before the move.
        Assert.Equal(500.0, ReadQueueOrder(DatabasePath("p1", "a"), first));
        Assert.Equal(500.0, ReadQueueOrder(DatabasePath("p1", "a"), second));
        Assert.Equal(new[] { mover, first, second }, ListAllJobIds());
        Assert.Equal(new[] { mover, first, second }, DrainByRealClaim());
    }

    [Fact]
    public void MoveBeforeAnUnknownJobIsNotFound()
    {
        var jobId = Enqueue(ProjectA);

        var result = Run($"jobs move {jobId} --project \"{ProjectA}\" --before job/doesNotExist --json");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(FailureReason.NotFound, Envelope(result.Error).Reason);
    }

    [Fact]
    public void MoveBeforeItselfIsAnInvocationErrorRatherThanARefusal()
    {
        var jobId = Enqueue(ProjectA);

        var result = Run($"jobs move {jobId} --project \"{ProjectA}\" --before {jobId} --json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(FailureReason.InvalidArgument, Envelope(result.Error).Reason);
    }

    [Fact]
    public void MoveRequiresExactlyOneDestination()
    {
        var jobId = Enqueue(ProjectA);

        Assert.Equal(1, Run($"jobs move {jobId} --project \"{ProjectA}\"").ExitCode);
        Assert.Equal(1, Run($"jobs move {jobId} --project \"{ProjectA}\" --to-top --to-bottom").ExitCode);
    }

    [Fact]
    public void CancelOnAQueuedJobTerminatesItDirectlyWithNoRunnerInvolved()
    {
        var jobId = Enqueue(ProjectA);

        var result = Run($"jobs cancel {jobId} --project \"{ProjectA}\" --json");

        Assert.Equal(0, result.ExitCode);
        var response = ProjectionJson.Deserialize<JobStatusResponse>(result.Output)!;
        Assert.Equal(JobStatus.Cancelled, response.Status);
        Assert.Equal(JobFailureCategory.Cancellation, response.FailureCategory);
    }

    [Fact]
    public void CancelOnARunningJobOnlySetsTheFlagRatherThanForcingItsStatus()
    {
        var jobId = Enqueue(ProjectA);
        ClaimForReal(ProjectA, jobId);

        var result = Run($"jobs cancel {jobId} --project \"{ProjectA}\" --json");

        Assert.Equal(0, result.ExitCode);
        var response = ProjectionJson.Deserialize<JobStatusResponse>(result.Output)!;
        Assert.Equal(JobStatus.Running, response.Status);
        Assert.True(response.CancellationRequested);
    }

    [Fact]
    public void CancelOnAnAlreadyTerminalJobIsRefused()
    {
        var jobId = Enqueue(ProjectA);
        Assert.Equal(0, Run($"jobs cancel {jobId} --project \"{ProjectA}\"").ExitCode);

        var result = Run($"jobs cancel {jobId} --project \"{ProjectA}\" --json");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(FailureReason.Refused, Envelope(result.Error).Reason);
    }

    [Fact]
    public void RequeueOnATerminalJobProducesAFreshAttemptThatIsClaimable()
    {
        var jobId = Enqueue(ProjectA);
        Assert.Equal(0, Run($"jobs cancel {jobId} --project \"{ProjectA}\"").ExitCode);

        var result = Run($"jobs requeue {jobId} --project \"{ProjectA}\" --json");

        Assert.Equal(0, result.ExitCode);
        var response = ProjectionJson.Deserialize<JobStatusResponse>(result.Output)!;
        Assert.Equal(JobStatus.Queued, response.Status);
        Assert.NotEqual(jobId, response.JobId);
        Assert.Contains(response.JobId, DrainByRealClaim());
    }

    [Fact]
    public void RequeueOnANonTerminalJobIsRefused()
    {
        var jobId = Enqueue(ProjectA);

        var result = Run($"jobs requeue {jobId} --project \"{ProjectA}\" --json");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(FailureReason.Refused, Envelope(result.Error).Reason);
    }

    [Fact]
    public void AssessmentsReturnsWhatATrialProduced()
    {
        var jobId = Enqueue(ProjectA);
        var assessmentId = RecordAssessment(ProjectA, "alpha");
        CompleteAsTrial(ProjectA, jobId, assessmentId);

        var result = Run($"jobs assessments {jobId} --project \"{ProjectA}\" --json");

        Assert.Equal(0, result.ExitCode);
        var response = ProjectionJson.Deserialize<JobAssessmentsResponse>(result.Output)!;
        Assert.Equal(jobId, response.JobId);
        Assert.Single(response.Assessments);
        Assert.Equal(assessmentId, response.Assessments[0].AssessmentId);
        Assert.Equal("pangloss", response.Assessments[0].Assessor);
        Assert.Equal("Correctness", response.Assessments[0].Kind);
    }

    [Fact]
    public void AssessmentsOnAnUnknownJobIsNotFound()
    {
        var result = Run($"jobs assessments job/doesNotExist --project \"{ProjectA}\" --json");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(FailureReason.NotFound, Envelope(result.Error).Reason);
    }

    /// Records one Correctness Assessment directly, standing in for what a real Trial's Assessor would produce.
    private static string RecordAssessment(string project, string word)
    {
        var locator = new ProjectLocator(project, Path.GetFileNameWithoutExtension(project));
        using var database = MotifDatabase.OpenOwned(ProjectDatabaseCatalog.DatabasePathFor(locator), locator,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var assessmentId = SIL.Motif.Contract.Ids.CanonicalId.Mint("assessment/").Value;
        var corpus = Selection.Create("test", new[] { word });
        new AssessmentRepository(database).Record(new NewAssessmentRecord(
            AssessmentId: assessmentId,
            ProposalId: null,
            ProposalIntentDigest: null,
            Assessor: "pangloss",
            Kind: "Correctness",
            ScopeJson: """{"engine":"fast","perWordLimitMs":1000}""",
            ScopeDigest: "sha256:" + new string('a', 64),
            TokeniserName: "none",
            TokeniserVersion: "1",
            BaselineToken: "{}",
            Selection: corpus,
            OutcomeDigest: "sha256:" + new string('b', 64),
            SemanticDigest: "sha256:" + new string('c', 64),
            GrammarSourceSha256: "sha256:" + new string('d', 64),
            ModelFingerprint: "model",
            Pipeline: "pipeline",
            DiagnosticCount: 0,
            Words: new[] { new AssessedWord(word, "analysed", new[] { new ParsedAnalysis(null, Array.Empty<string>(), 0, "digest") }) }));
        return assessmentId;
    }

    /// Drives a queued job through Running to Completed with a Trial-shaped ResultJson, without a real runner.
    private static void CompleteAsTrial(string project, string jobId, params string[] assessmentIds)
    {
        var locator = new ProjectLocator(project, Path.GetFileNameWithoutExtension(project));
        using var database = MotifDatabase.OpenOwned(ProjectDatabaseCatalog.DatabasePathFor(locator), locator,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var jobs = new JobRepository(database);
        jobs.Transition(jobId, JobStatus.Running);
        var resultJson = "{\"baselineToken\":{},\"assessmentIds\":[" +
            string.Join(",", assessmentIds.Select(id => "\"" + id + "\"")) + "]}";
        jobs.Transition(jobId, JobStatus.Completed, resultJson);
    }

    [Fact]
    public void ListWithoutAllIsAUsageFailureThatNamesTheFlag()
    {
        var result = Run("jobs list");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--all", result.Error, StringComparison.Ordinal);
    }

    private string ProjectDir(string name) => Path.Combine(_root, name);
    private string ProjectPath(string dir, string stem) => Path.Combine(ProjectDir(dir), stem + ".fwdata");
    private string DatabasePath(string dir, string stem) => Path.Combine(ProjectDir(dir), stem + ".motif.db");

    private string Enqueue(string project)
    {
        var result = Run($"baseline-refresh --project \"{project}\"");
        Assert.Equal(0, result.ExitCode);
        return result.Output.Trim();
    }

    private string[] ListAllJobIds()
    {
        var result = Run("jobs list --all --json");
        Assert.Equal(0, result.ExitCode);
        return ProjectionJson.Deserialize<JobQueueListResponse>(result.Output)!.Jobs
            .Select(job => job.JobId).ToArray();
    }

    /// Claims for real against the project's own database, simulating a runner already holding the row.
    private static void ClaimForReal(string project, string jobId)
    {
        var locator = new ProjectLocator(project, Path.GetFileNameWithoutExtension(project));
        using var database = MotifDatabase.OpenOwned(ProjectDatabaseCatalog.DatabasePathFor(locator), locator,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var claimed = new JobClaims(database).Claim(ProjectWorkspaceKey.Compute(locator), "test-owner",
            DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), TimeSpan.FromMinutes(5));
        Assert.NotNull(claimed);
        Assert.Equal(jobId, claimed!.JobId);
    }

    /// Drains every Known project by the same k-way merge the real runner sweep uses, claiming for real.
    private string[] DrainByRealClaim()
    {
        using var machine = MachineDatabase.Open(_root);
        var known = new KnownProjectRegistry(machine);
        var ownership = WorkspaceOwnership.Bootstrap(Path.Combine(_root, "owned"));
        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
        using var runtimes = new ProjectRuntimeRegistry(catalog,
            (jobs, _) => new WorkerRecoveryCoordinator(new WorkerRecovery(jobs), new WorkspaceCleaner(ownership)),
            new ProjectRuntimeActivity());
        using var lanes = new ProjectLaneRegistry(
            _ => throw new InvalidOperationException("No Dry Run handler runs in this suite."));
        var options = new RunnerOptions { Root = _root };

        var claimed = new List<string>();
        var guard = 0;
        while (true)
        {
            var outcome = SIL.Motif.Worker.Program.SweepOnceAsync(known, runtimes, lanes, options, "test-runner",
                CancellationToken.None).GetAwaiter().GetResult();
            if (outcome.JobId is not { } next) break;
            claimed.Add(next);
            if (++guard > 50) throw new InvalidOperationException("The sweep did not converge.");
        }
        return claimed.ToArray();
    }

    private static void SetQueueOrder(string databasePath, string jobId, double queueOrder)
    {
        using var connection = new SqliteConnection("Data Source=" + databasePath);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Jobs SET QueueOrder = $order WHERE JobId = $id;";
        command.Parameters.AddWithValue("$order", queueOrder);
        command.Parameters.AddWithValue("$id", jobId);
        command.ExecuteNonQuery();
    }

    private static double ReadQueueOrder(string databasePath, string jobId)
    {
        using var connection = new SqliteConnection("Data Source=" + databasePath);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT QueueOrder FROM Jobs WHERE JobId = $id;";
        command.Parameters.AddWithValue("$id", jobId);
        return (double)command.ExecuteScalar()!;
    }

    private static FailureEnvelope Envelope(string stderr) =>
        ProjectionJson.Deserialize<FailureEnvelope>(stderr)!;

    private CliRun Run(string arguments)
    {
        var executable = Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "SIL.Motif.Cli", "bin", "Debug", "net10.0", "motif.exe");
        var start = new ProcessStartInfo(executable)
        {
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment[RunnerOptions.RootVariable] = _root;
        start.Environment[RunnerKick.SuppressVariable] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60000), "The CLI did not exit within its bound.");
        return new CliRun(process.ExitCode, output, error);
    }

    private sealed record CliRun(int ExitCode, string Output, string Error);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}
