using System;
using System.IO;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Responses;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Cli;

/// <summary>
/// Runs one verb against a project's paired Motif store, and turns any failure into the CLI's contract.
/// </summary>
/// <remarks>
/// <para>
/// What a verb author would otherwise have to know: that the project file must exist before it can key a
/// workspace, that the schema constant and the product version go into the catalog together, and which
/// exception out of the store means which <see cref="FailureReason"/>. That last one is the real content
/// here — a held database and a malformed row both surface as exceptions thrown far away, and telling
/// them apart decides whether a caller is told to retry.
/// </para>
/// <para>
/// A verb supplies only what it does with an open store. Everything above is settled once.
/// </para>
/// </remarks>
public static class ProjectStoreCommand
{
    /// <summary>Opens the paired store for a project, runs the verb, and translates any failure.</summary>
    public static CommandResult Run(string fwDataPath, string productVersion,
        Func<MotifDatabase, ProjectLocator, CommandResult> act)
    {
        ArgumentNullException.ThrowIfNull(act);

        ProjectLocator project;
        try
        {
            project = Locate(fwDataPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return Refuse(FailureReason.InvalidArgument, exception.Message);
        }

        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, ParseVersion(productVersion));
        try
        {
            using var database = catalog.OpenOwned(project);
            return act(database, project);
        }
        catch (IOException exception)
        {
            // Someone else has the store; the caller may try again once they let go.
            return Refuse(FailureReason.Busy, exception.Message);
        }
        catch (NotSupportedException exception)
        {
            // A schema this build cannot open. Retrying will not help; updating Motif will.
            return Refuse(FailureReason.Refused, exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return Refuse(FailureReason.StoreInconsistent, exception.Message);
        }
    }

    /// <summary>Renders one refusal in the shape every verb's failures take.</summary>
    public static CommandResult Refuse(FailureReason reason, string message) =>
        new(FailureEnvelope.ExitCodeFor(reason), "error: " + message + Environment.NewLine, reason);

    /// A malformed product version must not stop a verb; the compatibility floor it feeds is a lower bound.
    private static Version ParseVersion(string productVersion) =>
        Version.TryParse(productVersion, out var parsed) ? parsed : new Version(1, 0);

    /// The file must exist: an unresolvable path would key a second, empty workspace instead of the real one.
    private static ProjectLocator Locate(string fwDataPath)
    {
        var full = Path.GetFullPath(fwDataPath);
        if (!File.Exists(full))
            throw new FileNotFoundException("Project file not found: '" + full + "'.", full);
        return new ProjectLocator(full, Path.GetFileNameWithoutExtension(full));
    }
}
