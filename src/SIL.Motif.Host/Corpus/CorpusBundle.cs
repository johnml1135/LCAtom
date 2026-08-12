using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SIL.Motif.Host.Corpus;

/// <summary>One document a bundle offers: where to get it, and what is known about it.</summary>
public sealed record BundleEntry(DocumentSource Source, DocumentMetadata Metadata);

/// <summary>
/// The handoff format: what an external fetching tool writes, and Motif reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>In plain terms:</b> linguistic-assistant knows how to pull eBible and OPUS, how to strip their markup,
/// and where each translation's licence is recorded. Motif knows none of that and should not learn it. A
/// bundle is the paperwork that travels with the text: here is what I fetched, here is where it came from,
/// here is what each piece of it is licensed for.
/// </para>
/// <para>
/// <b>It is a description, not an archive.</b> A bundle names files; it does not contain them. So a fetching
/// tool writes one small JSON file next to the hundreds of megabytes it already produced, rather than
/// repackaging them — and a bundle whose files have gone missing fails loudly at ingestion instead of
/// silently importing less than it claimed.
/// </para>
/// <para>
/// <b>Relative paths resolve against the bundle file's own directory</b>, so the whole folder can be moved or
/// copied between machines without editing it.
/// </para>
/// </remarks>
/// <param name="CorpusId">The corpus this creates.</param>
/// <param name="Provenance">Its origin, tokenisation and any qualification.</param>
/// <param name="Documents">The documents to add, in order.</param>
public sealed record CorpusBundle(
    string CorpusId,
    CorpusProvenance Provenance,
    IReadOnlyList<BundleEntry> Documents)
{
    /// <summary>Read a bundle from a JSON file, resolving relative document paths against its directory.</summary>
    /// <exception cref="FileNotFoundException">No bundle at that path.</exception>
    /// <exception cref="InvalidDataException">The bundle is missing something required.</exception>
    public static CorpusBundle ReadFile(string bundlePath)
    {
        var full = Path.GetFullPath(bundlePath);
        if (!File.Exists(full))
            throw new FileNotFoundException($"No corpus bundle at '{full}'.", full);

        var baseDirectory = Path.GetDirectoryName(full)!;
        using var document = JsonDocument.Parse(File.ReadAllText(full));
        return Read(document.RootElement, baseDirectory);
    }

    /// <summary>Read a bundle from parsed JSON. <paramref name="baseDirectory"/> resolves relative paths.</summary>
    public static CorpusBundle Read(JsonElement root, string baseDirectory)
    {
        var corpusId = RequiredString(root, "corpusId");

        if (!root.TryGetProperty("origin", out var originElement))
            throw new InvalidDataException("A corpus bundle must have an 'origin'. Text without a recorded source cannot be published from safely.");
        if (!root.TryGetProperty("tokenisation", out var tokenisationElement))
            throw new InvalidDataException("A corpus bundle must have a 'tokenisation'. Two corpora tokenised differently are not comparable, so a figure without it is not reproducible.");

        var origin = new CorpusOrigin(
            Description: RequiredString(originElement, "description"),
            Uri: OptionalString(originElement, "uri"),
            RetrievedUtc: RequiredDate(originElement, "retrievedUtc"),
            Licence: OptionalString(originElement, "licence"),
            Capabilities: ReadCapabilities(originElement));

        var tokenisation = new TokenisationRecord(
            Method: RequiredString(tokenisationElement, "method"),
            Version: RequiredString(tokenisationElement, "version"),
            Notes: OptionalString(tokenisationElement, "notes") ?? "");

        CorpusQualification? qualification = null;
        if (root.TryGetProperty("qualification", out var q) && q.ValueKind == JsonValueKind.Object)
        {
            qualification = new CorpusQualification(
                KnownClean: RequiredBool(q, "knownClean"),
                InScope: RequiredBool(q, "inScope"),
                Attestor: RequiredString(q, "attestor"),
                AttestedUtc: RequiredDate(q, "attestedUtc"),
                Note: OptionalString(q, "note") ?? "");
        }

        var entries = new List<BundleEntry>();
        if (root.TryGetProperty("documents", out var documents) && documents.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in documents.EnumerateArray())
            {
                var raw = RequiredString(d, "source");
                var source = DocumentSource.Parse(raw);

                // Resolved against the bundle's own directory, not the caller's cwd, so a bundle stays portable.
                if (source is DocumentSource.File file && !Path.IsPathRooted(file.Path))
                    source = new DocumentSource.File(Path.GetFullPath(Path.Combine(baseDirectory, file.Path)));

                entries.Add(new BundleEntry(
                    source,
                    new DocumentMetadata(
                        DocumentId: RequiredString(d, "documentId"),
                        Title: OptionalString(d, "title") ?? RequiredString(d, "documentId"),
                        Licence: OptionalString(d, "licence"),
                        Capabilities: ReadCapabilities(d),
                        Attributes: ReadAttributes(d))));
            }
        }

        if (entries.Count == 0)
            throw new InvalidDataException($"Corpus bundle '{corpusId}' lists no documents.");

        var duplicate = entries.GroupBy(e => e.Metadata.DocumentId, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Corpus bundle '{corpusId}' lists document id '{duplicate.Key}' more than once.");

        return new CorpusBundle(corpusId, new CorpusProvenance(origin, tokenisation, qualification), entries);
    }

    private static LicenceCapabilities? ReadCapabilities(JsonElement parent)
    {
        if (!parent.TryGetProperty("capabilities", out var c) || c.ValueKind != JsonValueKind.Object)
            return null;

        return new LicenceCapabilities(
            MayRedistribute: OptionalBool(c, "mayRedistribute"),
            MayDerive: OptionalBool(c, "mayDerive"),
            MayUseCommercially: OptionalBool(c, "mayUseCommercially"),
            RequiresAttribution: OptionalBool(c, "requiresAttribution") ?? true,
            Basis: RequiredString(c, "basis"));
    }

    private static IReadOnlyDictionary<string, string>? ReadAttributes(JsonElement parent)
    {
        if (!parent.TryGetProperty("attributes", out var a) || a.ValueKind != JsonValueKind.Object)
            return null;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in a.EnumerateObject())
            map[property.Name] = property.Value.ToString();
        return map;
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!
            : throw new InvalidDataException($"Corpus bundle is missing required string '{name}'.");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool RequiredBool(JsonElement element, string name) =>
        OptionalBool(element, name) ?? throw new InvalidDataException($"Corpus bundle is missing required boolean '{name}'.");

    private static bool? OptionalBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static DateTimeOffset RequiredDate(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), out var parsed)
            ? parsed
            : throw new InvalidDataException($"Corpus bundle is missing or cannot parse date '{name}'.");
}
