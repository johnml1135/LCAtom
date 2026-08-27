using SIL.LCModel;
using SIL.Motif.Contract.Responses;

namespace SIL.Motif.Projection;

/// <summary>Builds a <see cref="ProjectSummaryProjection"/> from a live, already-open project.</summary>
public static class ProjectSummaryReader
{
    public static ProjectSummaryProjection Read(LcmCache cache)
    {
        var entryRepo = cache.ServiceLocator.GetInstance<ILexEntryRepository>();
        return new ProjectSummaryProjection(cache.ProjectId.Name, entryRepo.Count);
    }
}
