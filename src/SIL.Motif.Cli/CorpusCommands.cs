using SIL.Motif.Contract.Responses;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Projection;
using SIL.Motif.Projection.Rendering;
using SIL.Motif.Projection.Usage;
using SIL.Motif.Worker.Store;

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
    /// <summary>The Corpus store for one project — the paired database <c>--project</c> resolves to.</summary>
    public static ICorpusStore StoreFor(ProjectLocator project) =>
        new SqliteCorpusStore(ProjectDatabaseCatalog.DatabasePathFor(project));

    /// <summary>Create an empty Corpus with its origin and tokenisation recorded.</summary>
    public static CommandResult AddCorpus(
        string fwDataPath,
        string productVersion,
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
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (_, project) =>
        {
            try
            {
                var ingestion = new CorpusIngestion(StoreFor(project));

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
        });
    }

    /// <summary>Add one Document to a Corpus, from a file on disk or a URL to retrieve.</summary>
    public static CommandResult AddDocument(
        string fwDataPath,
        string productVersion,
        string corpusId,
        string documentId,
        string fileOrUrl,
        string? title,
        string? licence,
        LicenceCapabilities? capabilities)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (_, project) =>
        {
            try
            {
                var ingestion = new CorpusIngestion(StoreFor(project));
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
        });
    }

    /// <summary>Take in a whole handoff bundle written by a fetching tool.</summary>
    public static CommandResult AddBundle(string fwDataPath, string productVersion, string bundlePath)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (_, project) =>
        {
            try
            {
                var bundle = CorpusBundle.ReadFile(bundlePath);
                var ingestion = new CorpusIngestion(StoreFor(project));
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
        });
    }

    /// <summary>Every stored Corpus, with its size and what may be done with it.</summary>
    public static CommandResult ListCorpora(string fwDataPath, string productVersion, UsageLog? usage = null)
    {
        usage?.Record("corpora", new[] { UsageArgumentShape.Text("fwDataPath") });
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (_, project) =>
            new CommandResult(0, CommandTextRenderer.Render(BuildCorpusList(project))));
    }

    /// <summary>The <c>corpora</c> report as JSON, rendered from the same projection as text.</summary>
    public static CommandResult ListCorporaJson(string fwDataPath, string productVersion, UsageLog? usage = null)
    {
        usage?.Record("corpora", new[] { UsageArgumentShape.Text("fwDataPath") });
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (_, project) =>
            new CommandResult(0, ProjectionJson.Serialize(BuildCorpusList(project)) + Environment.NewLine));
    }

    private static CorpusListProjection BuildCorpusList(ProjectLocator project)
        => CorpusProjectionQuery.List(StoreFor(project));

    /// <summary>One Corpus in full: provenance, every Document, and what each is licensed for.</summary>
    public static CommandResult ShowCorpus(
        string fwDataPath, string productVersion, string corpusId, UsageLog? usage = null)
    {
        usage?.Record(
            "show-corpus",
            new[] { UsageArgumentShape.Text("fwDataPath"), UsageArgumentShape.Text("corpusId") });
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (_, project) =>
        {
            var (projection, error) = BuildCorpusDetail(project, corpusId);
            return projection is not null
                ? new CommandResult(0, CommandTextRenderer.Render(projection))
                : new CommandResult(1, error!);
        });
    }

    /// <summary>The <c>show-corpus</c> report as JSON, rendered from the same projection as text.</summary>
    public static CommandResult ShowCorpusJson(
        string fwDataPath, string productVersion, string corpusId, UsageLog? usage = null)
    {
        usage?.Record(
            "show-corpus",
            new[] { UsageArgumentShape.Text("fwDataPath"), UsageArgumentShape.Text("corpusId") });
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (_, project) =>
        {
            var (projection, error) = BuildCorpusDetail(project, corpusId);
            return projection is not null
                ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
                : new CommandResult(1, error!);
        });
    }

    private static (CorpusDetailProjection? Projection, string? Error) BuildCorpusDetail(
        ProjectLocator project, string corpusId)
    {
        var projection = CorpusProjectionQuery.Detail(StoreFor(project), corpusId);
        return projection is null
            ? (null, $"No corpus '{corpusId}' in store." + Environment.NewLine)
            : (projection, null);
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
