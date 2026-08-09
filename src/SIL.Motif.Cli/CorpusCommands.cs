using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SIL.Motif.Host.Corpus;

namespace SIL.Motif.Cli;

/// <summary>
/// The CLI verbs that get text into Motif: <c>add-corpus</c>, <c>add-document</c>,
/// <c>add-corpus-bundle</c>, and the two read-only verbs for seeing what is there.
/// </summary>
/// <remarks>
/// <para>
/// <b>In plain terms:</b> a linguist — or more often a script — says "here is a body of text, here is where it
/// came from, and here is what its licence allows", and Motif takes it in and hashes it. Nothing measures a
/// grammar's reach until text has arrived this way, because a number computed over text of unknown origin
/// cannot be published from or reproduced.
/// </para>
/// <para>
/// The bundle verb is the one a program uses; the other two are what a person uses for a single file.
/// </para>
/// </remarks>
public static class CorpusCommands
{
    /// <summary>Corpora live beside the proposals, under the same store root.</summary>
    public static FileCorpusStore StoreFor(string storeDir) =>
        new(Path.Combine(storeDir, "corpora"));

    /// <summary>Create an empty Corpus with its origin and tokenisation recorded.</summary>
    public static CommandResult AddCorpus(
        string storeDir,
        string corpusId,
        string description,
        string? uri,
        string? licence,
        LicenceCapabilities capabilities,
        string tokeniser,
        string tokeniserVersion,
        string? tokeniserNotes,
        DateTimeOffset? retrievedUtc = null)
    {
        try
        {
            var ingestion = new CorpusIngestion(StoreFor(storeDir));

            var provenance = new CorpusProvenance(
                new CorpusOrigin(description, uri, retrievedUtc ?? DateTimeOffset.UtcNow, licence, capabilities),
                new TokenisationRecord(tokeniser, tokeniserVersion, tokeniserNotes ?? ""));

            ingestion.AddCorpus(corpusId, provenance);

            var sb = new StringBuilder();
            sb.AppendLine($"Created corpus '{corpusId}'.");
            sb.AppendLine($"  Origin:       {description}");
            if (!string.IsNullOrWhiteSpace(uri)) sb.AppendLine($"  Location:     {uri}");
            sb.AppendLine($"  Licence:      {licence ?? "(none recorded)"}");
            sb.AppendLine($"  Tokenisation: {tokeniser} {tokeniserVersion}");
            sb.AppendLine();
            AppendDerivationNote(sb, capabilities, description);
            sb.AppendLine("Add documents with: motif add-document --corpus " + corpusId + " --doc <id> --source <file-or-url>");
            return new CommandResult(0, sb.ToString());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new CommandResult(1, ex.Message + Environment.NewLine);
        }
    }

    /// <summary>Add one Document to a Corpus, from a file on disk or a URL to retrieve.</summary>
    public static CommandResult AddDocument(
        string storeDir,
        string corpusId,
        string documentId,
        string fileOrUrl,
        string? title,
        string? licence,
        LicenceCapabilities? capabilities)
    {
        try
        {
            var ingestion = new CorpusIngestion(StoreFor(storeDir));
            var source = DocumentSource.Parse(fileOrUrl);

            var document = ingestion.AddDocumentAsync(
                corpusId,
                source,
                new DocumentMetadata(documentId, title ?? documentId, licence, capabilities)).GetAwaiter().GetResult();

            var sb = new StringBuilder();
            sb.AppendLine($"Added document '{document.DocumentId}' to corpus '{corpusId}'.");
            sb.AppendLine($"  Title:      {document.Title}");
            sb.AppendLine($"  Source:     {source.Describe()}");
            sb.AppendLine($"  Characters: {document.Text.Length:N0}");
            sb.AppendLine($"  SHA-256:    {document.ContentSha256}");
            if (!string.IsNullOrWhiteSpace(document.Licence)) sb.AppendLine($"  Licence:    {document.Licence}");
            return new CommandResult(0, sb.ToString());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            return new CommandResult(1, ex.Message + Environment.NewLine);
        }
    }

    /// <summary>Take in a whole handoff bundle written by a fetching tool.</summary>
    public static CommandResult AddBundle(string storeDir, string bundlePath)
    {
        try
        {
            var bundle = CorpusBundle.ReadFile(bundlePath);
            var ingestion = new CorpusIngestion(StoreFor(storeDir));
            var corpus = ingestion.AddBundleAsync(bundle).GetAwaiter().GetResult();

            var sb = new StringBuilder();
            sb.AppendLine($"Ingested corpus '{corpus.CorpusId}' from bundle: {corpus.Documents.Count} document(s).");
            sb.AppendLine($"  Origin:  {corpus.Provenance.Origin.Description}");
            sb.AppendLine($"  Licence: {corpus.Provenance.Origin.Licence ?? "(none recorded)"}");
            sb.AppendLine();
            sb.AppendLine(corpus.DescribeDerivationRestrictions());
            sb.AppendLine();
            sb.AppendLine(corpus.Provenance.SupportsAccuracyClaims
                ? "This corpus is attested, so accuracy figures may be computed over it."
                : corpus.Provenance.WhyAccuracyIsNotComputable());
            return new CommandResult(0, sb.ToString());
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException
                                      or ArgumentException or IOException)
        {
            return new CommandResult(1, ex.Message + Environment.NewLine);
        }
    }

