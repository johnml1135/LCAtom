using System.Buffers.Binary;
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

    private static readonly IReadOnlyDictionary<string, (string Table, string[] Columns)> KnownIndexes =
        new Dictionary<string, (string, string[])>(StringComparer.Ordinal)
        {
            ["IX_AssessedWords_Assessment"] = ("AssessedWords", ["AssessmentId"]),
            ["IX_AssessedWords_Word"] = ("AssessedWords", ["AssessmentId", "Word"]),
            ["IX_ParsedAnalyses_Word"] = ("ParsedAnalyses", ["AssessedWordId"])
        };

    private static readonly IReadOnlyDictionary<string, string[]> ExpectedTypes =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Corpora"] = ["TEXT", "TEXT"],
            ["CorpusDocuments"] = ["TEXT", "TEXT", "INTEGER", "TEXT", "TEXT", "TEXT", "TEXT", "TEXT",
                "TEXT", "TEXT", "TEXT"],
            ["Assessments"] = ["TEXT", "TEXT", "TEXT", "TEXT", "TEXT", "TEXT", "TEXT", "TEXT", "TEXT",
                "TEXT", "INTEGER", "TEXT"],
            ["AssessedWords"] = ["INTEGER", "TEXT", "INTEGER", "TEXT", "TEXT"],
            ["ParsedAnalyses"] = ["INTEGER", "INTEGER", "TEXT", "TEXT", "INTEGER", "TEXT"],
            ["AssessmentPins"] = ["TEXT", "TEXT", "TEXT"]
        };

    private static readonly IReadOnlyDictionary<string, string[]> ExpectedPrimaryKeys =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Corpora"] = ["CorpusId"],
            ["CorpusDocuments"] = ["CorpusId", "DocumentId"],
            ["Assessments"] = ["AssessmentId"],
            ["AssessedWords"] = ["AssessedWordId"],
            ["ParsedAnalyses"] = [],
            ["AssessmentPins"] = ["AssessmentId", "PinnedBy"]
        };

    /// <summary>Copies known corpus and Assessment tables in one destination transaction.</summary>
    public static LegacyMigrationResult ImportInto(string legacyPath, MotifDatabase destination,
        Action<string>? afterBoundary = null, bool renameSourceAfterCommit = true,
        Action? beforeCommit = null, Action? beforeArchive = null)
    {
        if (string.IsNullOrWhiteSpace(legacyPath))
            throw new ArgumentException("A source path is required.", nameof(legacyPath));
        ArgumentNullException.ThrowIfNull(destination);
        var fullPath = Path.GetFullPath(legacyPath);
        if (PathsEqual(fullPath, destination.FullPath))
            throw new InvalidOperationException("A legacy bulk source cannot be the destination Motif database.");

        using var connection = destination.OpenConnection();
        if (!File.Exists(fullPath))
        {
            if (LedgerPathExists(connection, "legacy-bulk", fullPath))
                return new LegacyMigrationResult("", 0, false);
            throw new FileNotFoundException("Legacy Motif database was not found.", fullPath);
        }

        AttachReadOnly(connection, fullPath);
        var committed = false;
        var shouldArchive = false;
        string? committedDigest = null;
        try
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                var available = ReadTables(connection, transaction);
                ValidateSourceSchema(connection, transaction, available);
                VerifySourceForeignKeys(connection, transaction);
                var digest = LogicalDigest(connection, transaction);
                if (FileProposalStoreMigration.LedgerExists(connection, "legacy-bulk", fullPath, digest, transaction))
                {
                    shouldArchive = renameSourceAfterCommit;
                    committedDigest = digest;
                    transaction.Commit();
                    committed = true;
                    return new LegacyMigrationResult(digest, 0, renameSourceAfterCommit);
                }

                var count = 0;
                foreach (var map in Maps)
                    count += CopyTable(connection, transaction, map, afterBoundary);
                VerifyExactCounts(connection, transaction);
                FileProposalStoreMigration.AddLedger(connection, transaction, "legacy-bulk", fullPath, digest);
                afterBoundary?.Invoke("MigrationLedger");
                VerifyForeignKeys(connection, transaction);
                beforeCommit?.Invoke();
                EnsureSourceDigest(fullPath, digest);
                transaction.Commit();
                committed = true;
                shouldArchive = renameSourceAfterCommit;
                committedDigest = digest;
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

            if (committed && shouldArchive)
            {
                beforeArchive?.Invoke();
                EnsureSourceDigest(fullPath, committedDigest!);
                ArchiveSourceBundle(fullPath);
            }
        }
    }

    private static void AttachReadOnly(SqliteConnection connection, string path)
    {
        var uri = new Uri(path).AbsoluteUri.Replace("file:///", "file:", StringComparison.Ordinal) + "?mode=ro";
        using var command = connection.CreateCommand();
        command.CommandText = $"ATTACH DATABASE '{uri.Replace("'", "''", StringComparison.Ordinal)}' AS legacy;";
        command.ExecuteNonQuery();
    }

    private static HashSet<string> ReadTables(SqliteConnection connection, SqliteTransaction transaction,
        string schema = "legacy")
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT name FROM {schema}.sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        using var reader = command.ExecuteReader();
        var tables = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) tables.Add(reader.GetString(0));
        return tables;
    }

    private static string LogicalDigest(SqliteConnection connection, SqliteTransaction transaction) =>
        LogicalDigest(connection, transaction, "legacy");

    private static string LogicalDigest(SqliteConnection connection, SqliteTransaction transaction, string schema)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var map in Maps)
        {
            AppendString(hash, map.Name);
            foreach (var column in map.Columns) AppendString(hash, column);
            var columnList = string.Join(", ", map.Columns.Select(Quote));
            using var rowsCommand = connection.CreateCommand();
            rowsCommand.Transaction = transaction;
            var table = string.IsNullOrEmpty(schema) ? Quote(map.Name) : schema + "." + Quote(map.Name);
            rowsCommand.CommandText = $"SELECT {columnList} FROM {table} ORDER BY " +
                string.Join(", ", map.Keys.Select(Quote)) + ";";
            using var rows = rowsCommand.ExecuteReader();
            while (rows.Read())
            {
                AppendFrame(hash, 6, []);
                for (var index = 0; index < rows.FieldCount; index++) AppendValue(hash, rows.GetValue(index));
            }
        }
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static ColumnInfo[] ReadSourceColumns(SqliteConnection connection, SqliteTransaction transaction, string table,
        string schema = "legacy")
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA {schema}.table_info({Quote(table)});";
        using var reader = command.ExecuteReader();
        var columns = new List<ColumnInfo>();
        while (reader.Read())
            columns.Add(new ColumnInfo(reader.GetString(1), reader.GetString(2), reader.GetInt32(5)));
        return columns.ToArray();
    }

    private static void ValidateSourceSchema(SqliteConnection connection, SqliteTransaction transaction,
        HashSet<string> available, string schema = "legacy")
    {
        var expected = Maps.Select(map => map.Name).ToHashSet(StringComparer.Ordinal);
        if (!available.SetEquals(expected))
            throw new InvalidDataException("Legacy database does not match the recognized schema tables.");

        using (var objects = connection.CreateCommand())
        {
            objects.Transaction = transaction;
            objects.CommandText = $"SELECT type, name, tbl_name FROM {schema}.sqlite_master WHERE name NOT LIKE 'sqlite_%' " +
                "AND type NOT IN ('table');";
            using var reader = objects.ExecuteReader();
            while (reader.Read())
            {
                if (!StringComparer.Ordinal.Equals(reader.GetString(0), "index") ||
                    !KnownIndexes.TryGetValue(reader.GetString(1), out var index) ||
                    !StringComparer.Ordinal.Equals(reader.GetString(2), index.Table))
                    throw new InvalidDataException($"Legacy database contains unsupported {reader.GetString(0)} {reader.GetString(1)}.");
            }
        }

        ValidateKnownIndexes(connection, transaction, schema);

        foreach (var map in Maps)
        {
            var columns = ReadSourceColumns(connection, transaction, map.Name, schema);
            if (!columns.Select(column => column.Name).SequenceEqual(map.Columns, StringComparer.Ordinal))
                throw new InvalidDataException($"Legacy table {map.Name} does not match its recognized columns.");
            var types = ExpectedTypes[map.Name];
            if (columns.Length != types.Length || columns.Where((column, index) =>
                    !StringComparer.OrdinalIgnoreCase.Equals(column.Type, types[index])).Any())
                throw new InvalidDataException($"Legacy table {map.Name} does not match its recognized column types.");
            var expectedPrimaryKeys = ExpectedPrimaryKeys[map.Name].Select((key, rank) =>
                (Index: Array.IndexOf(map.Columns, key) + 1, Rank: rank + 1)).ToDictionary(item => item.Index,
                item => item.Rank);
            if (columns.Where((column, index) => column.PrimaryKey !=
                    (expectedPrimaryKeys.TryGetValue(index + 1, out var rank) ? rank : 0)).Any())
                throw new InvalidDataException($"Legacy table {map.Name} does not match its recognized primary key shape.");
        }
    }

    private static int CopyTable(SqliteConnection connection, SqliteTransaction transaction, TableMap map,
        Action<string>? afterBoundary)
    {
        var columnList = string.Join(", ", map.Columns.Select(Quote));
        long lastRowId = 0;
        var count = 0;
        while (true)
        {
            using var source = connection.CreateCommand();
            source.Transaction = transaction;
            source.CommandText = $"SELECT rowid, {columnList} FROM legacy.{Quote(map.Name)} " +
                "WHERE rowid > $rowid ORDER BY rowid LIMIT 1;";
            source.Parameters.AddWithValue("$rowid", lastRowId);
            using var reader = source.ExecuteReader();
            if (!reader.Read()) break;
            lastRowId = reader.GetInt64(0);
            var values = new object?[map.Columns.Length];
            for (var index = 0; index < values.Length; index++) values[index] = reader.GetValue(index + 1);
            var existing = FindExisting(connection, transaction, map, values);
            if (existing is not null)
            {
                for (var index = 0; index < values.Length; index++)
                    if (!ValueEquals(existing[index], values[index]))
                        throw new InvalidDataException($"Legacy row conflicts with destination row in {map.Name}.");
            }
            else Insert(connection, transaction, map, values);
            count++;
        }
        afterBoundary?.Invoke(map.Name);
        return count;
    }

    private static void ValidateKnownIndexes(SqliteConnection connection, SqliteTransaction transaction,
        string schema = "legacy")
    {
        foreach (var index in KnownIndexes)
        {
            using var list = connection.CreateCommand();
            list.Transaction = transaction;
            list.CommandText = $"PRAGMA {schema}.index_list({Quote(index.Value.Table)});";
            using var reader = list.ExecuteReader();
            var found = false;
            while (reader.Read())
                if (StringComparer.Ordinal.Equals(reader.GetString(1), index.Key)) found = true;
            if (!found) continue;

            using var columns = connection.CreateCommand();
            columns.Transaction = transaction;
            columns.CommandText = $"PRAGMA {schema}.index_info({Quote(index.Key)});";
            using var columnReader = columns.ExecuteReader();
            var actual = new List<string>();
            while (columnReader.Read()) actual.Add(columnReader.GetString(2));
            if (!actual.SequenceEqual(index.Value.Columns, StringComparer.Ordinal))
                throw new InvalidDataException($"Legacy index {index.Key} does not match its recognized shape.");
        }
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

    private static void VerifyExactCounts(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var map in Maps)
        {
            using var source = connection.CreateCommand();
            source.Transaction = transaction;
            source.CommandText = $"SELECT {string.Join(", ", map.Columns.Select(Quote))} FROM legacy.{Quote(map.Name)} " +
                $"ORDER BY {string.Join(", ", map.Keys.Select(Quote))};";
            var matched = 0L;
            using var reader = source.ExecuteReader();
            while (reader.Read())
            {
                var values = new object?[map.Columns.Length];
                reader.GetValues(values);
                var existing = FindExisting(connection, transaction, map, values);
                if (existing is null || values.Where((value, index) => !ValueEquals(value, existing[index])).Any())
                    throw new InvalidDataException($"Destination row verification failed for {map.Name}.");
                matched++;
            }
            using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = $"SELECT COUNT(*) FROM legacy.{Quote(map.Name)};";
            var sourceCount = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (matched != sourceCount)
                throw new InvalidDataException($"Destination row count verification failed for {map.Name}.");
        }
    }

    private static void AppendString(IncrementalHash hash, string value) =>
        AppendFrame(hash, 1, Encoding.UTF8.GetBytes(value));

    private static void AppendValue(IncrementalHash hash, object value)
    {
        switch (value)
        {
            case DBNull:
                AppendFrame(hash, 0, []);
                break;
            case string text:
                AppendFrame(hash, 1, Encoding.UTF8.GetBytes(text));
                break;
            case byte[] bytes:
                AppendFrame(hash, 4, bytes);
                break;
            case long number:
                Span<byte> integer = stackalloc byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(integer, number);
                AppendFrame(hash, 2, integer.ToArray());
                break;
            case int number:
                Span<byte> smallInteger = stackalloc byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(smallInteger, number);
                AppendFrame(hash, 2, smallInteger.ToArray());
                break;
            case double number:
                Span<byte> real = stackalloc byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(real, BitConverter.DoubleToInt64Bits(number));
                AppendFrame(hash, 3, real.ToArray());
                break;
            default:
                throw new InvalidDataException($"Legacy database returned unsupported SQLite value type {value.GetType().Name}.");
        }
    }

    private static void AppendFrame(IncrementalHash hash, byte type, byte[] bytes)
    {
        hash.AppendData([type]);
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void VerifySourceForeignKeys(SqliteConnection connection, SqliteTransaction transaction,
        string schema = "legacy")
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA {schema}.foreign_key_check;";
        using var reader = command.ExecuteReader();
        if (reader.Read()) throw new InvalidDataException("Legacy source contains a foreign-key violation.");
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

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison);
    }

    private static void EnsureSourceDigest(string path, string expectedDigest)
    {
        using var connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var available = ReadTables(connection, transaction, "main");
        ValidateSourceSchema(connection, transaction, available, "main");
        VerifySourceForeignKeys(connection, transaction, "main");
        var actual = LogicalDigest(connection, transaction, "");
        transaction.Commit();
        if (!string.Equals(actual, expectedDigest, StringComparison.Ordinal))
            throw new InvalidDataException("The legacy source changed before archival.");
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static void ArchiveSourceBundle(string path)
    {
        var baseArchive = path + ".migrated";
        var archive = baseArchive;
        var suffix = 1;
        while (File.Exists(archive) || Directory.Exists(archive) || File.Exists(archive + "-wal") ||
               File.Exists(archive + "-shm") || Directory.Exists(archive + "-wal") || Directory.Exists(archive + "-shm"))
            archive = baseArchive + "-" + suffix++;

        var moved = new List<(string Source, string Target)>();
        try
        {
            foreach (var suffixName in new[] { "", "-wal", "-shm" })
            {
                var source = path + suffixName;
                if (!File.Exists(source)) continue;
                var target = archive + suffixName;
                File.Move(source, target);
                moved.Add((source, target));
            }
        }
        catch
        {
            foreach (var pair in moved.AsEnumerable().Reverse())
                if (File.Exists(pair.Target) && !File.Exists(pair.Source)) File.Move(pair.Target, pair.Source);
            throw;
        }
    }

    private sealed record TableMap(string Name, string[] Columns, string[] Keys);
    private sealed record ColumnInfo(string Name, string Type, int PrimaryKey);
}

/// <summary>Summarizes a legacy bulk migration.</summary>
public sealed record LegacyMigrationResult(string SourceDigest, int RowCount, bool SourceRenamed);
