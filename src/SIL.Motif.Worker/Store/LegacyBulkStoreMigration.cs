using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SIL.Motif.Host.Store;

namespace SIL.Motif.Worker.Store;

/// <summary>Imports the legacy Motif bulk SQLite store with explicit table and column maps.</summary>
public static class LegacyBulkStoreMigration
{
    private static readonly TableMap[] Maps =
    [
        new("Corpora", ["CorpusId", "ProvenanceJson"], ["CorpusId"]),
        new("CorpusDocuments", ["CorpusId", "DocumentId", "OrdinalIndex", "Title", "Source", "Text",
            "ContentSha256", "IngestedUtc", "Licence", "CapabilitiesJson", "AttributesJson"], ["CorpusId", "DocumentId"]),
        new("Assessments", ["AssessmentId", "CorpusId", "CorpusWordsJson", "CorpusSha256", "CorpusProvenanceJson",
            "OutcomeDigest", "SemanticDigest", "GrammarSourceSha256", "ModelFingerprint", "Pipeline",
            "DiagnosticCount", "SavedUtc"], ["AssessmentId"]),
        new("AssessedWords", ["AssessedWordId", "AssessmentId", "OrdinalIndex", "Word", "Outcome"], ["AssessedWordId"]),
        new("ParsedAnalyses", ["AssessedWordId", "OrdinalIndex", "CategoryGuid", "MorphemeGuidsJson", "RootIndex",
            "IdentityDigest"], ["AssessedWordId", "OrdinalIndex"]),
        new("AssessmentPins", ["AssessmentId", "PinnedBy", "PinnedUtc"], ["AssessmentId", "PinnedBy"])
    ];

    /// <summary>Copies known corpus and Assessment tables in one destination transaction.</summary>
    public static LegacyMigrationResult ImportInto(string legacyPath, MotifDatabase destination,
        Action<string>? afterBoundary = null, bool renameSourceAfterCommit = true)
    {
        if (string.IsNullOrWhiteSpace(legacyPath)) throw new ArgumentException("A source path is required.", nameof(legacyPath));
        ArgumentNullException.ThrowIfNull(destination);
        var fullPath = Path.GetFullPath(legacyPath);
        using var connection = destination.OpenConnection();
        if (!File.Exists(fullPath))
        {
            if (LedgerPathExists(connection, "legacy-bulk", fullPath)) return new LegacyMigrationResult("", 0, false);
            throw new FileNotFoundException("Legacy Motif database was not found.", fullPath);
        }
        AttachReadOnly(connection, fullPath);
        var committed = false;
        try
        {
            var digest = LogicalDigest(connection);
            if (FileProposalStoreMigration.LedgerExists(connection, "legacy-bulk", fullPath, digest))
                return new LegacyMigrationResult(digest, 0, false);
            var available = ReadTables(connection);
            ValidateSourceSchema(connection, available);
            using var transaction = connection.BeginTransaction();
            try
            {
                var count = 0;
                foreach (var map in Maps) count += CopyTable(connection, transaction, map, available, afterBoundary);
                FileProposalStoreMigration.AddLedger(connection, transaction, "legacy-bulk", fullPath, digest);
                afterBoundary?.Invoke("MigrationLedger");
                VerifyForeignKeys(connection, transaction);
                transaction.Commit();
                committed = true;
                return new LegacyMigrationResult(digest, count, renameSourceAfterCommit);
            }
            catch
            {
                try { transaction.Rollback(); }
                catch (SqliteException) { }
                throw;
            }
        }
        finally
        {
            try
            {
                using var detach = connection.CreateCommand();
                detach.CommandText = "DETACH DATABASE legacy;";
                detach.ExecuteNonQuery();
            }
            catch (SqliteException) { }
            if (committed && renameSourceAfterCommit)
            {
                connection.Close();
                SqliteConnection.ClearAllPools();
                ArchiveSource(fullPath);
            }
        }
    }

