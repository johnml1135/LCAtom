using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Worker.Baselines;

internal sealed class BaselineWorkspaceCatalog
{
    private readonly string _workerRoot;
    private readonly IWorkspaceOwnership _ownership;

    public BaselineWorkspaceCatalog(IWorkspaceOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        _ownership = ownership;
        _workerRoot = Path.GetFullPath(ownership.WorkerRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public BaselinePublicationTarget For(ProjectRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var segment = ProjectWorkspaceKey.StorageSegment(runtime.WorkspaceKey);
        var path = Path.Combine(_workerRoot, segment, "baseline");
        if (!_ownership.IsOwned(path))
            throw new InvalidOperationException("The managed Baseline target is outside the worker workspace.");
        return new BaselinePublicationTarget(path, runtime.Project.FieldWorksProjectIdentity);
    }
}
