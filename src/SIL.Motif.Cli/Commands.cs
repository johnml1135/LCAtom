using SIL.Motif.Contract.Responses;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SIL.Motif.Cli.Store;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Analysis;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Host.Store;
using SIL.Motif.Projection;
using SIL.Motif.Projection.Rendering;
using SIL.Motif.Projection.Store;
using SIL.Motif.Projection.Usage;
using SIL.Motif.Runner.Apply;
using SIL.Motif.Runner.AppliedLog;
using SIL.Motif.Runner.Composers;
using SIL.Motif.Runner.DryRun;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Worker.Store;
using SIL.LCModel;

namespace SIL.Motif.Cli;

/// <summary>The result of one CLI command: an exit code, the text to print, and why it refused.</summary>
/// <remarks>
/// <see cref="Reason"/> is null on success and on the few failures raised before a verb is entered.
/// It is what <c>--json</c> renders as a structured envelope; the text in <see cref="Output"/> is the
/// same wording either way, because the human interface did not need changing.
/// </remarks>
public sealed record CommandResult(int ExitCode, string Output, FailureReason? Reason = null);

/// <summary>
/// Testable command handlers for every Motif CLI verb, driving the project's paired database (see
/// <see cref="SIL.Motif.Worker.Store.ProposalRepository"/>) and the real Contract/Runner/Host APIs end
/// to end. <c>Program.cs</c> is a thin argument dispatcher over these methods: every method here is a
/// plain function of explicit parameters returning a <see cref="CommandResult"/>, so tests call them
/// directly rather than shelling out to the built executable.
/// </summary>
/// <remarks>
/// <para>
/// This class never re-implements dry-run/apply/log semantics: it calls
/// <see cref="ProposalDryRunner.Run"/>, <see cref="ProposalApplier.Apply"/>,
/// <see cref="ProjectAppliedLog.ReadAll"/>, and <see cref="FwDataProjectLoader"/> exactly as Stages
/// C/D/A left them.
/// </para>
/// <para>
/// The static constructor force-loads the Runner assembly's module initializers up front. Kind
/// constants such as <c>LexicalSenseOperationKinds.SetGloss</c> are compiler-inlined literals, so a
/// command that only ever touches Contract's <see cref="ProposalJsonParser"/> (building or
/// finalizing a draft) would never otherwise trigger the Runner assembly to load and register its
/// kinds, and "Unknown operation kind" would fire even though a later DryRun/Apply in the same
/// process would have worked. Forcing it here once, up front, makes registration independent of
/// which command runs first.
/// </para>
/// </remarks>
public static class Commands
{
    static Commands()
    {
        // Force the Runner assembly's module initializers to run now; see the class remarks for why.
        RuntimeHelpers.RunModuleConstructor(typeof(LexicalSenseOperationKinds).Module.ModuleHandle);
    }

