using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Store;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Cli.Worker;

/// <summary>
/// The CLI verbs that take this machine's legacy store into the paired project database.
/// </summary>
/// <remarks>
/// The cutover runs in this process. Exclusion is SQLite's own write lock held for the length of its one
/// transaction, so a concurrent writer is serialised behind it rather than locked out of the database —
/// which is what lets a runner keep working while a cutover runs, pinned by
/// `ACutoverStillRunsWhileAnotherProcessHasTheDatabaseOpen`.
/// </remarks>
public static class StoreCommands
{
    /// <summary>Cuts one store location over to the paired database and renders what moved.</summary>
    public static CommandResult Cutover(string storeDirectory, string fwDataPath, string productVersion)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, project) =>
        {
            var result = ProjectStoreCutover.Run(storeDirectory, database);
            return new CommandResult(0, Render(storeDirectory, Describe(project, result)));
        });
    }

    private static StoreCutoverResponse Describe(ProjectLocator project, ProjectStoreCutoverResult result) =>
        new StoreCutoverResponse(
            ProjectWorkspaceKey.Compute(project),
            result.FileProposals is null && result.LegacyBulk is null,
            result.FileProposals?.ProposalIds.Count ?? 0,
            result.LegacyBulk?.RowCount ?? 0,
            result.ArchivedPaths,
            result.ArchiveFailures.Select(failure => failure.Path).ToList());

    private static string Render(string storeDirectory, StoreCutoverResponse response)
    {
        var text = new StringBuilder();
        if (response.AlreadyCutOver)
        {
            text.AppendLine("Store '" + storeDirectory + "' was already taken into the project database.");
            return text.ToString();
        }
        text.AppendLine("Store '" + storeDirectory + "' is now held in the project database.");
        text.AppendLine("  Proposals imported: " + response.ImportedProposals);
        text.AppendLine("  Legacy rows imported: " + response.ImportedLegacyRows);
        foreach (var path in response.ArchivedPaths)
            text.AppendLine("  Archived: " + path);
        if (response.UnarchivedPaths.Count == 0)
            return text.ToString();
        // The cutover succeeded; naming what is left says why the old files are still on disk.
        text.AppendLine("  The database is authoritative, but these sources could not be moved aside:");
        foreach (var path in response.UnarchivedPaths)
            text.AppendLine("    " + path);
        text.AppendLine("  Run this command again to retry moving them; nothing is imported twice.");
        return text.ToString();
    }
}
