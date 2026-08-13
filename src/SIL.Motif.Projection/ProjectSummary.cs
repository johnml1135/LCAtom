using SIL.LCModel;

namespace SIL.Motif.Projection;

/// <summary>The <c>open</c> report: the identity of a project and its lexicon's size.</summary>
public sealed record ProjectSummaryProjection(string ProjectName, int LexicalEntryCount);

/// <summary>Builds a <see cref="ProjectSummaryProjection"/> from a live, already-open project.</summary>
public static class ProjectSummaryReader
{
    public static ProjectSummaryProjection Read(LcmCache cache)
    {
        var entryRepo = cache.ServiceLocator.GetInstance<ILexEntryRepository>();
        return new ProjectSummaryProjection(cache.ProjectId.Name, entryRepo.Count);
    }
}