    private static readonly JsonSerializerOptions DraftJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Case-insensitive: Anchor's nested record matches JSON to ctor params, robust across runtimes.
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions ProposalJsonOptions = new()
    {
        WriteIndented = true,
        // Omit null entityId entirely: ParseOptionalId treats present-but-null as a type error, not absent.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static CommandResult Open(string fwDataPath, UsageLog? usage = null)
    {
        usage?.Record("open", new[] { UsageArgumentShape.Text("fwDataPath") });
        var (reason, projection, error) = BuildProjectSummary(fwDataPath);
        return projection is not null ? Ok(CommandTextRenderer.Render(projection)) : Refused(reason, error!);
    }

    /// <summary>The <c>open</c> report as JSON — the same <see cref="ProjectSummaryProjection"/> <see cref="Open"/> renders as text.</summary>
    public static CommandResult OpenJson(string fwDataPath, UsageLog? usage = null)
    {
        usage?.Record("open", new[] { UsageArgumentShape.Text("fwDataPath") });
        var (reason, projection, error) = BuildProjectSummary(fwDataPath);
        return projection is not null ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
            : Refused(reason, error!);
    }

    private static (FailureReason? Reason, ProjectSummaryProjection? Projection, string? Error) BuildProjectSummary(
        string fwDataPath)
    {
        try
        {
            var fullPath = ResolveProjectPath(fwDataPath);
            var loader = new FwDataProjectLoader();
            using var cache = loader.LoadCache(fullPath);
            return (null, ProjectSummaryReader.Read(cache), null);
        }
        catch (Exception ex)
        {
            return (ReasonFor(ex), null, FailText(ex.Message));
        }
    }

    public static CommandResult Analyses(string fwDataPath, UsageLog? usage = null)
    {
        usage?.Record("analyses", new[] { UsageArgumentShape.Text("fwDataPath") });
        var (reason, projection, error) = BuildManualAnalysisProjection(fwDataPath);
        return projection is not null ? Ok(CommandTextRenderer.Render(projection)) : Refused(reason, error!);
    }

    /// <summary>
    /// The <c>analyses</c> report as JSON, rendered from the same
    /// <see cref="AnalysisAggregateProjection"/> as <see cref="Analyses"/>.
    /// </summary>
    public static CommandResult AnalysesJson(string fwDataPath, UsageLog? usage = null)
    {
        usage?.Record("analyses", new[] { UsageArgumentShape.Text("fwDataPath") });
        var (reason, projection, error) = BuildManualAnalysisProjection(fwDataPath);
        return projection is not null
            ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
            : Refused(reason, error!);
    }

    public static CommandResult Analyses(
        string fwDataPath,
        string assessmentId,
        string currentCorpusSha256,
        string currentGrammarSourceSha256,
        UsageLog? usage = null)
    {
        RecordAssessmentAnalysisUsage(usage);
        var (reason, projection, error) = BuildAssessmentAnalysisProjection(
            fwDataPath, assessmentId, currentCorpusSha256, currentGrammarSourceSha256);
        return projection is not null ? Ok(CommandTextRenderer.Render(projection)) : Refused(reason, error!);
    }

    /// <summary>The Assessment-backed <c>analyses</c> report as JSON.</summary>
    public static CommandResult AnalysesJson(
        string fwDataPath,
        string assessmentId,
        string currentCorpusSha256,
        string currentGrammarSourceSha256,
        UsageLog? usage = null)
    {
        RecordAssessmentAnalysisUsage(usage);
        var (reason, projection, error) = BuildAssessmentAnalysisProjection(
            fwDataPath, assessmentId, currentCorpusSha256, currentGrammarSourceSha256);
        return projection is not null
            ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
            : Refused(reason, error!);
    }

    private static void RecordAssessmentAnalysisUsage(UsageLog? usage) =>
        usage?.Record(
            "analyses",
            new[]
            {
                UsageArgumentShape.Text("fwDataPath"),
                UsageArgumentShape.Text("assessmentId"),
                UsageArgumentShape.Text("currentCorpusSha256"),
                UsageArgumentShape.Text("currentGrammarSourceSha256"),
            });

    private static (FailureReason? Reason, AnalysisAggregateProjection? Projection, string? Error)
        BuildAssessmentAnalysisProjection(
            string fwDataPath,
            string assessmentId,
            string currentCorpusSha256,
            string currentGrammarSourceSha256)
    {
        try
        {
            Sha256Value.RequireCanonical(assessmentId, nameof(assessmentId));
            Sha256Value.RequireCanonical(currentCorpusSha256, nameof(currentCorpusSha256));
            Sha256Value.RequireCanonical(currentGrammarSourceSha256, nameof(currentGrammarSourceSha256));

            var fullPath = ResolveProjectPath(fwDataPath);
            var databasePath = ProjectDatabaseCatalog.DatabasePathFor(
                new ProjectLocator(fullPath, Path.GetFileNameWithoutExtension(fullPath)));
            if (!File.Exists(databasePath))
                return (FailureReason.NotFound, null, FailText($"Assessment '{assessmentId}' was not found in the Motif store."));

            var store = new SqliteAssessmentStore(databasePath);
            var assessment = store.Load(assessmentId);
            if (assessment is null)
                return (FailureReason.NotFound, null, FailText($"Assessment '{assessmentId}' was not found in the Motif store."));

            var loader = new FwDataProjectLoader();
            using var cache = loader.LoadScratchCache(fullPath);
            return (
                0,
                AnalysisAggregateProjectionQuery.Read(
                    cache, assessment, currentCorpusSha256, currentGrammarSourceSha256),
                null);
        }
        catch (Exception ex)
        {
            return (ReasonFor(ex), null, FailText(ex.Message));
        }
    }

    private static (FailureReason? Reason, AnalysisAggregateProjection? Projection, string? Error)
        BuildManualAnalysisProjection(string fwDataPath)
    {
        try
        {
            var fullPath = ResolveProjectPath(fwDataPath);
            var loader = new FwDataProjectLoader();
            using var cache = loader.LoadScratchCache(fullPath);
            return (null, ManualAnalysisProjectionQuery.Read(cache), null);
        }
        catch (Exception ex)
        {
            return (ReasonFor(ex), null, FailText(ex.Message));
        }
    }

    public static CommandResult New(string fwDataPath, string productVersion, string draftName, string? label)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, _) =>
        {
            try
            {
                var repository = new ProposalRepository(database);
                if (repository.DraftNameExists(draftName))
                    return Fail(DraftNameCollisionMessage(draftName, "creating a new draft with this name"));

                var proposalId = CanonicalId.Mint();
                var draft = new DraftDocument
                {
                    ProposalId = proposalId.Value,
                    // Empty: EnsureContractVersion populates this from whatever operations actually get authored.
                    ContractVersions = new Dictionary<string, string>(),
                    Requires = new List<string>(),
                    Label = label,
                    Comment = null,
                    Operations = new List<DraftOperation>(),
                };

                repository.CreateDraft(draftName, proposalId, SerializeDraft(draft));

                var sb = new StringBuilder();
                sb.AppendLine($"Created draft '{draftName}'.");
                sb.AppendLine($"  proposalId: {draft.ProposalId}");
                if (label is not null)
                    sb.AppendLine($"  label:       {label}");
                return Ok(sb);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        });
    }

    public static CommandResult AddSetGloss(
        string fwDataPath, string productVersion, string draftName, string target, string ws, string text,
        IReadOnlyList<string>? dependsOn = null)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, _) =>
        {
            try
            {
                var repository = new ProposalRepository(database);
                if (!TryLoadDraft(repository, draftName, out var draft))
                    return Missing(DraftNotFoundMessage(draftName));

                if (!CanonicalId.TryParse(target, out var targetId, out var idError))
                    return Invalid($"--target '{target}' is not a valid canonical id: {idError}");

                if (string.IsNullOrEmpty(ws))
                    return Invalid("--ws must not be empty.");

                if (!TryResolveDependsOn(draft, dependsOn, out var resolvedDependsOn, out var dependsOnError))
                    return Fail(dependsOnError!);

                var operationId = CanonicalId.Mint();

                draft.Operations.Add(new DraftOperation
                {
                    OperationId = operationId.Value,
                    Kind = LexicalSenseOperationKinds.SetGloss,
                    Target = targetId.Value,
                    DependsOn = resolvedDependsOn,
                    After = new Dictionary<string, JsonElement>
                    {
                        ["ws"] = JsonSerializer.SerializeToElement(ws),
                        ["text"] = JsonSerializer.SerializeToElement(text),
                    },
                });
                EnsureContractVersion(draft, LexicalSenseOperationKinds.SetGloss);

                repository.SaveDraft(draftName, SerializeDraft(draft));

                var sb = new StringBuilder();
                sb.AppendLine(
                    $"Added operation '{operationId.Value}' ({LexicalSenseOperationKinds.SetGloss}) to draft '{draftName}'.");
                sb.AppendLine($"  target: {targetId.Value}");
                sb.AppendLine($"  after:  ws={ws} text=\"{text}\"");
                if (resolvedDependsOn.Count > 0)
                    sb.AppendLine($"  dependsOn: {string.Join(", ", resolvedDependsOn)}");
                sb.AppendLine($"Draft now has {draft.Operations.Count} operation(s).");
                return Ok(sb);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        });
    }

    /// <summary>
    /// Adds a <c>lexical/lexEntry/deleteLexemeForm</c> operation: a real, already-lowered
    /// cascading-delete operation kind (<see cref="LexEntryLexemeFormOperationKinds.DeleteLexemeForm"/>),
    /// exposed here so the removal analysis has a genuine cascading-delete operation to test
    /// against, not a synthetic one. Its <c>after</c> payload is the empty object — an entry has at
    /// most one lexeme form, so nothing is left to disambiguate once the target entry is known.
    /// </summary>
    public static CommandResult AddDeleteLexemeForm(
        string fwDataPath, string productVersion, string draftName, string target)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, _) =>
        {
            try
            {
                var repository = new ProposalRepository(database);
                if (!TryLoadDraft(repository, draftName, out var draft))
                    return Missing(DraftNotFoundMessage(draftName));

                if (!CanonicalId.TryParse(target, out var targetId, out var idError))
                    return Invalid($"--target '{target}' is not a valid canonical id: {idError}");

                var operationId = CanonicalId.Mint();

                draft.Operations.Add(new DraftOperation
                {
                    OperationId = operationId.Value,
                    Kind = LexEntryLexemeFormOperationKinds.DeleteLexemeForm,
                    Target = targetId.Value,
                    After = new Dictionary<string, JsonElement>(),
                });
                EnsureContractVersion(draft, LexEntryLexemeFormOperationKinds.DeleteLexemeForm);

                repository.SaveDraft(draftName, SerializeDraft(draft));

                var sb = new StringBuilder();
                sb.AppendLine(
                    $"Added operation '{operationId.Value}' ({LexEntryLexemeFormOperationKinds.DeleteLexemeForm}) " +
                    $"to draft '{draftName}'.");
                sb.AppendLine($"  target: {targetId.Value}");
                sb.AppendLine($"Draft now has {draft.Operations.Count} operation(s).");
                return Ok(sb);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        });
    }

    /// <summary>
    /// Runs <see cref="AuthorLexemeFormComposer"/> against a live project and appends the operations
    /// it resolves to a draft — the CLI's first Layer-1 authoring surface (ADR 0009 decision 1). The
    /// agent authors one intent rather than enumerating up to three operations by hand; the intent
    /// itself is recorded as non-hashed provenance on the eventual Proposal, never entering its intent
    /// digest.
    /// </summary>
    /// <param name="intentJson">
    /// <c>{ "entry": "...", "morphType": "...", "ws": "...", "text": "...", "isAbstract": false,
    /// "sense": "...", "glossWs": "...", "glossText": "..." }</c> — see
    /// <see cref="AuthorLexemeFormIntentParser"/> for the exact closed schema.
    /// </param>
    public static CommandResult ComposeAuthorLexemeForm(
        string fwDataPath, string productVersion, string draftName, string intentJson)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, project) =>
        {
            try
            {
                var repository = new ProposalRepository(database);
                if (!TryLoadDraft(repository, draftName, out var draft))
                    return Missing(DraftNotFoundMessage(draftName));

                using var intentDocument = JsonDocument.Parse(intentJson);
                var intent = AuthorLexemeFormIntentParser.Parse(intentDocument.RootElement);

                var loader = new FwDataProjectLoader();
                IReadOnlyList<SIL.Motif.Contract.Model.OperationEnvelope> operations;
                using (var cache = loader.LoadCache(project.FullFwDataPath))
                    operations = AuthorLexemeFormComposer.Build(cache, intent);

                foreach (var operation in operations)
                {
                    draft.Operations.Add(ToDraftOperation(operation));
                    EnsureContractVersion(draft, operation.Kind);
                }

                var provenanceJson = JsonSerializer.Serialize(
                    new { composer = "AuthorLexemeForm", input = intentDocument.RootElement });
                using var provenanceDocument = JsonDocument.Parse(provenanceJson);
                draft.ComposerProvenance.Add(provenanceDocument.RootElement.Clone());

                repository.SaveDraft(draftName, SerializeDraft(draft));

                var sb = new StringBuilder();
                sb.AppendLine(
                    $"Composed 'AuthorLexemeForm' against draft '{draftName}': {operations.Count} operation(s) added.");
                foreach (var operation in operations)
                    sb.AppendLine($"  {operation.OperationId.Value}  ({operation.Kind})");
                sb.AppendLine($"Draft now has {draft.Operations.Count} operation(s).");
                return Ok(sb);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        });
    }

    /// <summary>
    /// Runs <see cref="AuthorFeatureStructureComposer"/> against a live project and appends the one
    /// operation it resolves to a draft — Motif's first grammar Layer-1 construct, alongside the
    /// lexical <see cref="ComposeAuthorLexemeForm"/>.
    /// </summary>
    /// <param name="intentJson"><c>{ "msa": "..." }</c> — see <see cref="AuthorFeatureStructureIntentParser"/>.</param>
    public static CommandResult ComposeAuthorFeatureStructure(
        string fwDataPath, string productVersion, string draftName, string intentJson)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, project) =>
        {
            try
            {
                var repository = new ProposalRepository(database);
                if (!TryLoadDraft(repository, draftName, out var draft))
                    return Missing(DraftNotFoundMessage(draftName));

                using var intentDocument = JsonDocument.Parse(intentJson);
                var intent = AuthorFeatureStructureIntentParser.Parse(intentDocument.RootElement);

                var loader = new FwDataProjectLoader();
                IReadOnlyList<SIL.Motif.Contract.Model.OperationEnvelope> operations;
                using (var cache = loader.LoadCache(project.FullFwDataPath))
                    operations = AuthorFeatureStructureComposer.Build(cache, intent);

                foreach (var operation in operations)
                {
                    draft.Operations.Add(ToDraftOperation(operation));
                    EnsureContractVersion(draft, operation.Kind);
                }

                var provenanceJson = JsonSerializer.Serialize(
                    new { composer = "AuthorFeatureStructure", input = intentDocument.RootElement });
                using var provenanceDocument = JsonDocument.Parse(provenanceJson);
                draft.ComposerProvenance.Add(provenanceDocument.RootElement.Clone());

                repository.SaveDraft(draftName, SerializeDraft(draft));

                var sb = new StringBuilder();
                sb.AppendLine(
                    $"Composed 'AuthorFeatureStructure' against draft '{draftName}': {operations.Count} operation(s) added.");
                foreach (var operation in operations)
                    sb.AppendLine($"  {operation.OperationId.Value}  ({operation.Kind})");
                sb.AppendLine($"Draft now has {draft.Operations.Count} operation(s).");
                return Ok(sb);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        });
    }

    /// <summary>
    /// Adds a <c>lexical/lexSense/setGloss</c> operation evidenced by a stored corpus — the only
    /// sanctioned route from the Motif store into the language project (ADR 0036 decision 2). The
    /// corpus's origin travels with the operation as non-hashed provenance, so a licence obligation a
    /// promoted value carries (e.g. CC-BY-SA attribution) is never lost between the evidence and the
    /// dictionary entry it justified.
    /// </summary>
    /// <remarks>
    /// The draft and the corpus both live in the project's paired database, opened once through
    /// <see cref="ProjectStoreCommand.Run"/> from <paramref name="fwDataPath"/> and
    /// <paramref name="productVersion"/> so the two never disagree about which project a corpus id
    /// names.
    /// </remarks>
    public static CommandResult PromoteGloss(
        string fwDataPath, string productVersion, string draftName, string target, string ws, string text,
        string corpusId, string? documentId = null)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, project) =>
        {
            try
            {
                var repository = new ProposalRepository(database);
                if (!TryLoadDraft(repository, draftName, out var draft))
                    return Missing(DraftNotFoundMessage(draftName));

                var corpus = CorpusCommands.StoreFor(project).Load(corpusId);
                if (corpus is null)
                    return Fail($"Corpus '{corpusId}' not found. Run 'corpora' to see what is there.");

                if (documentId is not null && corpus.Documents.All(d => d.DocumentId != documentId))
                    return Missing($"Corpus '{corpusId}' has no document '{documentId}'.");

                if (!CanonicalId.TryParse(target, out var targetId, out var idError))
                    return Invalid($"--target '{target}' is not a valid canonical id: {idError}");

                if (string.IsNullOrEmpty(ws))
                    return Invalid("--ws must not be empty.");

                var operationId = CanonicalId.Mint();

                draft.Operations.Add(new DraftOperation
                {
                    OperationId = operationId.Value,
                    Kind = LexicalSenseOperationKinds.SetGloss,
                    Target = targetId.Value,
                    After = new Dictionary<string, JsonElement>
                    {
                        ["ws"] = JsonSerializer.SerializeToElement(ws),
                        ["text"] = JsonSerializer.SerializeToElement(text),
                    },
                });
                EnsureContractVersion(draft, LexicalSenseOperationKinds.SetGloss);

                var origin = corpus.Provenance.Origin;
                var provenanceJson = JsonSerializer.Serialize(new
                {
                    operationId = operationId.Value,
                    corpusId,
                    documentId,
                    description = origin.Description,
                    licence = origin.Licence,
                    retrievedUtc = origin.RetrievedUtc,
                });
                using var provenanceDocument = JsonDocument.Parse(provenanceJson);
                draft.PromotionProvenance.Add(provenanceDocument.RootElement.Clone());

                repository.SaveDraft(draftName, SerializeDraft(draft));

                var sb = new StringBuilder();
                sb.AppendLine(
                    $"Added operation '{operationId.Value}' ({LexicalSenseOperationKinds.SetGloss}) to draft " +
                    $"'{draftName}', promoted from corpus '{corpusId}'.");
                sb.AppendLine($"  target: {targetId.Value}");
                sb.AppendLine($"  after:  ws={ws} text=\"{text}\"");
                if (origin.Licence is not null)
                    sb.AppendLine($"  licence: {origin.Licence}");
                sb.AppendLine($"Draft now has {draft.Operations.Count} operation(s).");
                return Ok(sb);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        });
    }

    /// <summary>Validates each dependsOn id is already a canonical operation id present in draft.</summary>
    private static bool TryResolveDependsOn(
        DraftDocument draft, IReadOnlyList<string>? dependsOn, out List<string> resolved, out string? error)
    {
        resolved = new List<string>();
        error = null;

        if (dependsOn is null)
            return true;

        var existingIds = new HashSet<string>(draft.Operations.Select(o => o.OperationId), StringComparer.Ordinal);

        foreach (var raw in dependsOn)
        {
            if (!CanonicalId.TryParse(raw, out var id, out var idError))
            {
                error = $"--depends-on '{raw}' is not a valid canonical operation id: {idError}";
                return false;
            }

            if (!existingIds.Contains(id.Value))
            {
                error = $"--depends-on '{id.Value}' does not name an operation already in this draft.";
                return false;
            }

            resolved.Add(id.Value);
        }

        return true;
    }

    public static CommandResult Label(string fwDataPath, string productVersion, string draftName, string text) =>
        SetDraftField(fwDataPath, productVersion, draftName, "label", (draft) => draft.Label = text);

    public static CommandResult Comment(string fwDataPath, string productVersion, string draftName, string text) =>
        SetDraftField(fwDataPath, productVersion, draftName, "comment", (draft) => draft.Comment = text);

    private static CommandResult SetDraftField(
        string fwDataPath, string productVersion, string draftName, string fieldName, Action<DraftDocument> setter)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, _) =>
        {
            try
            {
                var repository = new ProposalRepository(database);
                if (!TryLoadDraft(repository, draftName, out var draft))
                    return Missing(DraftNotFoundMessage(draftName));

                setter(draft);
                repository.SaveDraft(draftName, SerializeDraft(draft));

                var sb = new StringBuilder();
                sb.AppendLine($"Set {fieldName} on draft '{draftName}'.");
                return Ok(sb);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        });
    }

    public static CommandResult Finalize(string fwDataPath, string productVersion, string draftName)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, _) =>
        {
            try
            {
                var repository = new ProposalRepository(database);
                if (!TryLoadDraft(repository, draftName, out var draft))
                    return Missing(DraftNotFoundMessage(draftName));

                if (string.IsNullOrWhiteSpace(draft.Label) || string.IsNullOrWhiteSpace(draft.Comment))
                {
                    return Fail(
                        $"Draft '{draftName}' cannot be finalized without both a short description (label) " +
                        $"and an extended explanation (comment). Set them with 'label --draft {draftName} <text>' " +
                        $"and 'comment --draft {draftName} <text>', then finalize again.");
                }

                if (draft.Operations.Count == 0)
                {
                    return Fail(
                        $"Draft '{draftName}' has no operations; add at least one (e.g. 'add-set-gloss') " +
                        "before finalize.");
                }

                var proposalJson = BuildProposalJson(draft);

                SIL.Motif.Contract.Model.Proposal envelope;
                try
                {
                    envelope = ProposalJsonParser.Parse(proposalJson);
                }
                catch (ContractParseException ex)
                {
                    return Fail($"Draft '{draftName}' failed Proposal validation: {ex.Message}");
                }

                var intentDigest = IntentDigest.Compute(envelope);

                // Whether a committed revision already existed under this id decides "Finalized" vs "Amended".
                var isAmend = repository.Finalize(draftName, intentDigest, proposalJson, draft.Label!, draft.Comment!);

                var sb = new StringBuilder();
                if (isAmend)
                {
                    sb.AppendLine($"Amended draft '{draftName}' -> Proposal {draft.ProposalId} (status: proposed).");
                    sb.AppendLine("  (id unchanged; intentDigest moved to a new revision; prior revision retained)");
                }
                else
                {
                    sb.AppendLine($"Finalized draft '{draftName}' -> Proposal {draft.ProposalId} (status: proposed).");
                }
                sb.AppendLine($"  operations:   {envelope.Operations.Count}");
                sb.AppendLine($"  intentDigest: {intentDigest}");
                return Ok(sb);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        });
    }

    public static CommandResult Reopen(string fwDataPath, string productVersion, string draftName, string proposalId)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, _) =>
        {
            var id = proposalId;
            try
            {
                var repository = new ProposalRepository(database);
                if (repository.DraftNameExists(draftName))
                    return Fail(DraftNameCollisionMessage(draftName, "reopening a Proposal with this draft name"));

                id = NormalizeId(proposalId);
                var canonicalId = CanonicalId.Parse(id);
                var (record, envelope) = repository.GetFinalized(canonicalId);
                var manifest = ProposalRecordMapping.ToManifest(record);

                // Loads the envelope's content into a new draft with the SAME proposalId; finalize then produces an amend.
                var draft = new DraftDocument
                {
                    ProposalId = id,
                    ContractVersions = new Dictionary<string, string>(envelope.ContractVersions),
                    Requires = envelope.Requires.Select(r => r.Value).ToList(),
                    Label = manifest.Label,
                    Comment = manifest.Comment,
                    Operations = envelope.Operations.Select(ToDraftOperation).ToList(),
                    ComposerProvenance = ExtractComposerProvenance(envelope.Extensions),
                    PromotionProvenance = ExtractPromotionProvenance(envelope.Extensions),
                };

                repository.ReopenAsDraft(canonicalId, draftName, SerializeDraft(draft));

                var sb = new StringBuilder();
                sb.AppendLine($"Reopened Proposal {id} for editing as draft '{draftName}'.");
                sb.AppendLine($"  currentIntentDigest: {manifest.CurrentIntentDigest}");
                sb.AppendLine($"  operations:          {draft.Operations.Count}");
                sb.AppendLine(
                    "Finalizing this draft will amend the Proposal: same id, new intentDigest, " +
                    "status reset to proposed.");
                return Ok(sb);
            }
            catch (KeyNotFoundException)
            {
                return Missing(ProposalNotFoundMessage(id));
            }
            catch (InvalidDataException ex)
            {
                return Fail(FailureReason.StoreInconsistent, ex.Message);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        });
    }

    private static readonly string[] DeferrableFrom = { ManifestStatus.Proposed, ManifestStatus.Approved };
    private static readonly string[] ApprovableFrom = { ManifestStatus.Proposed, ManifestStatus.Deferred };
    private static readonly string[] RejectableFrom =
        { ManifestStatus.Proposed, ManifestStatus.Deferred, ManifestStatus.Approved };
    private static readonly string[] SupersedableFrom =
        { ManifestStatus.Proposed, ManifestStatus.Deferred, ManifestStatus.Approved, ManifestStatus.Rejected };

    /// <summary>Moves a Proposal to <c>deferred</c>: still wanted, not currently applicable (ADR 0031 decision 4).</summary>
    public static CommandResult Defer(string fwDataPath, string productVersion, string proposalId) =>
        TransitionStatus(fwDataPath, productVersion, proposalId, ManifestStatus.Deferred, DeferrableFrom,
            (repository, id, _) => repository.SetStatus(id, ManifestStatus.Deferred, supersededBy: null, clearDecision: true));

    /// <summary>
    /// Records an <c>approved</c> Decision against a Proposal's exact current content. The actor type
    /// is never inferred — ADR 0031 decision 7 requires the record always show whether a human or an
    /// AI made the call.
    /// </summary>
    public static CommandResult Approve(
        string fwDataPath, string productVersion, string proposalId, string actorType, string actorId,
        string? comment = null) =>
        Decide(fwDataPath, productVersion, proposalId, DecisionOutcome.Approved, ManifestStatus.Approved,
            ApprovableFrom, actorType, actorId, comment);

    /// <summary>Records a <c>rejected</c> Decision against a Proposal's exact current content.</summary>
    public static CommandResult Reject(
        string fwDataPath, string productVersion, string proposalId, string actorType, string actorId,
        string? comment = null) =>
        Decide(fwDataPath, productVersion, proposalId, DecisionOutcome.Rejected, ManifestStatus.Rejected,
            RejectableFrom, actorType, actorId, comment);

    /// <summary>Marks a Proposal <c>superseded</c> by another, naming which one replaced it.</summary>
    public static CommandResult Supersede(
        string fwDataPath, string productVersion, string proposalId, string supersededByProposalId)
    {
        string supersededById;
        try
        {
            supersededById = NormalizeId(supersededByProposalId);
        }
        catch (ArgumentException ex)
        {
            return Invalid(ex.Message);
        }

        return TransitionStatus(fwDataPath, productVersion, proposalId, ManifestStatus.Superseded, SupersedableFrom,
            (repository, id, _) =>
                repository.SetStatus(id, ManifestStatus.Superseded, supersededById, clearDecision: true));
    }

    private static CommandResult Decide(
        string fwDataPath, string productVersion, string proposalId, string outcome, string newStatus,
        string[] allowedFrom, string actorType, string actorId, string? comment)
    {
        if (actorType != DecisionActorType.Human && actorType != DecisionActorType.Ai)
        {
            return Fail(
                $"actorType must be '{DecisionActorType.Human}' or '{DecisionActorType.Ai}' — an AI actor " +
                "must never be recorded as if it were a human, or the reverse (ADR 0031 decision 7).");
        }

        if (string.IsNullOrWhiteSpace(actorId))
            return Invalid("actorId must not be empty — a Decision must name who made it.");

        return TransitionStatus(fwDataPath, productVersion, proposalId, newStatus, allowedFrom,
            (repository, id, record) => repository.SaveDecision(new DecisionRecord(
                id, record.IntentDigest!, outcome, actorType, actorId, comment,
                SIL.Motif.Model.AppliedLog.AppliedLogFormat.FormatTimestamp(DateTime.UtcNow))));
    }

    /// <summary>Moves a Proposal to a new status, refusing if its current status is not an allowed origin.</summary>
    private static CommandResult TransitionStatus(
        string fwDataPath, string productVersion, string proposalId, string newStatus, string[] allowedFrom,
        Action<ProposalRepository, CanonicalId, ProposalRecord> persist)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, _) =>
        {
            try
            {
                var repository = new ProposalRepository(database);
                var id = NormalizeId(proposalId);
                var canonicalId = CanonicalId.Parse(id);

                ProposalRecord record;
                try
                {
                    record = repository.GetForTransition(canonicalId);
                }
                catch (KeyNotFoundException)
                {
                    return Missing(ProposalNotFoundMessage(id));
                }

                if (Array.IndexOf(allowedFrom, record.Status) < 0)
                {
                    return Fail(
                        $"Proposal {id} is '{record.Status}'; cannot move to '{newStatus}' from there. " +
                        $"Allowed from: {string.Join(", ", allowedFrom)}.");
                }

                persist(repository, canonicalId, record);

                return Ok($"Proposal {id} is now '{newStatus}'.{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        });
    }

    /// <summary>Ensures a newly appended operation's contract group has a declared version.</summary>
    private static void EnsureContractVersion(DraftDocument draft, string kind)
    {
        var group = SIL.Motif.Contract.Model.OperationKind.GetGroup(kind);
        if (!draft.ContractVersions.ContainsKey(group))
            draft.ContractVersions[group] = "1.0";
    }

    /// <summary>Recovers composer provenance from a committed Proposal's <c>extensions</c>.</summary>
    private static List<JsonElement> ExtractComposerProvenance(JsonElement? extensions) =>
        ExtractProvenanceArray(extensions, "composers");

    /// <summary>Recovers promotion provenance from a committed Proposal's <c>extensions</c>.</summary>
    private static List<JsonElement> ExtractPromotionProvenance(JsonElement? extensions) =>
        ExtractProvenanceArray(extensions, "promotions");

    private static List<JsonElement> ExtractProvenanceArray(JsonElement? extensions, string propertyName)
    {
        if (extensions is not { } present ||
            present.ValueKind != JsonValueKind.Object ||
            !present.TryGetProperty(propertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return new List<JsonElement>();
        }

        return array.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static DraftOperation ToDraftOperation(SIL.Motif.Contract.Model.OperationEnvelope operation)
    {
        if (operation.Target is not { } target)
        {
            throw new NotSupportedException(
                $"Reopen does not yet support an operation with no 'target' (kind '{operation.Kind}').");
        }

        if (operation.After is not { } after)
        {
            throw new NotSupportedException(
                $"Reopen does not yet support an operation with no 'after' payload (kind '{operation.Kind}').");
        }

        var afterDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(after.GetRawText())
            ?? new Dictionary<string, JsonElement>();

        return new DraftOperation
        {
            OperationId = operation.OperationId.Value,
            Kind = operation.Kind,
            Target = target.Value,
            EntityId = operation.EntityId?.Value,
            DependsOn = operation.DependsOn.Select(d => d.OperationId.Value).ToList(),
            After = afterDict,
        };
    }

    /// <summary>
    /// Duplicates a committed Proposal's current content into a brand-new draft under a freshly
    /// minted <c>proposalId</c> — a distinct Proposal, not a revision of the source (contrast
    /// <see cref="Reopen"/>, which keeps the source id and produces an amend).
    /// </summary>
    public static CommandResult Duplicate(
        string fwDataPath, string productVersion, string sourceProposalId, string newDraftName)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, _) =>
        {
            var sourceId = sourceProposalId;
            try
            {
                var repository = new ProposalRepository(database);
                if (repository.DraftNameExists(newDraftName))
                {
                    return Fail(DraftNameCollisionMessage(
                        newDraftName, "duplicating a Proposal into a draft with this name"));
                }

                sourceId = NormalizeId(sourceProposalId);
                var (record, envelope) = repository.GetFinalized(CanonicalId.Parse(sourceId));
                var manifest = ProposalRecordMapping.ToManifest(record);

                var newProposalId = CanonicalId.Mint();
                var draft = new DraftDocument
                {
                    ProposalId = newProposalId.Value,
                    ContractVersions = new Dictionary<string, string>(envelope.ContractVersions),
                    Requires = envelope.Requires.Select(r => r.Value).ToList(),
                    Label = manifest.Label,
                    Comment = manifest.Comment,
                    Operations = envelope.Operations.Select(ToDraftOperation).ToList(),
                    ComposerProvenance = ExtractComposerProvenance(envelope.Extensions),
                    PromotionProvenance = ExtractPromotionProvenance(envelope.Extensions),
                };

                repository.CreateDraft(newDraftName, newProposalId, SerializeDraft(draft));

                var sb = new StringBuilder();
                sb.AppendLine($"Duplicated Proposal {sourceId} into new draft '{newDraftName}'.");
                sb.AppendLine($"  proposalId: {draft.ProposalId}  (a new Proposal; the source is untouched)");
                sb.AppendLine($"  operations: {draft.Operations.Count}");
                sb.AppendLine("Finalizing this draft will commit it as a brand-new Proposal.");
                return Ok(sb);
            }
            catch (KeyNotFoundException)
            {
                return Missing(ProposalNotFoundMessage(sourceId));
            }
            catch (InvalidDataException ex)
            {
                return Fail(FailureReason.StoreInconsistent, ex.Message);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        });
    }

    /// <summary>
    /// Removes one or more operations from a draft (created by <see cref="New"/> or reopened by
    /// <see cref="Reopen"/>), applying ADR 0021 decision 6: a removal with no dependents
    /// just happens; a removal that would orphan a dependent operation warns and names every
    /// consequence, then requires <paramref name="force"/>; a removal whose consequences cannot be
    /// honestly enumerated (a cascading <c>delete</c> operation — see
    /// <see cref="OperationDependencyGraph.IsCascadingDelete"/>) is refused outright, never forced.
    /// The caller still runs <c>finalize</c> afterwards (an amend, if this draft came from
    /// <c>reopen</c>) — this composes with the existing reopen/amend loop rather than bypassing it,
    /// which is also what clears a stale bound-DryRun anchor: <c>Finalize</c>'s amend path already
    /// sets <c>manifest.Anchor = null</c> on any content change, and a removal is exactly that.
    /// </summary>
    public static CommandResult RemoveOperations(
        string fwDataPath, string productVersion, string draftName, IReadOnlyList<string> operationIds, bool force)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, _) =>
        {
        try
        {
            var repository = new ProposalRepository(database);
            if (!TryLoadDraft(repository, draftName, out var draft))
                return Missing(DraftNotFoundMessage(draftName));

            if (operationIds.Count == 0)
                return Invalid("Specify at least one operation id to remove.");

            var requestedIds = new List<string>();
            foreach (var raw in operationIds)
            {
                if (!CanonicalId.TryParse(raw, out var id, out var error))
                    return Invalid($"'{raw}' is not a valid canonical operation id: {error}");
                requestedIds.Add(id.Value);
            }

            var byId = draft.Operations.ToDictionary(o => o.OperationId, StringComparer.Ordinal);
            var missing = requestedIds.Where(id => !byId.ContainsKey(id)).ToList();
            if (missing.Count > 0)
            {
                return Fail(
                    $"Draft '{draftName}' has no operation(s) {string.Join(", ", missing.Select(m => $"'{m}'"))}. " +
                    "Run 'show' on the source Proposal, or inspect the draft file, to find valid operation ids.");
            }

            var requestedOps = requestedIds.Select(id => byId[id]).ToList();

            // Decision 6, point 4: force never means "guess" -- a cascading delete's reach is discovered-only, so refuse.
            var unenumerable = requestedOps.FirstOrDefault(op => OperationDependencyGraph.IsCascadingDelete(op.Kind));
            if (unenumerable is not null)
            {
                return Fail(
                    $"Cannot remove operation '{unenumerable.OperationId}' ({unenumerable.Kind}): it is a " +
                    "cascading delete. LibLCM's ownership cascade reaches objects this Proposal never " +
                    "names, and that reach is only known by inspecting the live project — this store has " +
                    "no way to enumerate what removing it would affect. This removal is refused, not " +
                    "forced; --force cannot help, because there is no enumerated consequence set for it " +
                    "to accept.");
            }

            var requestedSet = new HashSet<string>(requestedIds, StringComparer.Ordinal);
            var consequences = OperationDependencyGraph.TransitiveDependents(draft.Operations, requestedSet);

            if (consequences.Count > 0 && !force)
            {
                var sb = new StringBuilder();
                sb.AppendLine(
                    $"Removing {DescribeOperationIds(requestedIds)} from draft '{draftName}' would orphan " +
                    $"{consequences.Count} dependent operation(s):");
                foreach (var edge in consequences)
                    sb.AppendLine($"  - {edge.Reason}");
                sb.AppendLine(
                    "Re-run with --force to remove the requested operation(s) together with every " +
                    "enumerated dependent above (force accepts the whole named consequence set, never a " +
                    "guess).");
                return new CommandResult(1, sb.ToString());
            }

            // Force accepts the full enumerated set (requested + every transitive dependent), never a partial guess.
            var toRemove = new HashSet<string>(requestedSet, StringComparer.Ordinal);
            foreach (var edge in consequences)
                toRemove.Add(edge.DependentOperationId);

            draft.Operations = draft.Operations.Where(o => !toRemove.Contains(o.OperationId)).ToList();
            repository.SaveDraft(draftName, SerializeDraft(draft));

            var outSb = new StringBuilder();
            outSb.AppendLine($"Removed {DescribeOperationIds(requestedIds)} from draft '{draftName}'.");
            if (consequences.Count > 0)
            {
                outSb.AppendLine(
                    $"  --force also removed {consequences.Count} dependent operation(s) named above:");
                foreach (var edge in consequences)
                    outSb.AppendLine($"  - {edge.DependentOperationId} ({edge.DependentKind})");
            }
            outSb.AppendLine($"Draft now has {draft.Operations.Count} operation(s).");
            outSb.AppendLine(
                "Run 'finalize' to commit this as a new revision (an amend, clearing any bound-DryRun " +
                "anchor, if this draft came from 'reopen').");
            return Ok(outSb);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        });
    }

    /// <summary>One output group for <see cref="Split"/>: a new draft name and the operation ids
    /// (from the source Proposal) it receives.</summary>
    public sealed record SplitGroup(string DraftName, IReadOnlyList<string> OperationIds);

    /// <summary>
    /// Splits a committed Proposal's current operations into several brand-new drafts, each under
    /// its own freshly minted <c>proposalId</c>. The unit of splitting is the individual operation,
    /// subject to <c>requires</c>/<c>dependsOn</c>. <paramref name="groups"/> must
    /// partition every operation in the source exactly once. If a declared dependency (<c>dependsOn</c>
    /// or a <c>target</c> naming another operation's <c>entityId</c>) would be severed by landing its
    /// two ends in different groups, that is named as a consequence and requires
    /// <paramref name="force"/> — the same warn/enumerate/force rule as <see cref="RemoveOperations"/>
    /// (decision 6), because nothing here is discovered-only: every edge is declared in the source
    /// Proposal, so this never hits the "cannot be enumerated" refusal.
    /// </summary>
    /// <remarks>
    /// The source Proposal is left exactly as it was — split does not supersede or discard it. A
    /// "superseded" status transition is a separate concern this method intentionally does not decide.
    /// </remarks>
    public static CommandResult Split(
        string fwDataPath, string productVersion, string sourceProposalId, IReadOnlyList<SplitGroup> groups,
        bool force)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, _) =>
        {
        var sourceId = sourceProposalId;
        try
        {
            if (groups.Count == 0)
                return Invalid("Specify at least one group to split into.");

            var repository = new ProposalRepository(database);
            sourceId = NormalizeId(sourceProposalId);
            var (record, envelope) = repository.GetFinalized(CanonicalId.Parse(sourceId));
            var manifest = ProposalRecordMapping.ToManifest(record);
            var sourceOperations = envelope.Operations.Select(ToDraftOperation).ToList();
            var allIds = sourceOperations.Select(o => o.OperationId).ToList();

            foreach (var draftName in groups.Select(g => g.DraftName))
            {
                if (repository.DraftNameExists(draftName))
                    return Fail(DraftNameCollisionMessage(draftName, "splitting into a draft with this name"));
            }
            if (groups.Select(g => g.DraftName).Distinct(StringComparer.Ordinal).Count() != groups.Count)
                return Invalid("Each split group must target a distinct draft name.");

            // Validate the groups partition every source operation exactly once.
            var groupOfId = new Dictionary<string, string>(StringComparer.Ordinal);
            var duplicates = new List<string>();
            var unknown = new List<string>();
            foreach (var group in groups)
            {
                foreach (var rawId in group.OperationIds)
                {
                    if (!CanonicalId.TryParse(rawId, out var id, out var idError))
                        return Invalid($"'{rawId}' is not a valid canonical operation id: {idError}");

                    if (!allIds.Contains(id.Value))
                    {
                        unknown.Add(id.Value);
                        continue;
                    }

                    if (!groupOfId.TryAdd(id.Value, group.DraftName))
                        duplicates.Add(id.Value);
                }
            }

            if (unknown.Count > 0)
            {
                return Fail(
                    $"Proposal {sourceId} has no operation(s) {string.Join(", ", unknown.Select(u => $"'{u}'"))}.");
            }
            if (duplicates.Count > 0)
            {
                return Fail(
                    $"Operation(s) {string.Join(", ", duplicates.Select(d => $"'{d}'"))} were assigned to " +
                    "more than one split group; each operation must go to exactly one.");
            }
            var unassigned = allIds.Where(id => !groupOfId.ContainsKey(id)).ToList();
            if (unassigned.Count > 0)
            {
                return Fail(
                    $"Operation(s) {string.Join(", ", unassigned.Select(u => $"'{u}'"))} from Proposal " +
                    $"{sourceId} were not assigned to any split group. A split must place every operation " +
                    "in exactly one resulting Proposal.");
            }

            var allEdges = OperationDependencyGraph.AllEdges(sourceOperations);
            var severed = allEdges
                .Where(e => groupOfId[e.DependentOperationId] != groupOfId[e.RequiredOperationId])
                .ToList();

            if (severed.Count > 0 && !force)
            {
                var sb = new StringBuilder();
                sb.AppendLine(
                    $"Splitting Proposal {sourceId} this way would sever {severed.Count} declared " +
                    "dependency edge(s) across the resulting Proposals:");
                foreach (var edge in severed)
                {
                    sb.AppendLine(
                        $"  - {edge.Reason} ('{edge.DependentOperationId}' -> draft " +
                        $"'{groupOfId[edge.DependentOperationId]}'; '{edge.RequiredOperationId}' -> draft " +
                        $"'{groupOfId[edge.RequiredOperationId]}').");
                }
                sb.AppendLine(
                    "Re-run with --force to proceed anyway. The dependency reference is kept exactly as " +
                    "authored in the receiving Proposal, which will then name an operation id outside its " +
                    "own operations array.");
                return new CommandResult(1, sb.ToString());
            }

            var sb2 = new StringBuilder();
            sb2.AppendLine($"Split Proposal {sourceId} into {groups.Count} new draft(s).");
            if (severed.Count > 0)
            {
                sb2.AppendLine($"  --force accepted {severed.Count} severed dependency edge(s) named above.");
            }

            foreach (var group in groups)
            {
                var groupOperations = sourceOperations.Where(o => groupOfId[o.OperationId] == group.DraftName).ToList();
                var usedGroups = new HashSet<string>(
                    groupOperations.Select(o => SIL.Motif.Contract.Model.OperationKind.GetGroup(o.Kind)),
                    StringComparer.Ordinal);
                var contractVersions = envelope.ContractVersions
                    .Where(kv => usedGroups.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                var newProposalId = CanonicalId.Mint();
                var draft = new DraftDocument
                {
                    ProposalId = newProposalId.Value,
                    ContractVersions = contractVersions,
                    Requires = envelope.Requires.Select(r => r.Value).ToList(),
                    Label = manifest.Label is null ? null : $"{manifest.Label} (split: {group.DraftName})",
                    Comment = manifest.Comment,
                    Operations = groupOperations,
                };
                repository.CreateDraft(group.DraftName, newProposalId, SerializeDraft(draft));

                sb2.AppendLine($"  draft '{group.DraftName}': proposalId={draft.ProposalId} operations={draft.Operations.Count}");
            }

            sb2.AppendLine($"  (the source Proposal {sourceId} is unchanged)");
            sb2.AppendLine("Finalize each draft to commit it as a brand-new Proposal.");
            return Ok(sb2);
        }
        catch (KeyNotFoundException)
        {
            return Missing(ProposalNotFoundMessage(sourceId));
        }
        catch (InvalidDataException ex)
        {
            return Fail(FailureReason.StoreInconsistent, ex.Message);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        });
    }

    private static string DescribeOperationIds(IReadOnlyList<string> ids) =>
        ids.Count == 1 ? $"operation '{ids[0]}'" : $"operations {string.Join(", ", ids.Select(i => $"'{i}'"))}";

    public static CommandResult List(string fwDataPath, string productVersion, UsageLog? usage = null)
    {
        usage?.Record("list", new[] { UsageArgumentShape.Text("fwDataPath") });
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, _) =>
        {
            var (reason, projection, error) = BuildProposalList(database);
            return projection is not null ? Ok(CommandTextRenderer.Render(projection)) : Refused(reason, error!);
        });
    }

    /// <summary>The <c>list</c> report as JSON — the same <see cref="ProposalListProjection"/> <see cref="List"/> renders as text.</summary>
    public static CommandResult ListJson(string fwDataPath, string productVersion, UsageLog? usage = null)
    {
        usage?.Record("list", new[] { UsageArgumentShape.Text("fwDataPath") });
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, _) =>
        {
            var (reason, projection, error) = BuildProposalList(database);
            return projection is not null
                ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
                : Refused(reason, error!);
        });
    }

    private static (FailureReason? Reason, ProposalListProjection? Projection, string? Error) BuildProposalList(
        MotifDatabase database)
    {
        try
        {
            var repository = new ProposalRepository(database);
            var manifests = repository.List(new ProposalListFilter())
                .Select(ProposalRecordMapping.ToManifest)
                .ToList();
            return (null, ProposalListProjectionBuilder.Build(manifests), null);
        }
        catch (Exception ex)
        {
            return (ReasonForProposal(ex), null, FailText(ex.Message));
        }
    }

    public static CommandResult Show(
        string fwDataPath, string productVersion, string proposalId, UsageLog? usage = null)
    {
        usage?.Record("show", new[] { UsageArgumentShape.Text("fwDataPath"), UsageArgumentShape.Text("proposalId") });
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, _) =>
        {
            var (reason, projection, error) = BuildProposalDetail(database, proposalId);
            return projection is not null ? Ok(CommandTextRenderer.Render(projection)) : Refused(reason, error!);
        });
    }

    /// <summary>The <c>show</c> report as JSON — the same <see cref="ProposalDetailProjection"/> <see cref="Show"/> renders as text.</summary>
    public static CommandResult ShowJson(
        string fwDataPath, string productVersion, string proposalId, UsageLog? usage = null)
    {
        usage?.Record("show", new[] { UsageArgumentShape.Text("fwDataPath"), UsageArgumentShape.Text("proposalId") });
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, _) =>
        {
            var (reason, projection, error) = BuildProposalDetail(database, proposalId);
            return projection is not null
                ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
                : Refused(reason, error!);
        });
    }

    private static (FailureReason? Reason, ProposalDetailProjection? Projection, string? Error) BuildProposalDetail(
        MotifDatabase database, string proposalId)
    {
        try
        {
            var repository = new ProposalRepository(database);
            var id = NormalizeId(proposalId);
            var (record, envelope) = repository.GetFinalized(CanonicalId.Parse(id));
            var manifest = ProposalRecordMapping.ToManifest(record);
            return (null, ProposalDetailProjectionBuilder.Build(id, manifest, envelope), null);
        }
        catch (Exception ex)
        {
            return (ReasonForProposal(ex), null, FailText(ex.Message));
        }
    }

    public static CommandResult DryRun(
        string fwDataPath, string productVersion, string proposalId, UsageLog? usage = null)
    {
        RecordDryRunUsage(usage, "fwDataPath", "proposalId");
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, project) =>
        {
            var (reason, projection, error) = BuildDryRunProjection(database, project, proposalId);
            if (projection is null) return Refused(reason, error!);

            var sb = new StringBuilder(CommandTextRenderer.Render(projection));
            // State the side effect: a dry run reads as "nothing happens", but it saved the project first (ADR 0016).
            sb.AppendLine("  (the project was saved before measuring, so the scratch copy matched it; " +
                          "the project itself was not modified by this dry run)");
            return Ok(sb);
        });
    }

    /// <summary>The <c>dry-run</c> report as JSON — the same <see cref="DryRunProjection"/> <see cref="DryRun(string,string,string,UsageLog)"/> renders as text.</summary>
    public static CommandResult DryRunJson(
        string fwDataPath, string productVersion, string proposalId, UsageLog? usage = null)
    {
        RecordDryRunUsage(usage, "fwDataPath", "proposalId");
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, project) =>
        {
            var (reason, projection, error) = BuildDryRunProjection(database, project, proposalId);
            return projection is not null
                ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
                : Refused(reason, error!);
        });
    }

    private static (FailureReason? Reason, DryRunProjection? Projection, string? Error) BuildDryRunProjection(
        MotifDatabase database, ProjectLocator project, string proposalId)
    {
        LcmCache? cache = null;
        DryRunScratch? scratch = null;
        try
        {
            var repository = new ProposalRepository(database);
            var id = NormalizeId(proposalId);
            var canonicalId = CanonicalId.Parse(id);
            var (_, envelope) = repository.GetFinalized(canonicalId);

            var loader = new FwDataProjectLoader();

            // Hold the project open throughout: one writer at a time (ADR 0006 decision 4), or the anchor means nothing.
            cache = loader.LoadCache(project.FullFwDataPath);

            var appliedProposalIds = ProjectAppliedLog.ReadAll(cache)
                .Select(entry => entry.ProposalId)
                .ToArray();
            var plan = repository.PlanPrerequisites(envelope, appliedProposalIds);

            // Save before the copy: the scratch copies the FILE, so an uncommitted edit is invisible to it (ADR 0016).
            loader.Save(cache);

            // Mutate a throwaway copy and delete it: no rollback, so nothing leaves derived caches stale.
            var scratchRoot = Path.Combine(
                Path.GetTempPath(), "SIL.Motif.DryRun", Guid.NewGuid().ToString("N"));
            scratch = DryRunScratch.Adopt(
                new ScratchCacheFactory(loader).CreateFromFileCopy(project.FullFwDataPath, scratchRoot),
                $"file copy of {project.FullFwDataPath}",
                onDisposed: () => TryDeleteDirectory(scratchRoot));

            var dryRun = ProposalDryRunner.Run(scratch, plan);

            // Persist the bound-DryRun anchor (docs/adr/0004 decision 3): apply requires it present and unmoved.
            repository.SetAnchor(canonicalId, JsonSerializer.Serialize(dryRun.Anchor));

            return (null, DryRunProjectionBuilder.Build(id, dryRun), null);
        }
        catch (LcmFileLockedException)
        {
            // A held project is retryable once it is let go, which is what Busy tells a caller.
            return (FailureReason.Busy, null,
                FailText(ProjectInUseMessage(project.FullFwDataPath, "dry-run")));
        }
        catch (Exception ex)
        {
            return (ReasonFor(ex), null, FailText(ex.Message));
        }
        finally
        {
            // Discard, never revert: disposing the scratch is what undoes the dry run's mutations.
            scratch?.Dispose();

            // And release the project, so the linguist can have FieldWorks back.
            if (cache is { IsDisposed: false }) cache.Dispose();
        }
    }

    private static void RecordDryRunUsage(UsageLog? usage, params string[] names) =>
        usage?.Record("dry-run", names.Select(UsageArgumentShape.Text).ToList());

    /// <remarks>
    /// A failed apply rolls back, and a rollback is not an Undo: LexEntry headword/homograph and
    /// MoStemAllomorph monomorphemic caches can be left stale, and ADR 0005's non-undoable schema
    /// phase can survive outright. There is no field list to consult and nothing here can repair it —
    /// the rule is unconditional (ADR 0016): a caller must discard this <see cref="LcmCache"/> and
    /// reload the project rather than reuse it after a failed apply.
    /// </remarks>
    public static CommandResult Apply(
        string fwDataPath, string productVersion, string proposalId, string user, UsageLog? usage = null)
    {
        RecordApplyUsage(usage, "fwDataPath", "proposalId", "user");
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, project) =>
        {
            var (reason, projection, error) = BuildApplyProjection(database, project, proposalId, user);
            return projection is not null ? Ok(CommandTextRenderer.Render(projection)) : Refused(reason, error!);
        });
    }

    /// <summary>The <c>apply</c> report as JSON — the same <see cref="ApplyProjection"/> <see cref="Apply(string,string,string,string,UsageLog)"/> renders as text.</summary>
    public static CommandResult ApplyJson(
        string fwDataPath, string productVersion, string proposalId, string user, UsageLog? usage = null)
    {
        RecordApplyUsage(usage, "fwDataPath", "proposalId", "user");
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, project) =>
        {
            var (reason, projection, error) = BuildApplyProjection(database, project, proposalId, user);
            return projection is not null
                ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
                : Refused(reason, error!);
        });
    }

    private static (FailureReason? Reason, ApplyProjection? Projection, string? Error) BuildApplyProjection(
        MotifDatabase database, ProjectLocator project, string proposalId, string user)
    {
        LcmCache? cache = null;
        try
        {
            var repository = new ProposalRepository(database);
            var id = NormalizeId(proposalId);
            var canonicalId = CanonicalId.Parse(id);
            var (record, envelope) = repository.GetFinalized(canonicalId);
            var manifest = ProposalRecordMapping.ToManifest(record);

            // ADR 0004 decision 3: a bare apply with no bound DryRun is a hard error, checked before loading the project.
            if (manifest.Anchor is null)
            {
                return (FailureReason.Refused, null, FailText(
                    $"Proposal {id} has no bound DryRun recorded. Run " +
                    $"'dry-run {id} --project <fwdata>' first, then 'apply'."));
            }

            var loader = new FwDataProjectLoader();
            cache = loader.LoadCache(project.FullFwDataPath);
            try
            {
                var description = manifest.Label ?? "";
                var receipt = ProposalApplier.Apply(cache, envelope, manifest.Anchor, user, description);

                // The core never saves; the host does, only after the unit of work has closed.
                if (!receipt.AlreadyApplied)
                {
                    try { loader.Save(cache); }
                    catch (Exception ex)
                    {
                        throw new NeedsReconciliationException(
                            ReconciliationBoundary.Save,
                            $"Proposal {id} committed to the live project, but saving it to the .fwdata " +
                            "file failed partway through. This is not a rollback: the file's on-disk " +
                            "state is not guaranteed intact. Do not retry automatically -- inspect the " +
                            "project file before doing anything else with it.",
                            ex);
                    }
                }

                try
                {
                    repository.MarkApplied(canonicalId);
                }
                catch (Exception ex)
                {
                    throw new NeedsReconciliationException(
                        ReconciliationBoundary.ReceiptRecording,
                        $"Proposal {id} was applied and saved to the project, but recording that in the " +
                        "proposal store failed. The project and the store now disagree about whether " +
                        "this Proposal is applied. Do not retry automatically -- inspect the store before " +
                        "doing anything else with it.",
                        ex);
                }

                return (null, ApplyProjectionBuilder.Build(id, receipt), null);
            }
            finally
            {
                if (cache is { IsDisposed: false }) cache.Dispose();
            }
        }
        catch (LcmFileLockedException)
        {
            // A held project is retryable once it is let go, which is what Busy tells a caller.
            return (FailureReason.Busy, null,
                FailText(ProjectInUseMessage(project.FullFwDataPath, "apply")));
        }
        catch (NeedsReconciliationException ex)
        {
            // Distinct from the rollback wording below: the mutation may already be durable.
            return (ReasonFor(ex), null, FailText(ex.Message));
        }
        catch (Exception ex)
        {
            // A failed apply rolled back, not Undo: derived caches may be stale (ADR 0016) -- see the remarks above.
            return (FailureReason.StoreInconsistent, null, FailText(
                ex.Message +
                " [This LcmCache is no longer trustworthy: a failed apply rolls back, which does not " +
                "refresh LibLCM's derived caches. Discard it and reload the project.]"));
        }
    }

    private static void RecordApplyUsage(UsageLog? usage, params string[] names) =>
        usage?.Record("apply", names.Select(UsageArgumentShape.Text).ToList());

    public static CommandResult Log(string fwDataPath, UsageLog? usage = null)
    {
        usage?.Record("log", new[] { UsageArgumentShape.Text("fwDataPath") });
        var (reason, projection, error) = BuildAppliedLog(fwDataPath);
        return projection is not null ? Ok(CommandTextRenderer.Render(projection)) : Refused(reason, error!);
    }

    /// <summary>The <c>log</c> report as JSON — the same <see cref="AppliedLogProjection"/> <see cref="Log(string,UsageLog)"/> renders as text.</summary>
    public static CommandResult LogJson(string fwDataPath, UsageLog? usage = null)
    {
        usage?.Record("log", new[] { UsageArgumentShape.Text("fwDataPath") });
        var (reason, projection, error) = BuildAppliedLog(fwDataPath);
        return projection is not null ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
            : Refused(reason, error!);
    }

    private static (FailureReason? Reason, AppliedLogProjection? Projection, string? Error) BuildAppliedLog(string fwDataPath)
    {
        try
        {
            var fullFwDataPath = ResolveProjectPath(fwDataPath);
            var loader = new FwDataProjectLoader();
            using var cache = loader.LoadCache(fullFwDataPath);

            var diagnostics = new List<string>();
            var entries = ProjectAppliedLog.ReadAll(
                cache,
                (name, error) => diagnostics.Add($"  [unparseable Motif entry] name='{name}' error='{error}'"));

            return (null, AppliedLogProjectionBuilder.Build(fullFwDataPath, entries, diagnostics), null);
        }
        catch (Exception ex)
        {
            return (ReasonFor(ex), null, FailText(ex.Message));
        }
    }

    /// <summary>Explains a refused open: LibLCM's own message is confusing and lacks the fix (ADR 0030).</summary>
    private static string ProjectInUseMessage(string fwDataPath, string verb) =>
        $"Cannot {verb}: the project '{Path.GetFileNameWithoutExtension(fwDataPath)}' is in use by " +
        "another program — most likely FieldWorks, or another Motif command that has not finished. " +
        "Only one program may hold a FieldWorks project at a time, and Motif takes the same lock " +
        "FieldWorks does. Close the other program and try again.";

    /// <summary>Best-effort delete of a scratch copy: a leaked temp dir beats a reported Dry Run failure.</summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A still-locked native handle must not fail a dry run that already succeeded.
        }
    }

    private static string BuildProposalJson(DraftDocument draft)
    {
        var document = new
        {
            contractVersions = draft.ContractVersions,
            proposalId = draft.ProposalId,
            requires = draft.Requires,
            operations = draft.Operations.Select(op => new
            {
                operationId = op.OperationId,
                kind = op.Kind,
                entityId = op.EntityId,
                target = op.Target,
                dependsOn = op.DependsOn,
                after = op.After,
            }).ToList(),
            extensions = BuildExtensions(draft),
        };

        return JsonSerializer.Serialize(document, ProposalJsonOptions);
    }

    private static object? BuildExtensions(DraftDocument draft)
    {
        if (draft.ComposerProvenance.Count == 0 && draft.PromotionProvenance.Count == 0)
            return null;

        return new
        {
            composers = draft.ComposerProvenance.Count > 0 ? draft.ComposerProvenance : null,
            promotions = draft.PromotionProvenance.Count > 0 ? draft.PromotionProvenance : null,
        };
    }

    private static string ResolveProjectPath(string fwDataPath)
    {
        var full = Path.GetFullPath(fwDataPath);
        if (!File.Exists(full))
            throw new FileNotFoundException($"Project file not found: '{full}'.", full);
        return full;
    }

    private static string NormalizeId(string proposalId)
    {
        if (!CanonicalId.TryParse(proposalId, out var id, out var error))
            throw new ArgumentException($"'{proposalId}' is not a valid canonical Proposal id: {error}");
        return id.Value;
    }

    private static string DraftNotFoundMessage(string draftName) =>
        $"Draft '{draftName}' not found in store. Run 'new --draft {draftName}' first.";

    private static string ProposalNotFoundMessage(string id) =>
        $"Proposal '{id}' not found in store. Run 'list' to see committed proposals.";

    private static string DraftNameCollisionMessage(string draftName, string trailingClause) =>
        $"Draft '{draftName}' already exists. Finalize or delete it before {trailingClause}.";

    /// <summary>Loads one Draft's in-progress content by name, or reports it is not there.</summary>
    private static bool TryLoadDraft(ProposalRepository repository, string draftName, out DraftDocument draft)
    {
        try
        {
            draft = DeserializeDraft(repository.GetDraft(draftName).ProposalJson!);
            return true;
        }
        catch (KeyNotFoundException)
        {
            draft = null!;
            return false;
        }
    }

    private static DraftDocument DeserializeDraft(string json) =>
        JsonSerializer.Deserialize<DraftDocument>(json, DraftJsonOptions)
        ?? throw new InvalidOperationException("Draft content is empty or invalid.");

    private static string SerializeDraft(DraftDocument draft) =>
        JsonSerializer.Serialize(draft, DraftJsonOptions);

    private static CommandResult Ok(StringBuilder sb) => new(0, sb.ToString());

    private static CommandResult Ok(string text) => new(0, text);

    private static CommandResult Refused(FailureReason? reason, string text) =>
        new(FailureEnvelope.ExitCodeFor(reason ?? FailureReason.Refused), text,
            reason ?? FailureReason.Refused);

    /// In a Proposal-loading helper an absent file names an absent Proposal, which the type alone cannot say.
    private static FailureReason ReasonForProposal(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException => FailureReason.NotFound,
        _ => ReasonFor(exception),
    };

    /// A caught exception knows more than a broad catch does; anything else is refused, which does not retry.
    private static FailureReason ReasonFor(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException => FailureReason.InvalidArgument,
        ArgumentException => FailureReason.InvalidArgument,
        KeyNotFoundException => FailureReason.NotFound,
        InvalidDataException => FailureReason.StoreInconsistent,
        _ => FailureReason.Refused,
    };

    private static CommandResult Fail(string message) => Fail(FailureReason.Refused, message);

    private static CommandResult Fail(FailureReason reason, string message) =>
        new(FailureEnvelope.ExitCodeFor(reason), FailText(message), reason);

    /// A malformed flag or value: retrying the same invocation cannot help.
    private static CommandResult Invalid(string message) => Fail(FailureReason.InvalidArgument, message);

    /// The request named something that is not there.
    private static CommandResult Missing(string message) => Fail(FailureReason.NotFound, message);

    // Same rendering as Fail, for a Build* helper returning a bare string rather than a CommandResult.
    private static string FailText(string message) => "error: " + message + Environment.NewLine;
}