    private static void AttachReadOnly(SqliteConnection connection, string path)
    {
        var uri = "file:///" + path.Replace('\\', '/') + "?mode=ro";
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"ATTACH DATABASE '{uri.Replace("'", "''", StringComparison.Ordinal)}' AS legacy;";
            try
            {
                command.ExecuteNonQuery();
                return;
            }
            catch (SqliteException) { }
        }
        using var fallback = connection.CreateCommand();
        fallback.CommandText = "ATTACH DATABASE $path AS legacy;";
        fallback.Parameters.AddWithValue("$path", path);
        fallback.ExecuteNonQuery();
    }

    private static HashSet<string> ReadTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM legacy.sqlite_master WHERE type = 'table';";
        using var reader = command.ExecuteReader();
        var tables = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) tables.Add(reader.GetString(0));
        return tables;
    }

    private static string LogicalDigest(SqliteConnection connection)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var tablesCommand = connection.CreateCommand();
        tablesCommand.CommandText = "SELECT name, sql FROM legacy.sqlite_master WHERE type = 'table' ORDER BY name;";
        using var tables = tablesCommand.ExecuteReader();
        while (tables.Read())
        {
            var table = tables.GetString(0);
            Append(hash, table);
            Append(hash, tables.IsDBNull(1) ? "" : tables.GetString(1));
            var columns = ReadSourceColumns(connection, table);
            foreach (var column in columns) Append(hash, column);
            using var rowsCommand = connection.CreateCommand();
            rowsCommand.CommandText = $"SELECT {string.Join(", ", columns.Select(Quote))} FROM legacy.{Quote(table)} ORDER BY rowid;";
            using var rows = rowsCommand.ExecuteReader();
            while (rows.Read())
                for (var index = 0; index < rows.FieldCount; index++) Append(hash, ValueText(rows.GetValue(index)));
        }
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string[] ReadSourceColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA legacy.table_info({Quote(table)});";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns.ToArray();
    }

    private static void ValidateSourceSchema(SqliteConnection connection, HashSet<string> available)
    {
        foreach (var map in Maps)
        {
            if (!available.Contains(map.Name))
                throw new InvalidDataException($"Legacy database is missing required table {map.Name}.");
            var columns = ReadSourceColumns(connection, map.Name);
            var missing = map.Columns.Where(column => !columns.Contains(column, StringComparer.Ordinal)).ToArray();
            if (missing.Length > 0)
                throw new InvalidDataException($"Legacy table {map.Name} is missing columns: {string.Join(", ", missing)}.");
        }
    }

    private static int CopyTable(SqliteConnection connection, SqliteTransaction transaction, TableMap map,
        HashSet<string> available, Action<string>? afterBoundary)
    {
        if (!available.Contains(map.Name))
        {
            afterBoundary?.Invoke(map.Name);
            return 0;
        }
        var columnList = string.Join(", ", map.Columns.Select(Quote));
        using var totalCommand = connection.CreateCommand();
        totalCommand.Transaction = transaction;
        totalCommand.CommandText = $"SELECT COUNT(*) FROM legacy.{Quote(map.Name)};";
        var total = Convert.ToInt32(totalCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        var count = 0;
        for (var offset = 0; offset < total; offset++)
        {
            using var source = connection.CreateCommand();
            source.Transaction = transaction;
            source.CommandText = $"SELECT {columnList} FROM legacy.{Quote(map.Name)} ORDER BY rowid LIMIT 1 OFFSET $offset;";
            source.Parameters.AddWithValue("$offset", offset);
            using var reader = source.ExecuteReader();
            if (!reader.Read()) throw new InvalidDataException($"Legacy table {map.Name} changed during import.");
            var values = new object?[map.Columns.Length];
            reader.GetValues(values);
            var existing = FindExisting(connection, transaction, map, values);
            if (existing is not null)
            {
                for (var index = 0; index < values.Length; index++)
                    if (!ValueEquals(existing[index], values[index]))
                        throw new InvalidDataException($"Legacy row conflicts with destination row in {map.Name}.");
                count++;
                continue;
            }
            Insert(connection, transaction, map, values);
            count++;
        }
        afterBoundary?.Invoke(map.Name);
        return count;
    }

    private static object?[]? FindExisting(SqliteConnection connection, SqliteTransaction transaction, TableMap map,
        object?[] values)
    {
        var keyIndexes = map.Keys.Select(key => Array.IndexOf(map.Columns, key)).ToArray();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {string.Join(", ", map.Columns.Select(Quote))} FROM {Quote(map.Name)} WHERE " +
            string.Join(" AND ", keyIndexes.Select(index => Quote(map.Columns[index]) + " IS $k" + index)) + ";";
        foreach (var index in keyIndexes) AddValue(command, "$k" + index, values[index]);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var existing = new object?[map.Columns.Length];
        reader.GetValues(existing);
        return existing;
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, TableMap map, object?[] values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = string.Join(", ", map.Columns.Select((_, index) => "$p" + index));
        command.CommandText = $"INSERT INTO {Quote(map.Name)} ({string.Join(", ", map.Columns.Select(Quote))}) VALUES ({parameters});";
        for (var index = 0; index < values.Length; index++) AddValue(command, "$p" + index, values[index]);
        command.ExecuteNonQuery();
    }

    private static void AddValue(SqliteCommand command, string name, object? value)
    {
        var parameter = command.Parameters.Add(name, value switch
        {
            null or DBNull => SqliteType.Text,
            byte[] => SqliteType.Blob,
            long or int or short => SqliteType.Integer,
            double or float or decimal => SqliteType.Real,
            _ => SqliteType.Text
        });
        parameter.Value = value ?? DBNull.Value;
    }

    private static bool ValueEquals(object? left, object? right)
    {
        if (left is DBNull) left = null;
        if (right is DBNull) right = null;
        if (left is byte[] leftBytes && right is byte[] rightBytes) return leftBytes.SequenceEqual(rightBytes);
        return Equals(left, right);
    }

    private static string ValueText(object value) => value switch
    {
        DBNull => "null",
        byte[] bytes => "blob:" + Convert.ToHexString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData(new byte[] { 0 });
    }

    private static void VerifyForeignKeys(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        if (reader.Read()) throw new InvalidDataException("Legacy migration produced a foreign-key violation.");
    }

    private static bool LedgerPathExists(SqliteConnection connection, string kind, string path)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM MigrationLedger WHERE SourceKind = $kind AND SourcePath = $path LIMIT 1;";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$path", path);
        return command.ExecuteScalar() is not null;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static void ArchiveSource(string path)
    {
        var baseArchive = path + ".migrated";
        var archive = baseArchive;
        var suffix = 1;
        while (File.Exists(archive)) archive = baseArchive + "-" + suffix++;
        File.Move(path, archive);
    }

    private sealed record TableMap(string Name, string[] Columns, string[] Keys);
}

/// <summary>Summarizes a legacy bulk migration.</summary>
public sealed record LegacyMigrationResult(string SourceDigest, int RowCount, bool SourceRenamed);
