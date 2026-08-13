using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SIL.Motif.Host.Store;

namespace SIL.Motif.Host.Corpus;

/// <summary>
/// Stores Corpora in the embedded database ADR 0036 decision 6 assigns them to.
/// </summary>
/// <remarks>
/// <para>
/// <b>What moved here from <see cref="FileCorpusStore"/> and why.</b> A corpus is bulk (tens to hundreds of
/// megabytes once several documents accumulate) and is queried in aggregate — "how many documents", "which
/// corpora exist" — where a filesystem gives no aggregate query and no pruning. <see cref="List"/> and
/// <see cref="Exists"/> read only the <c>Corpora</c> table, which carries no document text at all, so listing
/// what is in the store never touches a byte of any document.
/// </para>
/// <para>
/// <b><see cref="Load"/> still returns everything</b>, text included, because that is what the interface
/// promises callers today and what ingestion needs when it appends a document (<c>CorpusIngestion</c> loads,
/// appends, and saves the whole corpus back). The property this type adds over the file store is that the
/// operations that do not need the text — listing, existence — no longer pay for it either.
/// </para>
/// <para>
/// <b>Migration.</b> Nothing here reads a <c>corpus.json</c> file; a store already on disk stays exactly as
/// readable as it always was through <see cref="FileCorpusStore"/>, and <see cref="CorpusStoreMigration"/> is
/// the supported one-time path from one into the other.
/// </para>
/// </remarks>
public sealed class SqliteCorpusStore : ICorpusStore
{
    private readonly string _databasePath;

    public SqliteCorpusStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A database path is required.", nameof(databasePath));

        _databasePath = Path.GetFullPath(databasePath);
    }

    public bool Exists(string corpusId)
    {
        using var connection = SqliteMotifDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM Corpora WHERE CorpusId = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", corpusId);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>Every corpus id, read from a table with no document text in it — never a scan of the bulk data.</summary>
    public IReadOnlyList<string> List()
    {
        using var connection = SqliteMotifDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CorpusId FROM Corpora ORDER BY CorpusId;";

        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    public void Save(StoredCorpus corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);

        using var connection = SqliteMotifDatabase.OpenConnection(_databasePath);
        using var transaction = connection.BeginTransaction();

        using (var corpusCommand = connection.CreateCommand())
        {
            corpusCommand.Transaction = transaction;
            corpusCommand.CommandText = """
                INSERT INTO Corpora (CorpusId, ProvenanceJson) VALUES ($id, $provenance)
                ON CONFLICT(CorpusId) DO UPDATE SET ProvenanceJson = excluded.ProvenanceJson;
                """;
            corpusCommand.Parameters.AddWithValue("$id", corpus.CorpusId);
            corpusCommand.Parameters.AddWithValue("$provenance", JsonSerializer.Serialize(corpus.Provenance));
            corpusCommand.ExecuteNonQuery();
        }

        for (var i = 0; i < corpus.Documents.Count; i++)
        {
            var document = corpus.Documents[i];
            using var documentCommand = connection.CreateCommand();
            documentCommand.Transaction = transaction;
            documentCommand.CommandText = """
                INSERT INTO CorpusDocuments
                    (CorpusId, DocumentId, OrdinalIndex, Title, Source, Text, ContentSha256, IngestedUtc,
                     Licence, CapabilitiesJson, AttributesJson)
                VALUES
                    ($corpusId, $documentId, $ordinal, $title, $source, $text, $sha256, $ingestedUtc,
                     $licence, $capabilities, $attributes)
                ON CONFLICT(CorpusId, DocumentId) DO UPDATE SET
                    OrdinalIndex = excluded.OrdinalIndex, Title = excluded.Title, Source = excluded.Source,
                    Text = excluded.Text, ContentSha256 = excluded.ContentSha256,
                    IngestedUtc = excluded.IngestedUtc, Licence = excluded.Licence,
                    CapabilitiesJson = excluded.CapabilitiesJson, AttributesJson = excluded.AttributesJson;
                """;
            documentCommand.Parameters.AddWithValue("$corpusId", corpus.CorpusId);
            documentCommand.Parameters.AddWithValue("$documentId", document.DocumentId);
            documentCommand.Parameters.AddWithValue("$ordinal", i);
            documentCommand.Parameters.AddWithValue("$title", document.Title);
            documentCommand.Parameters.AddWithValue("$source", document.Source.Describe());
            documentCommand.Parameters.AddWithValue("$text", document.Text);
            documentCommand.Parameters.AddWithValue("$sha256", document.ContentSha256);
            documentCommand.Parameters.AddWithValue("$ingestedUtc", document.IngestedUtc.ToString("O", CultureInfo.InvariantCulture));
            documentCommand.Parameters.AddWithValue("$licence", (object?)document.Licence ?? DBNull.Value);
            documentCommand.Parameters.AddWithValue(
                "$capabilities", document.Capabilities is null ? DBNull.Value : JsonSerializer.Serialize(document.Capabilities));
            documentCommand.Parameters.AddWithValue(
                "$attributes", document.Attributes is null ? DBNull.Value : JsonSerializer.Serialize(document.Attributes));
            documentCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public StoredCorpus? Load(string corpusId)
    {
        using var connection = SqliteMotifDatabase.OpenConnection(_databasePath);

        string? provenanceJson;
        using (var corpusCommand = connection.CreateCommand())
        {
            corpusCommand.CommandText = "SELECT ProvenanceJson FROM Corpora WHERE CorpusId = $id;";
            corpusCommand.Parameters.AddWithValue("$id", corpusId);
            var result = corpusCommand.ExecuteScalar();
            if (result is null) return null;
            provenanceJson = (string)result;
        }

        var provenance = JsonSerializer.Deserialize<CorpusProvenance>(provenanceJson)!;
        var documents = new List<CorpusDocument>();

        using (var documentsCommand = connection.CreateCommand())
        {
            documentsCommand.CommandText = """
                SELECT DocumentId, Title, Source, Text, ContentSha256, IngestedUtc, Licence, CapabilitiesJson, AttributesJson
                FROM CorpusDocuments WHERE CorpusId = $id ORDER BY OrdinalIndex;
                """;
            documentsCommand.Parameters.AddWithValue("$id", corpusId);
            using var reader = documentsCommand.ExecuteReader();
            while (reader.Read())
            {
                documents.Add(new CorpusDocument(
                    DocumentId: reader.GetString(0),
                    Title: reader.GetString(1),
                    Source: DocumentSource.Parse(reader.GetString(2)),
                    Text: reader.GetString(3),
                    ContentSha256: reader.GetString(4),
                    IngestedUtc: DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                    Licence: reader.IsDBNull(6) ? null : reader.GetString(6),
                    Capabilities: reader.IsDBNull(7) ? null : JsonSerializer.Deserialize<LicenceCapabilities>(reader.GetString(7)),
                    Attributes: reader.IsDBNull(8)
                        ? null
                        : JsonSerializer.Deserialize<IReadOnlyDictionary<string, string>>(reader.GetString(8))));
            }
        }

        return new StoredCorpus(corpusId, provenance, documents);
    }
}
