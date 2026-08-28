using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Cli;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Responses;
using SIL.Motif.Host.Baselines;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Runner.AppliedLog;
using SIL.Motif.Runner.DryRun;
using SIL.Motif.Worker.Baselines;
using SIL.Motif.Projection.Usage;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Scheduling;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Tests.TestFixtures;

/// <summary>
/// Stands in for the real runner (ADR 0041 decision 7) inside an in-process CLI test: enqueues a Dry
/// Run, records a Baseline pointing at the project's own saved file the first time one is needed (this
/// is not <c>baseline-refresh</c>'s own capture path — no separate bundle is copied), drains exactly
/// that job through the real <see cref="DryRunJobHandler"/>, then renders it exactly as
/// <c>dry-run --wait</c> does.
/// </summary>
internal static class DryRunJobRunner
{
    public static CommandResult Run(string fwDataPath, string productVersion, string proposalId,
        bool asJson = false, UsageLog? usage = null)
    {
        var enqueued = JobCommands.EnqueueDryRun(fwDataPath, productVersion, proposalId, usage);
        if (enqueued.ExitCode != 0) return enqueued;
        var jobId = enqueued.Output.Trim();

        var full = Path.GetFullPath(fwDataPath);
        var project = new ProjectLocator(full, Path.GetFileNameWithoutExtension(full));
        var workspaceKey = ProjectWorkspaceKey.Compute(project);

        using (var database = ProjectMotifDatabase.Open(fwDataPath))
        {
            var baselines = new BaselineRepository(database);
            if (baselines.GetCurrent(workspaceKey) is null)
            {
                var token = new BaselineToken(project.FieldWorksProjectIdentity, "sha256:" + new string('a', 64),
                    "1", DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    "sha256:" + new string('b', 64));
                baselines.Record(workspaceKey, new BaselinePublication(Path.GetDirectoryName(full)!, full, token),
                    DateTimeOffset.UtcNow);
            }

            using var lanes = new ProjectLaneRegistry(_ => baselines.GetCurrent(workspaceKey)!.Token);
            var proposals = new ProposalRepository(database);
            var handler = new DryRunJobHandler(new JobRepository(database), baselines, proposals, lanes, _ => null,
                (candidatePath, _) =>
                {
                    // A peek open on the immutable published Baseline, separate from the scratch the run opens.
                    using var peek = new FwDataProjectLoader().LoadScratchCache(candidatePath);
                    return Task.FromResult<IReadOnlyCollection<Guid>>(
                        ProjectAppliedLog.ReadAll(peek).Select(entry => entry.ProposalId).ToArray());
                },
                (candidatePath, plan, _) => Task.FromResult(
                    ProposalDryRunner.Run(new BaselineScratchFactory().OpenSingleUse(candidatePath), plan)));
            var loop = new JobRunnerLoop(new JobClaims(database), workspaceKey, "test-runner",
                TimeSpan.FromMinutes(1), TimeSpan.Zero,
                new Dictionary<string, JobRunnerLoop.Handler>(StringComparer.Ordinal)
                {
                    [JobCommands.DryRunKind] = (job, token) => handler.RunAsync(job.JobId, project, token),
                });
            loop.RunUntilIdleAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        return JobCommands.WaitForDryRun(fwDataPath, productVersion, proposalId, jobId, asJson, TimeSpan.FromSeconds(5));
    }
}
