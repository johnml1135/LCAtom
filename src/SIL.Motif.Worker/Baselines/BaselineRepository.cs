using System.Globalization;
using Microsoft.Data.Sqlite;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Host.Store;

namespace SIL.Motif.Worker.Baselines;

internal sealed record BaselineRecord(
    string ProjectKey,
    BaselineToken Token,
    string RootDirectory,
    string FwDataPath,
    DateTimeOffset PublishedUtc);

internal sealed class BaselineRepository
{
    private readonly MotifDatabase _database;

    public BaselineRepository(MotifDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public BaselineRecord? GetCurrent(string projectKey)
    {
        RequireProjectKey(projectKey);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE ProjectKey = $project;";
        command.Parameters.AddWithValue("$project", projectKey);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public BaselineRecord Record(
        string projectKey, BaselinePublication publication, DateTimeOffset publishedUtc)
    {
        RequireProjectKey(projectKey);
        ArgumentNullException.ThrowIfNull(publication);
        if (publishedUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("The publication time must be UTC.", nameof(publishedUtc));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Baselines
                (ProjectKey, ProjectIdentity, SemanticSnapshotDigest, ProjectionVersion, CapturedUtc,
                 BundleDigest, CapturedHostSessionId, CapturedEditGeneration, RootDirectory, FwDataPath, PublishedUtc)
            VALUES
                ($project, $identity, $semantic, $projection, $captured, $bundle, $hostSession,
                 $editGeneration, $root, $fwdata, $published)
            ON CONFLICT(ProjectKey) DO UPDATE SET
                ProjectIdentity = excluded.ProjectIdentity,
                SemanticSnapshotDigest = excluded.SemanticSnapshotDigest,
                ProjectionVersion = excluded.ProjectionVersion,
                CapturedUtc = excluded.CapturedUtc,
                BundleDigest = excluded.BundleDigest,
                CapturedHostSessionId = excluded.CapturedHostSessionId,
                CapturedEditGeneration = excluded.CapturedEditGeneration,
                RootDirectory = excluded.RootDirectory,
                FwDataPath = excluded.FwDataPath,
                PublishedUtc = excluded.PublishedUtc
            WHERE Baselines.BundleDigest <> excluded.BundleDigest;
            """;
        AddParameters(command, projectKey, publication, publishedUtc);
        command.ExecuteNonQuery();
        command.CommandText = SelectSql + " WHERE ProjectKey = $project;";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("The Baseline publication was not recorded.");
        var result = Read(reader);
        reader.Close();
        transaction.Commit();
        return result;
    }

    private static void AddParameters(SqliteCommand command, string projectKey,
        BaselinePublication publication, DateTimeOffset publishedUtc)
    {
        var token = publication.Token;
        command.Parameters.AddWithValue("$project", projectKey);
        command.Parameters.AddWithValue("$identity", token.ProjectIdentity);
        command.Parameters.AddWithValue("$semantic", token.SemanticSnapshotDigest);
        command.Parameters.AddWithValue("$projection", token.ProjectionVersion);
        command.Parameters.AddWithValue("$captured", token.CapturedUtc);
        command.Parameters.AddWithValue("$bundle", token.BundleDigest);
        command.Parameters.AddWithValue("$hostSession", (object?)token.CapturedHostSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$editGeneration", (object?)token.CapturedEditGeneration ?? DBNull.Value);
        command.Parameters.AddWithValue("$root", Path.GetFullPath(publication.RootDirectory));
        command.Parameters.AddWithValue("$fwdata", Path.GetFullPath(publication.FwDataPath));
        command.Parameters.AddWithValue("$published", publishedUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    private static BaselineRecord Read(SqliteDataReader reader)
    {
        try
        {
            var token = new BaselineToken(reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7));
            var published = DateTimeOffset.ParseExact(reader.GetString(10), "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
            if (published.Offset != TimeSpan.Zero)
                throw new InvalidDataException("The persisted Baseline publication time is not UTC.");
            return new BaselineRecord(reader.GetString(0), token, Path.GetFullPath(reader.GetString(8)),
                Path.GetFullPath(reader.GetString(9)), published);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidCastException or
            InvalidOperationException or OverflowException)
        {
            throw new InvalidDataException("The persisted Baseline row is malformed.", exception);
        }
    }

    private static void RequireProjectKey(string projectKey)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
            throw new ArgumentException("A project workspace key is required.", nameof(projectKey));
    }

    private const string SelectSql = "SELECT ProjectKey, ProjectIdentity, SemanticSnapshotDigest, " +
        "ProjectionVersion, CapturedUtc, BundleDigest, CapturedHostSessionId, CapturedEditGeneration, " +
        "RootDirectory, FwDataPath, PublishedUtc FROM Baselines";
}
