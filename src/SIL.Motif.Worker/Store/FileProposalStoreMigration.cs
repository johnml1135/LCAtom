using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Host.Store;

namespace SIL.Motif.Worker.Store;

/// <summary>Imports the staged file Proposal store into the worker-owned SQLite database.</summary>
public static class FileProposalStoreMigration
{
    /// <summary>Imports one file store in one transaction and optionally archives it after commit.</summary>
    public static ProposalMigrationResult ImportInto(
        LegacyProposalStoreLayout source,
        MotifDatabase destination,
        Action<string>? afterBoundary = null,
        bool renameSourceAfterCommit = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var sourcePath = Path.GetFullPath(source.RootDirectory);
        var sourceFiles = ReadSourceFiles(source);
        var digest = Digest(sourceFiles.Values);
        using var connection = destination.OpenConnection();
        if (LedgerExists(connection, "file-proposals", sourcePath, digest))
            return ProposalMigrationResult.Empty(digest);
        if (sourceFiles.Count == 0)
            return ProposalMigrationResult.Empty(digest);

        using var transaction = connection.BeginTransaction();
        try
        {
            var objects = ReadObjectNames(sourceFiles);
            var manifests = ReadManifests(sourceFiles);
            var imported = new List<string>();
            foreach (var manifest in manifests)
            {
                var id = RequiredString(manifest.Text, "proposalId");
                var intent = RequiredString(manifest.Text, "currentIntentDigest");
                var objectName = intent.StartsWith("sha256:", StringComparison.Ordinal) ? intent["sha256:".Length..] : intent;
                if (!objects.TryGetValue(objectName, out var objectFile))
                    throw new InvalidDataException($"Manifest '{manifest.Name}' points to missing Proposal object '{intent}'.");
                var proposalJson = objectFile.Text;
                var status = OptionalString(manifest.Text, "status") ?? "proposed";
                var label = OptionalString(manifest.Text, "label");
                var comment = OptionalString(manifest.Text, "comment");
                var superseded = OptionalString(manifest.Text, "supersededBy");
                var envelope = ProposalJsonParser.Parse(proposalJson);
                if (!string.Equals(envelope.ProposalId.Value, id, StringComparison.Ordinal) ||
                    !string.Equals(IntentDigest.Compute(envelope), intent, StringComparison.Ordinal))
                    throw new InvalidDataException($"Proposal object '{intent}' does not match manifest '{id}'.");
                UpsertProposal(connection, transaction, id, intent, proposalJson, objectFile.Bytes, status, label, comment, superseded);
                afterBoundary?.Invoke("Proposals");
                UpsertRevision(connection, transaction, id, intent, proposalJson, objectFile.Bytes, status, label, comment);
                afterBoundary?.Invoke("ProposalRevisions");
                if (TryDecision(manifest.Text, out var decision))
                {
                    if (!string.Equals(decision.BoundIntentDigest, intent, StringComparison.Ordinal))
                        throw new InvalidDataException($"Decision for Proposal '{id}' is bound to a different intent digest.");
                    UpsertDecision(connection, transaction, id, intent, decision);
                    afterBoundary?.Invoke("Decisions");
                }
                imported.Add(id);
            }

            foreach (var draft in ReadDrafts(sourceFiles))
            {
                var id = RequiredString(draft.Text, "proposalId");
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO Drafts (DraftName, ProposalId, DraftJson)
                    VALUES ($name, $id, $json)
                    ON CONFLICT(DraftName) DO UPDATE SET ProposalId = excluded.ProposalId, DraftJson = excluded.DraftJson;
                    """;
                command.Parameters.AddWithValue("$name", draft.Name);
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$json", draft.Text);
                command.ExecuteNonQuery();
                afterBoundary?.Invoke("Drafts");
            }

            AddLedger(connection, transaction, "file-proposals", sourcePath, digest);
            afterBoundary?.Invoke("MigrationLedger");
            VerifyForeignKeys(connection, transaction);
            transaction.Commit();
            if (renameSourceAfterCommit)
                ArchiveSource(source.RootDirectory);
            return new ProposalMigrationResult(imported, sourceFiles.Count, digest, renameSourceAfterCommit);
        }
        catch
        {
            try { transaction.Rollback(); }
            catch (SqliteException) { }
            throw;
        }
    }

    private static Dictionary<string, SourceFile> ReadSourceFiles(LegacyProposalStoreLayout source)
    {
        var result = new Dictionary<string, SourceFile>(StringComparer.Ordinal);
        foreach (var path in DirectoryFiles(source.ObjectsDirectory, "*.json"))
            result["objects/" + RelativeName(path, source.ObjectsDirectory)] = Read(path, "objects/" + RelativeName(path, source.ObjectsDirectory));
        foreach (var path in DirectoryFiles(source.ManifestsDirectory, "*.json"))
            result["manifests/" + RelativeName(path, source.ManifestsDirectory)] = Read(path, "manifests/" + RelativeName(path, source.ManifestsDirectory));
        foreach (var path in DirectoryFiles(source.DraftsDirectory, "*.json"))
            result["drafts/" + RelativeName(path, source.DraftsDirectory)] = Read(path, "drafts/" + RelativeName(path, source.DraftsDirectory));
        return result;
    }

    private static IEnumerable<string> DirectoryFiles(string directory, string pattern)
        => Directory.Exists(directory) ? Directory.GetFiles(directory, pattern, SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal) : [];

    private static string RelativeName(string path, string directory) =>
        Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');

    private static SourceFile Read(string path, string name) =>
        new(name, File.ReadAllBytes(path), File.ReadAllText(path, Encoding.UTF8));

    private static Dictionary<string, SourceFile> ReadObjectNames(Dictionary<string, SourceFile> files)
    {
        var objects = new Dictionary<string, SourceFile>(StringComparer.Ordinal);
        foreach (var file in files.Values.Where(f => f.Name.StartsWith("objects/", StringComparison.Ordinal)))
            objects[Path.GetFileNameWithoutExtension(file.Name)] = file;
        return objects;
    }

    private static List<ManifestFile> ReadManifests(Dictionary<string, SourceFile> files) =>
        files.Values.Where(f => f.Name.StartsWith("manifests/", StringComparison.Ordinal))
            .Select(f => new ManifestFile(Path.GetFileNameWithoutExtension(f.Name), f.Text, f))
            .OrderBy(f => f.Name, StringComparer.Ordinal).ToList();

    private static List<DraftFile> ReadDrafts(Dictionary<string, SourceFile> files) =>
        files.Values.Where(f => f.Name.StartsWith("drafts/", StringComparison.Ordinal))
            .Select(f => new DraftFile(Path.GetFileNameWithoutExtension(f.Name), f.Text))
            .OrderBy(f => f.Name, StringComparer.Ordinal).ToList();

    private static string RequiredString(string json, string property)
        => OptionalString(json, property) ?? throw new InvalidDataException($"File Proposal JSON is missing '{property}'.");

    private static string? OptionalString(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString() : null;
    }

    private static bool TryDecision(string json, out DecisionValues decision)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("decision", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            decision = default;
            return false;
        }
        decision = new DecisionValues(
            RequiredString(value.GetRawText(), "outcome"),
            RequiredString(value.GetRawText(), "actorType"),
            RequiredString(value.GetRawText(), "actorId"),
            OptionalString(value.GetRawText(), "comment"),
            RequiredString(value.GetRawText(), "boundIntentDigest"),
            RequiredString(value.GetRawText(), "timestampUtc"));
        return true;
    }

    private static void UpsertRevision(SqliteConnection connection, SqliteTransaction transaction, string id, string digest,
        string json, byte[] bytes, string status, string? label, string? comment)
    {
        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT ProposalJson FROM ProposalRevisions WHERE ProposalId = $id AND IntentDigest = $digest;";
            check.Parameters.AddWithValue("$id", id);
            check.Parameters.AddWithValue("$digest", digest);
            var existing = check.ExecuteScalar();
            if (existing is byte[] existingBytes && !existingBytes.SequenceEqual(bytes))
                throw new InvalidDataException($"Proposal revision '{digest}' already exists with different bytes.");
            if (existing is not null) return;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ProposalRevisions
                (ProposalId, IntentDigest, ProposalJson, CreatedUtc)
            VALUES ($id, $digest, $bytes, $utc)
            ON CONFLICT(ProposalId, IntentDigest) DO NOTHING;
            """;
        Add(command, id, digest, json, status, label, comment);
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void UpsertProposal(SqliteConnection connection, SqliteTransaction transaction, string id, string digest,
        string json, byte[] bytes, string status, string? label, string? comment, string? superseded)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Proposals
                (ProposalId, CurrentIntentDigest, Status, Label, Comment, SupersededBy)
            VALUES ($id, $digest, $status, $label, $comment, $superseded)
            ON CONFLICT(ProposalId) DO UPDATE SET CurrentIntentDigest = excluded.CurrentIntentDigest,
                Status = excluded.Status, Label = excluded.Label,
                Comment = excluded.Comment, SupersededBy = excluded.SupersededBy;
            """;
        Add(command, id, digest, json, status, label, comment);
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$superseded", (object?)superseded ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void UpsertDecision(SqliteConnection connection, SqliteTransaction transaction, string id, string digest, DecisionValues decision)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Decisions (ProposalId, IntentDigest, Outcome, ActorType, ActorId, Comment, TimestampUtc)
            VALUES ($id, $digest, $outcome, $actorType, $actorId, $comment, $timestamp)
            ON CONFLICT(ProposalId, IntentDigest) DO UPDATE SET Outcome = excluded.Outcome,
                ActorType = excluded.ActorType, ActorId = excluded.ActorId, Comment = excluded.Comment,
                TimestampUtc = excluded.TimestampUtc;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$outcome", decision.Outcome);
        command.Parameters.AddWithValue("$actorType", decision.ActorType);
        command.Parameters.AddWithValue("$actorId", decision.ActorId);
        command.Parameters.AddWithValue("$comment", (object?)decision.Comment ?? DBNull.Value);
        command.Parameters.AddWithValue("$timestamp", decision.TimestampUtc);
        command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, string id, string digest, string json, string status, string? label, string? comment)
    {
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$label", (object?)label ?? DBNull.Value);
        command.Parameters.AddWithValue("$comment", (object?)comment ?? DBNull.Value);
    }

    private static void VerifyForeignKeys(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        if (reader.Read()) throw new InvalidDataException("Proposal file migration produced a foreign-key violation.");
    }

    internal static bool LedgerExists(SqliteConnection connection, string kind, string path, string digest)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM MigrationLedger WHERE SourceKind = $kind AND SourcePath = $path AND SourceDigest = $digest;";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$digest", digest);
        return command.ExecuteScalar() is not null;
    }

    internal static void AddLedger(SqliteConnection connection, SqliteTransaction transaction, string kind, string path, string digest)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO MigrationLedger (SourceKind, SourcePath, SourceDigest, ImportedUtc)
            VALUES ($kind, $path, $digest, $utc)
            ON CONFLICT(SourceKind, SourcePath, SourceDigest) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    internal static string Digest(IEnumerable<SourceFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.Name));
            hash.AppendData(new byte[] { 0 });
            hash.AppendData(file.Bytes);
        }
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void ArchiveSource(string path)
    {
        var baseArchive = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".migrated";
        var archive = baseArchive;
        var suffix = 1;
        while (Directory.Exists(archive))
            archive = baseArchive + "-" + suffix++;
        Directory.Move(path, archive);
    }

    internal sealed record SourceFile(string Name, byte[] Bytes, string Text);
    private sealed record ManifestFile(string Name, string Text, SourceFile Source);
    private sealed record DraftFile(string Name, string Text);
    private readonly record struct DecisionValues(string Outcome, string ActorType, string ActorId, string? Comment,
        string BoundIntentDigest, string TimestampUtc);
}

/// <summary>Summarizes a file Proposal migration.</summary>
public sealed record ProposalMigrationResult(
    IReadOnlyList<string> ProposalIds,
    int SourceFileCount,
    string SourceDigest,
    bool SourceRenamed)
{
    internal static ProposalMigrationResult Empty(string digest) => new(Array.Empty<string>(), 0, digest, false);
}

/// <summary>Read-only path layout used to import the legacy CLI Proposal files.</summary>
public sealed record LegacyProposalStoreLayout(string RootDirectory)
{
    /// <summary>Directory containing mutable local drafts.</summary>
    public string DraftsDirectory => Path.Combine(RootDirectory, "drafts");
    /// <summary>Directory containing immutable Proposal objects.</summary>
    public string ObjectsDirectory => Path.Combine(RootDirectory, "objects");
    /// <summary>Directory containing mutable Proposal manifests.</summary>
    public string ManifestsDirectory => Path.Combine(RootDirectory, "manifests");
}
