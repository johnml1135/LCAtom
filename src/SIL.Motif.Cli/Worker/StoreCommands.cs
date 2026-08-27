using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Store;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Cli.Worker;

/// <summary>
/// The CLI verbs that take this machine's legacy store into the paired project database.
/// </summary>
/// <remarks>
/// The cutover runs in this process, and exclusion is the paired database's own owner lock rather than a
/// lease inside a server, so it spans processes: a second invocation is refused outright and leaves the
/// store untouched, pinned by
/// `AnotherProcessHoldingTheDatabaseRefusesTheCutoverRatherThanWaiting`.
/// </remarks>
public static class StoreCommands
{
    /// <summary>Cuts one store location over to the paired database and renders what moved.</summary>
    public static CommandResult Cutover(string storeDirectory, string fwDataPath, string productVersion)
    {
        ProjectLocator project;
        try
        {
            project = Locate(fwDataPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return new CommandResult(1, "error: " + exception.Message + Environment.NewLine);
        }

        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, ParseVersion(productVersion));
        try
        {
            using var database = catalog.OpenOwned(project);
            var result = ProjectStoreCutover.Run(storeDirectory, database);
            return new CommandResult(0, Render(storeDirectory, Describe(project, result)));
        }
        catch (IOException exception)
        {
            // The owner lock is held elsewhere, or the database is unreadable; both are "try again", not a bug.
            return new CommandResult(1, "error: " + exception.Message + Environment.NewLine);
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidDataException)
        {
            return new CommandResult(1, "error: " + exception.Message + Environment.NewLine);
        }
    }

    private static StoreCutoverResponse Describe(ProjectLocator project, ProjectStoreCutoverResult result) =>
        new StoreCutoverResponse(
            ProjectWorkspaceKey.Compute(project),
            result.FileProposals is null && result.LegacyBulk is null,
            result.FileProposals?.ProposalIds.Count ?? 0,
            result.LegacyBulk?.RowCount ?? 0,
            result.ArchivedPaths,
            result.ArchiveFailures.Select(failure => failure.Path).ToList());

    /// A malformed product version must not stop a cutover; the compatibility floor it feeds is a lower bound.
    private static Version ParseVersion(string productVersion) =>
        Version.TryParse(productVersion, out var parsed) ? parsed : new Version(1, 0);

    /// The file must exist: an unresolvable path would key a second, empty workspace instead of the real one.
    private static ProjectLocator Locate(string fwDataPath)
    {
        var full = System.IO.Path.GetFullPath(fwDataPath);
        if (!System.IO.File.Exists(full))
            throw new FileNotFoundException("Project file not found: '" + full + "'.", full);
        return new ProjectLocator(full, System.IO.Path.GetFileNameWithoutExtension(full));
    }

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
