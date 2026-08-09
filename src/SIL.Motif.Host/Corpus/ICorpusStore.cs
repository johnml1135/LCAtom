using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SIL.Motif.Host.Corpus;

/// <summary>Where Corpora are kept.</summary>
/// <remarks>
/// An interface because ADR 0036 decision 6 puts Corpora in an embedded database and that database does not
/// exist yet. Ingestion is useful before the storage question is settled, and it should not have to be
/// rewritten when it is — so ingestion depends on this, and <see cref="FileCorpusStore"/> is what satisfies it
/// today.
/// </remarks>
public interface ICorpusStore
{
    /// <summary>Whether a corpus with this id is already stored.</summary>
    bool Exists(string corpusId);

    /// <summary>Load a corpus with its documents, or <c>null</c> if there is none.</summary>
    StoredCorpus? Load(string corpusId);

    /// <summary>Write a corpus, replacing what is there.</summary>
    void Save(StoredCorpus corpus);

    /// <summary>Every stored corpus id, in a stable order.</summary>
    IReadOnlyList<string> List();
}

/// <summary>
/// Stores Corpora as a metadata file plus one text file per Document.
/// </summary>
/// <remarks>
/// <para>
/// <b>The text stays out of the JSON</b>, which is the only decision here worth defending. A Document is
/// routinely megabytes; putting it in the metadata file means every read of "what is in this corpus" parses
/// all of it, and means a person can no longer open the metadata to see what they have. So metadata is small
/// and readable, and the text sits beside it as plain UTF-8 that any other tool can also read.
/// </para>
/// <para>
/// <b>This is the interim implementation.</b> ADR 0036 decision 6 puts Corpora in an embedded database,
/// because "the hundred most frequent unparsed forms" over a hundred megabytes is a scan here and an index
/// there. What this proves is that ingestion does not care which it is.
/// </para>
/// </remarks>
public sealed class FileCorpusStore : ICorpusStore
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public FileCorpusStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Corpus store root must not be empty.", nameof(rootDirectory));

        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    /// <summary>The <c>corpora/</c> directory holding every corpus.</summary>
    public string RootDirectory { get; }

    private string DirectoryFor(string corpusId) => Path.Combine(RootDirectory, SafeName(corpusId));
    private string MetadataPathFor(string corpusId) => Path.Combine(DirectoryFor(corpusId), "corpus.json");
    private string TextPathFor(string corpusId, string documentId) =>
        Path.Combine(DirectoryFor(corpusId), "documents", SafeName(documentId) + ".txt");

    public bool Exists(string corpusId) => File.Exists(MetadataPathFor(corpusId));

    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(RootDirectory)) return Array.Empty<string>();

        return Directory.EnumerateDirectories(RootDirectory)
            .Where(d => File.Exists(Path.Combine(d, "corpus.json")))
            .Select(d => ReadCorpusId(Path.Combine(d, "corpus.json")))
            .Where(id => id is not null)
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    public void Save(StoredCorpus corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);

        Directory.CreateDirectory(Path.Combine(DirectoryFor(corpus.CorpusId), "documents"));

        foreach (var document in corpus.Documents)
            File.WriteAllText(TextPathFor(corpus.CorpusId, document.DocumentId), document.Text, new UTF8Encoding(false));

        File.WriteAllText(MetadataPathFor(corpus.CorpusId), Serialise(corpus), new UTF8Encoding(false));
    }

    public StoredCorpus? Load(string corpusId)
    {
        var path = MetadataPathFor(corpusId);
        if (!File.Exists(path)) return null;

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;

        var origin = root.GetProperty("origin");
        var tokenisation = root.GetProperty("tokenisation");

        var provenance = new CorpusProvenance(
            new CorpusOrigin(
                origin.GetProperty("description").GetString()!,
                Str(origin, "uri"),
                DateTimeOffset.Parse(origin.GetProperty("retrievedUtc").GetString()!, CultureInfo.InvariantCulture),
                Str(origin, "licence"),
                Capabilities(origin)),
            new TokenisationRecord(
                tokenisation.GetProperty("method").GetString()!,
                tokenisation.GetProperty("version").GetString()!,
                Str(tokenisation, "notes") ?? ""),
            Qualification(root));

        var storedId = root.GetProperty("corpusId").GetString()!;
        var documents = new List<CorpusDocument>();

        if (root.TryGetProperty("documents", out var documentsElement))
        {
            foreach (var d in documentsElement.EnumerateArray())
            {
                var documentId = d.GetProperty("documentId").GetString()!;
                var textPath = TextPathFor(storedId, documentId);

                documents.Add(new CorpusDocument(
                    DocumentId: documentId,
                    Title: d.GetProperty("title").GetString()!,
                    Source: DocumentSource.Parse(d.GetProperty("source").GetString()!),
                    Text: File.Exists(textPath) ? File.ReadAllText(textPath) : "",
                    ContentSha256: d.GetProperty("contentSha256").GetString()!,
                    IngestedUtc: DateTimeOffset.Parse(d.GetProperty("ingestedUtc").GetString()!, CultureInfo.InvariantCulture),
                    Licence: Str(d, "licence"),
                    Capabilities: Capabilities(d),
                    Attributes: Attributes(d)));
            }
        }

        return new StoredCorpus(storedId, provenance, documents);
    }

    private static string Serialise(StoredCorpus corpus)
    {
        var root = new JsonObject
        {
            ["corpusId"] = corpus.CorpusId,
            ["origin"] = new JsonObject
            {
                ["description"] = corpus.Provenance.Origin.Description,
                ["uri"] = corpus.Provenance.Origin.Uri,
                ["retrievedUtc"] = corpus.Provenance.Origin.RetrievedUtc.ToString("O", CultureInfo.InvariantCulture),
                ["licence"] = corpus.Provenance.Origin.Licence,
                ["capabilities"] = CapabilitiesJson(corpus.Provenance.Origin.Capabilities),
            },
            ["tokenisation"] = new JsonObject
            {
                ["method"] = corpus.Provenance.Tokenisation.Method,
                ["version"] = corpus.Provenance.Tokenisation.Version,
                ["notes"] = corpus.Provenance.Tokenisation.Notes,
            },
            ["qualification"] = corpus.Provenance.Qualification is { } q
                ? new JsonObject
                {
                    ["knownClean"] = q.KnownClean,
                    ["inScope"] = q.InScope,
                    ["attestor"] = q.Attestor,
                    ["attestedUtc"] = q.AttestedUtc.ToString("O", CultureInfo.InvariantCulture),
                    ["note"] = q.Note,
                }
                : null,
            ["documents"] = new JsonArray(corpus.Documents.Select(d => (JsonNode?)new JsonObject
            {
                ["documentId"] = d.DocumentId,
                ["title"] = d.Title,
                ["source"] = d.Source.Describe(),
                ["contentSha256"] = d.ContentSha256,
                ["characterCount"] = d.Text.Length,
                ["ingestedUtc"] = d.IngestedUtc.ToString("O", CultureInfo.InvariantCulture),
                ["licence"] = d.Licence,
                ["capabilities"] = CapabilitiesJson(d.Capabilities),
                ["attributes"] = d.Attributes is null
                    ? null
                    : new JsonObject(d.Attributes.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
            }).ToArray()),
        };

        return root.ToJsonString(Pretty);
    }

    private static JsonNode? CapabilitiesJson(LicenceCapabilities? capabilities) =>
        capabilities is null
            ? null
            : new JsonObject
            {
                ["mayRedistribute"] = capabilities.MayRedistribute,
                ["mayDerive"] = capabilities.MayDerive,
                ["mayUseCommercially"] = capabilities.MayUseCommercially,
                ["requiresAttribution"] = capabilities.RequiresAttribution,
                ["basis"] = capabilities.Basis,
            };

    private static LicenceCapabilities? Capabilities(JsonElement parent)
    {
        if (!parent.TryGetProperty("capabilities", out var c) || c.ValueKind != JsonValueKind.Object)
            return null;

        return new LicenceCapabilities(
            Bool(c, "mayRedistribute"),
            Bool(c, "mayDerive"),
            Bool(c, "mayUseCommercially"),
            Bool(c, "requiresAttribution") ?? true,
            Str(c, "basis") ?? "not established");
    }

    private static CorpusQualification? Qualification(JsonElement root)
    {
        if (!root.TryGetProperty("qualification", out var q) || q.ValueKind != JsonValueKind.Object)
            return null;

        return new CorpusQualification(
            Bool(q, "knownClean") ?? false,
            Bool(q, "inScope") ?? false,
            Str(q, "attestor") ?? "",
            DateTimeOffset.Parse(q.GetProperty("attestedUtc").GetString()!, CultureInfo.InvariantCulture),
            Str(q, "note") ?? "");
    }

    private static IReadOnlyDictionary<string, string>? Attributes(JsonElement parent)
    {
        if (!parent.TryGetProperty("attributes", out var a) || a.ValueKind != JsonValueKind.Object)
            return null;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in a.EnumerateObject())
            map[property.Name] = property.Value.GetString() ?? property.Value.ToString();
        return map;
    }

    private static string? ReadCorpusId(string metadataPath)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(metadataPath));
            return json.RootElement.GetProperty("corpusId").GetString();
        }
        catch (JsonException)
        {
            return null;   // a corrupt corpus should not stop the rest being listed
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    /// <summary>Corpus and document ids come from outside; they must not be able to escape the store.</summary>
    private static string SafeName(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id must not be empty.", nameof(id));

        var safe = new StringBuilder(id.Length);
        foreach (var c in id)
            safe.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');

        var result = safe.ToString().Trim('.');
        if (result.Length == 0)
            throw new ArgumentException($"Id '{id}' contains no usable characters.", nameof(id));

        return result;
    }
}
