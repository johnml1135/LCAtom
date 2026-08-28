using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SIL.Motif.Host.Store;

/// <summary>One project this installation has been pointed at, and when it was last seen.</summary>
public sealed record KnownProjectRecord(string WorkspaceKey, string FullFwDataPath, DateTimeOffset LastSeenUtc);

/// <summary>
/// Maintains <c>KnownProjects</c> in the machine store: the list the job runner sweeps to find work in a
/// project it was not launched with. Every verb that names a project upserts it here on the way past, so
/// there is no separate registration step and no way for the list to go stale by omission. A project whose
/// <c>.fwdata</c> file has since gone missing is still recorded here — it is dropped by the next sweep,
/// not refused at recording time.
/// </summary>
public sealed class KnownProjectRegistry
{
    private readonly MachineDatabase _database;

    public KnownProjectRegistry(MachineDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>Upserts a project by its workspace key; a second call for the same key updates the last-seen time.</summary>
    public KnownProjectRecord Record(string workspaceKey, string fullFwDataPath, DateTimeOffset lastSeenUtc)
    {
        RequireWorkspaceKey(workspaceKey);
        if (string.IsNullOrWhiteSpace(fullFwDataPath))
            throw new ArgumentException("A full .fwdata path is required.", nameof(fullFwDataPath));
        if (lastSeenUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("The last-seen time must be UTC.", nameof(lastSeenUtc));

        var path = Path.GetFullPath(fullFwDataPath);
        var seen = lastSeenUtc.ToString("O", CultureInfo.InvariantCulture);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO KnownProjects (WorkspaceKey, FullFwDataPath, LastSeenUtc)
            VALUES ($key, $path, $seen)
            ON CONFLICT(WorkspaceKey) DO UPDATE SET
                FullFwDataPath = excluded.FullFwDataPath,
                LastSeenUtc = excluded.LastSeenUtc;
            """;
        command.Parameters.AddWithValue("$key", workspaceKey);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$seen", seen);
        command.ExecuteNonQuery();
        return new KnownProjectRecord(workspaceKey, path, lastSeenUtc);
    }

    /// <summary>Lists every recorded project, ordered by workspace key.</summary>
    public IReadOnlyList<KnownProjectRecord> List()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT WorkspaceKey, FullFwDataPath, LastSeenUtc FROM KnownProjects ORDER BY WorkspaceKey;";
        using var reader = command.ExecuteReader();
        var records = new List<KnownProjectRecord>();
        while (reader.Read()) records.Add(Read(reader));
        return records;
    }

    /// <summary>Removes a project from the registry. A project not present is left silently absent.</summary>
    public void Forget(string workspaceKey)
    {
        RequireWorkspaceKey(workspaceKey);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM KnownProjects WHERE WorkspaceKey = $key;";
        command.Parameters.AddWithValue("$key", workspaceKey);
        command.ExecuteNonQuery();
    }

    private static KnownProjectRecord Read(SqliteDataReader reader)
    {
        try
        {
            var lastSeen = DateTimeOffset.ParseExact(reader.GetString(2), "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
            if (lastSeen.Offset != TimeSpan.Zero)
                throw new InvalidDataException("The persisted last-seen time is not UTC.");
            return new KnownProjectRecord(reader.GetString(0), reader.GetString(1), lastSeen);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            throw new InvalidDataException("The persisted Known project row is malformed.", exception);
        }
    }

    private static void RequireWorkspaceKey(string workspaceKey)
    {
        if (string.IsNullOrWhiteSpace(workspaceKey))
            throw new ArgumentException("A project workspace key is required.", nameof(workspaceKey));
    }
}
