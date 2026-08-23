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
        bool renameSourceAfterCommit = true,
        Action? beforeCommit = null,
        Action? beforeArchive = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var sourcePath = Path.GetFullPath(source.RootDirectory);
        if (PathsEqual(sourcePath, destination.FullPath))
            throw new InvalidOperationException("A file Proposal source cannot be the destination Motif database.");
        var sourceFiles = ReadSourceFiles(source);
        var digest = Digest(sourceFiles.Values);
        using var connection = destination.OpenConnection();
        if (LedgerExists(connection, "file-proposals", sourcePath, digest))
        {
            if (renameSourceAfterCommit && Directory.Exists(source.RootDirectory))
            {
                beforeArchive?.Invoke();
                EnsureSourceDigest(source, digest);
                ArchiveSource(source.RootDirectory, digest);
                return new ProposalMigrationResult(Array.Empty<string>(), sourceFiles.Count, digest, true);
            }
            return ProposalMigrationResult.Empty(digest);
        }
        if (sourceFiles.Count == 0)
            return ProposalMigrationResult.Empty(digest);

        using var transaction = connection.BeginTransaction();
        try
        {
            var objects = ReadObjectNames(sourceFiles);
            var manifests = ReadManifests(sourceFiles).Select(ToManifestInfo).ToList();
            ValidateObjectsAndManifests(objects, manifests);
            var imported = new List<string>();
            foreach (var manifest in manifests)
            {
                UpsertProposal(connection, transaction, manifest.Id, manifest.CurrentIntentDigest, manifest.Status,
                    manifest.Label, manifest.Comment, manifest.SupersededBy, manifest.AnchorJson);
                afterBoundary?.Invoke("Proposals");
                imported.Add(manifest.Id);
            }

            foreach (var objectFile in objects.Values)
            {
                var envelope = ProposalJsonParser.Parse(objectFile.File.Text);
                UpsertRevision(connection, transaction, envelope.ProposalId.Value, objectFile.Digest,
                    objectFile.File.Bytes);
                afterBoundary?.Invoke("ProposalRevisions");
            }

            foreach (var manifest in manifests)
            {
                if (manifest.Decision is not null)
                {
                    UpsertDecision(connection, transaction, manifest.Id, manifest.CurrentIntentDigest, manifest.Decision.Value);
                    afterBoundary?.Invoke("Decisions");
                }
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

            beforeCommit?.Invoke();
            var finalFiles = ReadSourceFiles(source);
            var finalDigest = Digest(finalFiles.Values);
            if (!string.Equals(finalDigest, digest, StringComparison.Ordinal))
                throw new InvalidDataException("The file Proposal source changed during migration.");
            AddLedger(connection, transaction, "file-proposals", sourcePath, finalDigest);
            afterBoundary?.Invoke("MigrationLedger");
            VerifyForeignKeys(connection, transaction);
            transaction.Commit();
            if (renameSourceAfterCommit)
            {
                beforeArchive?.Invoke();
                EnsureSourceDigest(source, digest);
                ArchiveSource(source.RootDirectory, digest);
            }
            return new ProposalMigrationResult(imported, sourceFiles.Count, digest, renameSourceAfterCommit);
        }
        catch
        {
            try { transaction.Rollback(); }
            catch (Exception exception) when (exception is SqliteException or InvalidOperationException) { }
            throw;
        }
    }

    private static Dictionary<string, SourceFile> ReadSourceFiles(LegacyProposalStoreLayout source)
    {
        var result = new Dictionary<string, SourceFile>(StringComparer.Ordinal);
        foreach (var path in JsonFiles(source.ObjectsDirectory))
            result["objects/" + RelativeName(path, source.ObjectsDirectory)] = Read(path, "objects/" + RelativeName(path, source.ObjectsDirectory));
        foreach (var path in JsonFiles(source.ManifestsDirectory))
            result["manifests/" + RelativeName(path, source.ManifestsDirectory)] = Read(path, "manifests/" + RelativeName(path, source.ManifestsDirectory));
        foreach (var path in JsonFiles(source.DraftsDirectory))
            result["drafts/" + RelativeName(path, source.DraftsDirectory)] = Read(path, "drafts/" + RelativeName(path, source.DraftsDirectory));
        return result;
    }

    private static IEnumerable<string> DirectoryFiles(string directory, string pattern)
        => Directory.Exists(directory) ? Directory.GetFiles(directory, pattern, SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal) : [];

    private static IEnumerable<string> JsonFiles(string directory)
    {
        foreach (var path in DirectoryFiles(directory, "*"))
        {
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Legacy Proposal store contains a non-JSON file '{path}'.");
            yield return path;
        }
    }

    private static string RelativeName(string path, string directory) =>
        Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');

    private static SourceFile Read(string path, string name)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new(name, bytes, new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"Legacy Proposal file '{name}' is not valid UTF-8.", exception);
        }
    }

    private static Dictionary<string, ObjectFile> ReadObjectNames(Dictionary<string, SourceFile> files)
    {
        var objects = new Dictionary<string, ObjectFile>(StringComparer.Ordinal);
        foreach (var file in files.Values.Where(f => f.Name.StartsWith("objects/", StringComparison.Ordinal)))
        {
            var name = TrimJsonSuffix(file.Name["objects/".Length..]);
            var digest = name.StartsWith("sha256:", StringComparison.Ordinal) ? name : "sha256:" + name;
            if (!objects.TryAdd(digest, new ObjectFile(digest, file)))
                throw new InvalidDataException($"Duplicate Proposal object digest '{digest}'.");
        }
        return objects;
    }

    private static List<ManifestFile> ReadManifests(Dictionary<string, SourceFile> files) =>
        files.Values.Where(f => f.Name.StartsWith("manifests/", StringComparison.Ordinal))
            .Select(f => new ManifestFile(TrimJsonSuffix(f.Name["manifests/".Length..]), f.Text, f))
            .OrderBy(f => f.Name, StringComparer.Ordinal).ToList();

    private static List<DraftFile> ReadDrafts(Dictionary<string, SourceFile> files) =>
        files.Values.Where(f => f.Name.StartsWith("drafts/", StringComparison.Ordinal))
            .Select(f => new DraftFile(TrimJsonSuffix(f.Name["drafts/".Length..]), f.Text))
            .OrderBy(f => f.Name, StringComparer.Ordinal).ToList();

    private static ManifestInfo ToManifestInfo(ManifestFile manifest)
    {
        var id = RequiredString(manifest.Text, "proposalId");
        if (!string.Equals(manifest.Name, id, StringComparison.Ordinal))
            throw new InvalidDataException($"Manifest filename '{manifest.Name}' does not match Proposal id '{id}'.");
        var current = RequiredString(manifest.Text, "currentIntentDigest");
        DecisionValues? decision = TryDecision(manifest.Text, out var parsedDecision) ? parsedDecision : null;
        if (decision is not null && !string.Equals(decision.Value.BoundIntentDigest, current, StringComparison.Ordinal))
            throw new InvalidDataException($"Decision for Proposal '{id}' is bound to a different intent digest.");
        return new ManifestInfo(id, current, OptionalString(manifest.Text, "status") ?? "proposed",
            OptionalString(manifest.Text, "label"), OptionalString(manifest.Text, "comment"),
            OptionalString(manifest.Text, "supersededBy"), OptionalRaw(manifest.Text, "anchor"), decision);
    }

    private static void ValidateObjectsAndManifests(
        IReadOnlyDictionary<string, ObjectFile> objects, IReadOnlyList<ManifestInfo> manifests)
    {
        var manifestIds = manifests.Select(manifest => manifest.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var objectFile in objects.Values)
        {
            var envelope = ProposalJsonParser.Parse(objectFile.File.Text);
            var computed = IntentDigest.Compute(envelope);
            if (!string.Equals(computed, objectFile.Digest, StringComparison.Ordinal))
                throw new InvalidDataException($"Proposal object filename digest '{objectFile.Digest}' does not match its content.");
            if (!manifestIds.Contains(envelope.ProposalId.Value))
                throw new InvalidDataException($"Proposal object '{objectFile.Digest}' has no matching manifest.");
        }
        foreach (var manifest in manifests)
        {
            if (!objects.TryGetValue(manifest.CurrentIntentDigest, out var objectFile))
                throw new InvalidDataException($"Manifest '{manifest.Id}' points at missing Proposal object '{manifest.CurrentIntentDigest}'.");
            var envelope = ProposalJsonParser.Parse(objectFile.File.Text);
            if (!string.Equals(envelope.ProposalId.Value, manifest.Id, StringComparison.Ordinal))
                throw new InvalidDataException($"Proposal object '{manifest.CurrentIntentDigest}' does not match manifest '{manifest.Id}'.");
        }
    }

    private static string? OptionalRaw(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetRawText() : null;
    }

    private static string TrimJsonSuffix(string name) =>
        name.EndsWith(".json", StringComparison.Ordinal) ? name[..^5] : name;

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
        byte[] bytes)
    {
        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT ProposalJson FROM ProposalRevisions WHERE ProposalId = $id AND IntentDigest = $digest;";
            check.Parameters.AddWithValue("$id", id);
            check.Parameters.AddWithValue("$digest", digest);
            var existing = check.ExecuteScalar();
            if (existing is not null && existing is not byte[])
                throw new InvalidDataException("Proposal revision JSON is not stored as a BLOB.");
            if (existing is byte[] existingBytes)
            {
                if (!existingBytes.SequenceEqual(bytes))
                    throw new InvalidDataException($"Proposal revision '{digest}' already exists with different bytes.");
                return;
            }
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ProposalRevisions
                (ProposalId, IntentDigest, ProposalJson, CreatedUtc)
            VALUES ($id, $digest, $bytes, $utc)
            ON CONFLICT(ProposalId, IntentDigest) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void UpsertProposal(SqliteConnection connection, SqliteTransaction transaction, string id, string digest,
        string status, string? label, string? comment, string? superseded, string? anchorJson)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Proposals
                (ProposalId, CurrentIntentDigest, Status, Label, Comment, SupersededBy, AnchorJson)
            VALUES ($id, $digest, $status, $label, $comment, $superseded, $anchor)
            ON CONFLICT(ProposalId) DO UPDATE SET CurrentIntentDigest = excluded.CurrentIntentDigest,
                Status = excluded.Status, Label = excluded.Label,
                Comment = excluded.Comment, SupersededBy = excluded.SupersededBy,
                AnchorJson = excluded.AnchorJson;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$label", (object?)label ?? DBNull.Value);
        command.Parameters.AddWithValue("$comment", (object?)comment ?? DBNull.Value);
        command.Parameters.AddWithValue("$superseded", (object?)superseded ?? DBNull.Value);
        command.Parameters.AddWithValue("$anchor", (object?)anchorJson ?? DBNull.Value);
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

    private static void VerifyForeignKeys(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        if (reader.Read()) throw new InvalidDataException("Proposal file migration produced a foreign-key violation.");
    }

    internal static bool LedgerExists(SqliteConnection connection, string kind, string path, string digest,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
            AppendFrame(hash, Encoding.UTF8.GetBytes(file.Name));
            AppendFrame(hash, file.Bytes);
        }
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendFrame(IncrementalHash hash, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison);
    }

    private static void EnsureSourceDigest(LegacyProposalStoreLayout source, string expectedDigest)
    {
        if (!Directory.Exists(source.RootDirectory) ||
            !string.Equals(Digest(ReadSourceFiles(source).Values), expectedDigest, StringComparison.Ordinal))
            throw new InvalidDataException("The file Proposal source changed before archival.");
    }

    private static void ArchiveSource(string path, string expectedDigest)
    {
        var baseArchive = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".migrated";
        var archive = baseArchive;
        var suffix = 1;
        while (Directory.Exists(archive) || File.Exists(archive))
            archive = baseArchive + "-" + suffix++;
        Directory.Move(path, archive);
        try
        {
            EnsureSourceDigest(new LegacyProposalStoreLayout(archive), expectedDigest);
        }
        catch
        {
            if (!Directory.Exists(path) && Directory.Exists(archive))
                Directory.Move(archive, path);
            throw;
        }
    }

    internal sealed record SourceFile(string Name, byte[] Bytes, string Text);
    private sealed record ObjectFile(string Digest, SourceFile File);
    private sealed record ManifestFile(string Name, string Text, SourceFile Source);
    private sealed record ManifestInfo(string Id, string CurrentIntentDigest, string Status, string? Label,
        string? Comment, string? SupersededBy, string? AnchorJson, DecisionValues? Decision);
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
