using SIL.Motif.Contract.Baselines;

namespace SIL.Motif.Worker.Scheduling;

public enum ProjectWorkKind
{
    Refresh,
    DryRun,
    CandidateExport
}

/// <summary>One ordered project operation whose Baseline is assigned by its lane.</summary>
public sealed class ProjectWorkItem
{
    private ProjectWorkItem(ProjectWorkKind kind,
        Func<BaselineToken, CancellationToken, Task>? baselineWork,
        Func<CancellationToken, Task<BaselineToken>>? refreshWork)
    {
        Kind = kind;
        BaselineWork = baselineWork;
        RefreshWork = refreshWork;
    }

    public ProjectWorkKind Kind { get; }
    internal Func<BaselineToken, CancellationToken, Task>? BaselineWork { get; }
    internal Func<CancellationToken, Task<BaselineToken>>? RefreshWork { get; }

    public static ProjectWorkItem Refresh(Func<CancellationToken, Task<BaselineToken>> work) =>
        new(ProjectWorkKind.Refresh, null, work ?? throw new ArgumentNullException(nameof(work)));

    public static ProjectWorkItem DryRun(Func<BaselineToken, CancellationToken, Task> work) =>
        new(ProjectWorkKind.DryRun, work ?? throw new ArgumentNullException(nameof(work)), null);

    public static ProjectWorkItem CandidateExport(Func<BaselineToken, CancellationToken, Task> work) =>
        new(ProjectWorkKind.CandidateExport, work ?? throw new ArgumentNullException(nameof(work)), null);
}

/// <summary>Records which immutable Baseline one ordered project operation used or published.</summary>
public sealed record ProjectWorkResult(ProjectWorkKind Kind, BaselineToken Baseline);