    /// <summary>Every stored Corpus, with its size and what may be done with it.</summary>
    public static CommandResult ListCorpora(string storeDir)
    {
        var store = StoreFor(storeDir);
        var ids = store.List();

        var sb = new StringBuilder();
        if (ids.Count == 0)
        {
            sb.AppendLine("No corpora in store.");
            return new CommandResult(0, sb.ToString());
        }

        foreach (var id in ids)
        {
            var corpus = store.Load(id);
            if (corpus is null) continue;

            var derivable = corpus.DocumentsPermittingDerivation().Count;
            sb.AppendLine($"{id}");
            sb.AppendLine($"  {corpus.Provenance.Origin.Description}");
            sb.AppendLine($"  {corpus.Documents.Count} document(s); {derivable} permit derived works");
            sb.AppendLine($"  accuracy figures: {(corpus.Provenance.SupportsAccuracyClaims ? "permitted" : "not computable — no attestation")}");
        }

        return new CommandResult(0, sb.ToString());
    }

    /// <summary>One Corpus in full: provenance, every Document, and what each is licensed for.</summary>
    public static CommandResult ShowCorpus(string storeDir, string corpusId)
    {
        var corpus = StoreFor(storeDir).Load(corpusId);
        if (corpus is null)
            return new CommandResult(1, $"No corpus '{corpusId}' in store." + Environment.NewLine);

        var origin = corpus.Provenance.Origin;
        var sb = new StringBuilder();
        sb.AppendLine($"Corpus:       {corpus.CorpusId}");
        sb.AppendLine($"Origin:       {origin.Description}");
        if (!string.IsNullOrWhiteSpace(origin.Uri)) sb.AppendLine($"Location:     {origin.Uri}");
        sb.AppendLine($"Retrieved:    {origin.RetrievedUtc:u}");
        sb.AppendLine($"Licence:      {origin.Licence ?? "(none recorded)"}");
        sb.AppendLine($"Tokenisation: {corpus.Provenance.Tokenisation.Method} {corpus.Provenance.Tokenisation.Version}");
        if (!string.IsNullOrWhiteSpace(corpus.Provenance.Tokenisation.Notes))
            sb.AppendLine($"              {corpus.Provenance.Tokenisation.Notes}");

        sb.AppendLine();
        sb.AppendLine(corpus.Provenance.SupportsAccuracyClaims
            ? $"Attested by {corpus.Provenance.Qualification!.Attestor}: accuracy figures may be computed."
            : corpus.Provenance.WhyAccuracyIsNotComputable());

        sb.AppendLine();
        sb.AppendLine($"Documents ({corpus.Documents.Count}):");
        foreach (var d in corpus.Documents)
        {
            var caps = d.EffectiveCapabilities(origin);
            sb.AppendLine($"  {d.DocumentId}  {d.Title}");
            // "..." rather than an ellipsis character: Windows consoles at the default code page render
            // U+2026 as a full stop, which reads as a truncated hash that ends in a dot.
            sb.AppendLine($"    {d.Text.Length:N0} characters, sha256 {d.ContentSha256[..12]}...");
            sb.AppendLine($"    licence: {d.EffectiveLicence(origin) ?? "(none recorded)"}; " +
                          $"derived works: {(caps.PermitsDerivedArtefacts ? "permitted" : "not permitted")}");
        }

        sb.AppendLine();
        sb.AppendLine(corpus.DescribeDerivationRestrictions());
        return new CommandResult(0, sb.ToString());
    }

    /// <summary>
    /// Build capabilities from CLI flags, defaulting to "nothing established" rather than to permission.
    /// </summary>
    public static LicenceCapabilities CapabilitiesFromFlags(IReadOnlyDictionary<string, string> flags)
    {
        var basis = flags.GetValueOrDefault("licence-basis");

        var any = flags.ContainsKey("may-derive")
                  || flags.ContainsKey("may-redistribute")
                  || flags.ContainsKey("may-use-commercially")
                  || flags.ContainsKey("requires-attribution")
                  || basis is not null;

        if (!any) return LicenceCapabilities.Unknown();

        return new LicenceCapabilities(
            MayRedistribute: Tri(flags, "may-redistribute"),
            MayDerive: Tri(flags, "may-derive"),
            MayUseCommercially: Tri(flags, "may-use-commercially"),
            RequiresAttribution: Tri(flags, "requires-attribution") ?? true,
            Basis: basis ?? "stated on the command line, source unrecorded");

        static bool? Tri(IReadOnlyDictionary<string, string> f, string name) =>
            f.TryGetValue(name, out var v) && bool.TryParse(v, out var parsed) ? parsed : null;
    }

    private static void AppendDerivationNote(StringBuilder sb, LicenceCapabilities capabilities, string description)
    {
        if (capabilities.PermitsDerivedArtefacts)
        {
            sb.AppendLine($"Derived works (n-gram models, published word lists) are permitted. Basis: {capabilities.Basis}");
        }
        else
        {
            sb.AppendLine(capabilities.WhyDerivedArtefactsAreNotPermitted(description));
        }

        sb.AppendLine();
    }
}
