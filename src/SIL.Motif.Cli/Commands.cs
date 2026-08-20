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
using SIL.LCModel;

namespace SIL.Motif.Cli;

/// <summary>The result of one CLI command: a captured exit code and the text to print.</summary>
public sealed record CommandResult(int ExitCode, string Output);

/// <summary>
/// Testable command handlers for every Motif CLI verb, driving the Stage E files store (see
/// <see cref="SIL.Motif.Cli.Store.ProposalStore"/>) and the real Contract/Runner/Host APIs end to
/// end. <c>Program.cs</c> is a thin argument dispatcher over these methods: every method here is a
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
        var (exitCode, projection, error) = BuildProjectSummary(fwDataPath);
        return projection is not null ? Ok(CommandTextRenderer.Render(projection)) : new CommandResult(exitCode, error!);
    }

    /// <summary>The <c>open</c> report as JSON — the same <see cref="ProjectSummaryProjection"/> <see cref="Open"/> renders as text.</summary>
    public static CommandResult OpenJson(string fwDataPath, UsageLog? usage = null)
    {
        usage?.Record("open", new[] { UsageArgumentShape.Text("fwDataPath") });
        var (exitCode, projection, error) = BuildProjectSummary(fwDataPath);
        return projection is not null ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
            : new CommandResult(exitCode, error!);
    }

    private static (int ExitCode, ProjectSummaryProjection? Projection, string? Error) BuildProjectSummary(
        string fwDataPath)
    {
        try
        {
            var fullPath = ResolveProjectPath(fwDataPath);
            var loader = new FwDataProjectLoader();
            using var cache = loader.LoadCache(fullPath);
            return (0, ProjectSummaryReader.Read(cache), null);
        }
        catch (Exception ex)
        {
            return (1, null, FailText(ex.Message));
        }
    }

    public static CommandResult Analyses(string fwDataPath, UsageLog? usage = null)
    {
        usage?.Record("analyses", new[] { UsageArgumentShape.Text("fwDataPath") });
        var (exitCode, projection, error) = BuildManualAnalysisProjection(fwDataPath);
        return projection is not null ? Ok(CommandTextRenderer.Render(projection)) : new CommandResult(exitCode, error!);
    }

    /// <summary>
    /// The <c>analyses</c> report as JSON, rendered from the same
    /// <see cref="AnalysisAggregateProjection"/> as <see cref="Analyses"/>.
    /// </summary>
    public static CommandResult AnalysesJson(string fwDataPath, UsageLog? usage = null)
    {
        usage?.Record("analyses", new[] { UsageArgumentShape.Text("fwDataPath") });
        var (exitCode, projection, error) = BuildManualAnalysisProjection(fwDataPath);
        return projection is not null
            ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
            : new CommandResult(exitCode, error!);
    }

    public static CommandResult Analyses(
        string storeDir,
        string fwDataPath,
        string assessmentId,
        string currentCorpusSha256,
        string currentGrammarSourceSha256,
        UsageLog? usage = null)
    {
        RecordAssessmentAnalysisUsage(usage);
        var (exitCode, projection, error) = BuildAssessmentAnalysisProjection(
            storeDir, fwDataPath, assessmentId, currentCorpusSha256, currentGrammarSourceSha256);
        return projection is not null ? Ok(CommandTextRenderer.Render(projection)) : new CommandResult(exitCode, error!);
    }

    /// <summary>The Assessment-backed <c>analyses</c> report as JSON.</summary>
    public static CommandResult AnalysesJson(
        string storeDir,
        string fwDataPath,
        string assessmentId,
        string currentCorpusSha256,
        string currentGrammarSourceSha256,
        UsageLog? usage = null)
    {
        RecordAssessmentAnalysisUsage(usage);
        var (exitCode, projection, error) = BuildAssessmentAnalysisProjection(
            storeDir, fwDataPath, assessmentId, currentCorpusSha256, currentGrammarSourceSha256);
        return projection is not null
            ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
            : new CommandResult(exitCode, error!);
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

    private static (int ExitCode, AnalysisAggregateProjection? Projection, string? Error)
        BuildAssessmentAnalysisProjection(
            string storeDir,
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

            var databasePath = Path.Combine(storeDir, "motif.db");
            if (!File.Exists(databasePath))
                return (1, null, FailText($"Assessment '{assessmentId}' was not found in the Motif store."));

            var store = new SqliteAssessmentStore(databasePath);
            var assessment = store.Load(assessmentId);
            if (assessment is null)
                return (1, null, FailText($"Assessment '{assessmentId}' was not found in the Motif store."));

            var fullPath = ResolveProjectPath(fwDataPath);
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
            return (1, null, FailText(ex.Message));
        }
    }

    private static (int ExitCode, AnalysisAggregateProjection? Projection, string? Error)
        BuildManualAnalysisProjection(string fwDataPath)
    {
        try
        {
            var fullPath = ResolveProjectPath(fwDataPath);
            var loader = new FwDataProjectLoader();
            using var cache = loader.LoadScratchCache(fullPath);
            return (0, ManualAnalysisProjectionQuery.Read(cache), null);
        }
        catch (Exception ex)
        {
            return (1, null, FailText(ex.Message));
        }
    }

    public static CommandResult New(string storeDir, string draftName, string? label)
    {
        try
        {
            var store = new ProposalStore(storeDir);
            store.EnsureDirectoriesExist();
            var draftPath = store.DraftPath(draftName);

            if (File.Exists(draftPath))
            {
                return Fail(
                    $"Draft '{draftName}' already exists at '{draftPath}'. Finalize or delete it " +
                    "before creating a new draft with this name.");
            }

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

            WriteDraft(draftPath, draft);

            var sb = new StringBuilder();
            sb.AppendLine($"Created draft '{draftName}' at '{draftPath}'.");
            sb.AppendLine($"  proposalId: {draft.ProposalId}");
            if (label is not null)
                sb.AppendLine($"  label:       {label}");
            return Ok(sb);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public static CommandResult AddSetGloss(
        string storeDir, string draftName, string target, string ws, string text,
        IReadOnlyList<string>? dependsOn = null)
    {
        try
        {
            var store = new ProposalStore(storeDir);
            var draftPath = store.DraftPath(draftName);
            if (!File.Exists(draftPath))
                return Fail(DraftNotFoundMessage(store, draftName));

            if (!CanonicalId.TryParse(target, out var targetId, out var idError))
                return Fail($"--target '{target}' is not a valid canonical id: {idError}");

            if (string.IsNullOrEmpty(ws))
                return Fail("--ws must not be empty.");

            var draft = ReadDraft(draftPath);

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

            WriteDraft(draftPath, draft);

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
    }

    /// <summary>
    /// Adds a <c>lexical/lexEntry/deleteLexemeForm</c> operation: a real, already-lowered
    /// cascading-delete operation kind (<see cref="LexEntryLexemeFormOperationKinds.DeleteLexemeForm"/>),
    /// exposed here so the removal analysis has a genuine cascading-delete operation to test
    /// against, not a synthetic one. Its <c>after</c> payload is the empty object — an entry has at
    /// most one lexeme form, so nothing is left to disambiguate once the target entry is known.
    /// </summary>
    public static CommandResult AddDeleteLexemeForm(string storeDir, string draftName, string target)
    {
        try
        {
            var store = new ProposalStore(storeDir);
            var draftPath = store.DraftPath(draftName);
            if (!File.Exists(draftPath))
                return Fail(DraftNotFoundMessage(store, draftName));

            if (!CanonicalId.TryParse(target, out var targetId, out var idError))
                return Fail($"--target '{target}' is not a valid canonical id: {idError}");

            var draft = ReadDraft(draftPath);
            var operationId = CanonicalId.Mint();

            draft.Operations.Add(new DraftOperation
            {
                OperationId = operationId.Value,
                Kind = LexEntryLexemeFormOperationKinds.DeleteLexemeForm,
                Target = targetId.Value,
                After = new Dictionary<string, JsonElement>(),
            });
            EnsureContractVersion(draft, LexEntryLexemeFormOperationKinds.DeleteLexemeForm);

            WriteDraft(draftPath, draft);

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
        string storeDir, string draftName, string fwDataPath, string intentJson)
    {
        try
        {
            var store = new ProposalStore(storeDir);
            var draftPath = store.DraftPath(draftName);
            if (!File.Exists(draftPath))
                return Fail(DraftNotFoundMessage(store, draftName));

            using var intentDocument = JsonDocument.Parse(intentJson);
            var intent = AuthorLexemeFormIntentParser.Parse(intentDocument.RootElement);

            var fullPath = ResolveProjectPath(fwDataPath);
            var loader = new FwDataProjectLoader();
            IReadOnlyList<SIL.Motif.Contract.Model.OperationEnvelope> operations;
            using (var cache = loader.LoadCache(fullPath))
                operations = AuthorLexemeFormComposer.Build(cache, intent);

            var draft = ReadDraft(draftPath);
            foreach (var operation in operations)
            {
                draft.Operations.Add(ToDraftOperation(operation));
                EnsureContractVersion(draft, operation.Kind);
            }

            var provenanceJson = JsonSerializer.Serialize(
                new { composer = "AuthorLexemeForm", input = intentDocument.RootElement });
            using var provenanceDocument = JsonDocument.Parse(provenanceJson);
            draft.ComposerProvenance.Add(provenanceDocument.RootElement.Clone());

            WriteDraft(draftPath, draft);

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
    }

    /// <summary>
    /// Runs <see cref="AuthorFeatureStructureComposer"/> against a live project and appends the one
    /// operation it resolves to a draft — Motif's first grammar Layer-1 construct, alongside the
    /// lexical <see cref="ComposeAuthorLexemeForm"/>.
    /// </summary>
    /// <param name="intentJson"><c>{ "msa": "..." }</c> — see <see cref="AuthorFeatureStructureIntentParser"/>.</param>
    public static CommandResult ComposeAuthorFeatureStructure(
        string storeDir, string draftName, string fwDataPath, string intentJson)
    {
        try
        {
            var store = new ProposalStore(storeDir);
            var draftPath = store.DraftPath(draftName);
            if (!File.Exists(draftPath))
                return Fail(DraftNotFoundMessage(store, draftName));

            using var intentDocument = JsonDocument.Parse(intentJson);
            var intent = AuthorFeatureStructureIntentParser.Parse(intentDocument.RootElement);

            var fullPath = ResolveProjectPath(fwDataPath);
            var loader = new FwDataProjectLoader();
            IReadOnlyList<SIL.Motif.Contract.Model.OperationEnvelope> operations;
            using (var cache = loader.LoadCache(fullPath))
                operations = AuthorFeatureStructureComposer.Build(cache, intent);

            var draft = ReadDraft(draftPath);
            foreach (var operation in operations)
            {
                draft.Operations.Add(ToDraftOperation(operation));
                EnsureContractVersion(draft, operation.Kind);
            }

            var provenanceJson = JsonSerializer.Serialize(
                new { composer = "AuthorFeatureStructure", input = intentDocument.RootElement });
            using var provenanceDocument = JsonDocument.Parse(provenanceJson);
            draft.ComposerProvenance.Add(provenanceDocument.RootElement.Clone());

            WriteDraft(draftPath, draft);

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
    }

    /// <summary>
    /// Adds a <c>lexical/lexSense/setGloss</c> operation evidenced by a stored corpus — the only
    /// sanctioned route from the Motif store into the language project (ADR 0036 decision 2). The
    /// corpus's origin travels with the operation as non-hashed provenance, so a licence obligation a
    /// promoted value carries (e.g. CC-BY-SA attribution) is never lost between the evidence and the
    /// dictionary entry it justified.
    /// </summary>
    public static CommandResult PromoteGloss(
        string storeDir, string draftName, string target, string ws, string text,
        string corpusId, string? documentId = null)
    {
        try
        {
            var store = new ProposalStore(storeDir);
            var draftPath = store.DraftPath(draftName);
            if (!File.Exists(draftPath))
                return Fail(DraftNotFoundMessage(store, draftName));

            var corpus = CorpusCommands.StoreFor(storeDir).Load(corpusId);
            if (corpus is null)
            {
                return Fail(
                    $"Corpus '{corpusId}' not found in store '{storeDir}'. Run 'corpora' to see what is there.");
            }

            if (documentId is not null && corpus.Documents.All(d => d.DocumentId != documentId))
                return Fail($"Corpus '{corpusId}' has no document '{documentId}'.");

            if (!CanonicalId.TryParse(target, out var targetId, out var idError))
                return Fail($"--target '{target}' is not a valid canonical id: {idError}");

            if (string.IsNullOrEmpty(ws))
                return Fail("--ws must not be empty.");

            var draft = ReadDraft(draftPath);
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

            WriteDraft(draftPath, draft);

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

    public static CommandResult Label(string storeDir, string draftName, string text) =>
        SetDraftField(storeDir, draftName, "label", (draft) => draft.Label = text);

    public static CommandResult Comment(string storeDir, string draftName, string text) =>
        SetDraftField(storeDir, draftName, "comment", (draft) => draft.Comment = text);

    private static CommandResult SetDraftField(
        string storeDir, string draftName, string fieldName, Action<DraftDocument> setter)
    {
        try
        {
            var store = new ProposalStore(storeDir);
            var draftPath = store.DraftPath(draftName);
            if (!File.Exists(draftPath))
                return Fail(DraftNotFoundMessage(store, draftName));

            var draft = ReadDraft(draftPath);
            setter(draft);
            WriteDraft(draftPath, draft);

            var sb = new StringBuilder();
            sb.AppendLine($"Set {fieldName} on draft '{draftName}'.");
            return Ok(sb);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public static CommandResult Finalize(string storeDir, string draftName)
    {
        try
        {
            var store = new ProposalStore(storeDir);
            var draftPath = store.DraftPath(draftName);
            if (!File.Exists(draftPath))
                return Fail(DraftNotFoundMessage(store, draftName));

            var draft = ReadDraft(draftPath);
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

            store.EnsureDirectoriesExist();
            var objectPath = store.ObjectPath(intentDigest);
            var manifestPath = store.ManifestPath(draft.ProposalId);

            // Write-once: never overwrite an existing object; content-addressing makes revisiting one impossible anyway.
            if (!File.Exists(objectPath))
                File.WriteAllText(objectPath, proposalJson);

            // A manifest already present under this id means this draft is from reopen: re-finalizing is an amend.
            var isAmend = File.Exists(manifestPath);
            ManifestDocument manifest;
            if (isAmend)
            {
                manifest = ReadManifest(manifestPath);
                manifest.CurrentIntentDigest = intentDigest;
                // Approval is effect-digest-scoped: any content change invalidates it, so amend resets to proposed.
                manifest.Status = ManifestStatus.Proposed;
                manifest.Label = draft.Label ?? manifest.Label;
                manifest.Comment = draft.Comment ?? manifest.Comment;
                // The prior Anchor was bound to the previous content's digest; amend invalidates it too (ADR 0004 decision 3).
                manifest.Anchor = null;
                // Same reasoning as Anchor: a Decision is bound to content that no longer exists (ADR 0031 decision 3/4).
                manifest.Decision = null;
                manifest.SupersededBy = null;
            }
            else
            {
                manifest = new ManifestDocument
                {
                    ProposalId = draft.ProposalId,
                    Status = ManifestStatus.Proposed,
                    Label = draft.Label,
                    Comment = draft.Comment,
                    CurrentIntentDigest = intentDigest,
                };
            }
            WriteManifest(manifestPath, manifest);

            File.Delete(draftPath);

            var sb = new StringBuilder();
            if (isAmend)
            {
                sb.AppendLine($"Amended draft '{draftName}' -> Proposal {draft.ProposalId} (status: proposed).");
                sb.AppendLine("  (id unchanged; intentDigest moved to a new object; prior object version retained)");
            }
            else
            {
                sb.AppendLine($"Finalized draft '{draftName}' -> Proposal {draft.ProposalId} (status: proposed).");
            }
            sb.AppendLine($"  operations:   {envelope.Operations.Count}");
            sb.AppendLine($"  intentDigest: {intentDigest}");
            sb.AppendLine($"  object:       {objectPath}");
            sb.AppendLine($"  manifest:     {manifestPath}");
            return Ok(sb);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public static CommandResult Reopen(string storeDir, string draftName, string proposalId)
    {
        try
        {
            var store = new ProposalStore(storeDir);
            var draftPath = store.DraftPath(draftName);
            if (File.Exists(draftPath))
            {
                return Fail(
                    $"Draft '{draftName}' already exists at '{draftPath}'. Finalize or delete it " +
                    "before reopening a Proposal with this draft name.");
            }

            var id = NormalizeId(proposalId);
            var manifestPath = store.ManifestPath(id);
            if (!File.Exists(manifestPath))
                return Fail(ProposalNotFoundMessage(store, id));

            var manifest = ReadManifest(manifestPath);
            var objectPath = store.ObjectPath(manifest.CurrentIntentDigest);
            if (!File.Exists(objectPath))
                return Fail(StoreInconsistencyMessage(id, manifest.CurrentIntentDigest, objectPath));

            var envelope = ProposalJsonParser.Parse(File.ReadAllText(objectPath));

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

            store.EnsureDirectoriesExist();
            WriteDraft(draftPath, draft);

            var sb = new StringBuilder();
            sb.AppendLine($"Reopened Proposal {id} for editing as draft '{draftName}'.");
            sb.AppendLine($"  currentIntentDigest: {manifest.CurrentIntentDigest}");
            sb.AppendLine($"  operations:          {draft.Operations.Count}");
            sb.AppendLine(
                "Finalizing this draft will amend the Proposal: same id, new intentDigest, " +
                "status reset to proposed.");
            return Ok(sb);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static readonly string[] DeferrableFrom = { ManifestStatus.Proposed, ManifestStatus.Approved };
    private static readonly string[] ApprovableFrom = { ManifestStatus.Proposed, ManifestStatus.Deferred };
    private static readonly string[] RejectableFrom =
        { ManifestStatus.Proposed, ManifestStatus.Deferred, ManifestStatus.Approved };
    private static readonly string[] SupersedableFrom =
        { ManifestStatus.Proposed, ManifestStatus.Deferred, ManifestStatus.Approved, ManifestStatus.Rejected };

    /// <summary>Moves a Proposal to <c>deferred</c>: still wanted, not currently applicable (ADR 0031 decision 4).</summary>
    public static CommandResult Defer(string storeDir, string proposalId) =>
        TransitionStatus(storeDir, proposalId, ManifestStatus.Deferred, DeferrableFrom, manifest =>
        {
            manifest.Decision = null;
        });

    /// <summary>
    /// Records an <c>approved</c> Decision against a Proposal's exact current content. The actor type
    /// is never inferred — ADR 0031 decision 7 requires the record always show whether a human or an
    /// AI made the call.
    /// </summary>
    public static CommandResult Approve(
        string storeDir, string proposalId, string actorType, string actorId, string? comment = null) =>
        Decide(storeDir, proposalId, DecisionOutcome.Approved, ManifestStatus.Approved, ApprovableFrom,
            actorType, actorId, comment);

    /// <summary>Records a <c>rejected</c> Decision against a Proposal's exact current content.</summary>
    public static CommandResult Reject(
        string storeDir, string proposalId, string actorType, string actorId, string? comment = null) =>
        Decide(storeDir, proposalId, DecisionOutcome.Rejected, ManifestStatus.Rejected, RejectableFrom,
            actorType, actorId, comment);

    /// <summary>Marks a Proposal <c>superseded</c> by another, naming which one replaced it.</summary>
    public static CommandResult Supersede(string storeDir, string proposalId, string supersededByProposalId)
    {
        string supersededById;
        try
        {
            supersededById = NormalizeId(supersededByProposalId);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }

        return TransitionStatus(storeDir, proposalId, ManifestStatus.Superseded, SupersedableFrom, manifest =>
        {
            manifest.Decision = null;
            manifest.SupersededBy = supersededById;
        });
    }

    private static CommandResult Decide(
        string storeDir, string proposalId, string outcome, string newStatus, string[] allowedFrom,
        string actorType, string actorId, string? comment)
    {
        if (actorType != DecisionActorType.Human && actorType != DecisionActorType.Ai)
        {
            return Fail(
                $"actorType must be '{DecisionActorType.Human}' or '{DecisionActorType.Ai}' — an AI actor " +
                "must never be recorded as if it were a human, or the reverse (ADR 0031 decision 7).");
        }

        if (string.IsNullOrWhiteSpace(actorId))
            return Fail("actorId must not be empty — a Decision must name who made it.");

        return TransitionStatus(storeDir, proposalId, newStatus, allowedFrom, manifest =>
        {
            manifest.Decision = new Decision
            {
                Outcome = outcome,
                ActorType = actorType,
                ActorId = actorId,
                Comment = comment,
                BoundIntentDigest = manifest.CurrentIntentDigest,
                TimestampUtc = SIL.Motif.Model.AppliedLog.AppliedLogFormat.FormatTimestamp(DateTime.UtcNow),
            };
            manifest.SupersededBy = null;
        });
    }

    /// <summary>Moves a manifest to a new status, refusing if its current status is not an allowed origin.</summary>
    private static CommandResult TransitionStatus(
        string storeDir, string proposalId, string newStatus, string[] allowedFrom, Action<ManifestDocument> mutate)
    {
        try
        {
            var store = new ProposalStore(storeDir);
            var id = NormalizeId(proposalId);
            var manifestPath = store.ManifestPath(id);
            if (!File.Exists(manifestPath))
                return Fail(ProposalNotFoundMessage(store, id));

            var manifest = ReadManifest(manifestPath);
            if (Array.IndexOf(allowedFrom, manifest.Status) < 0)
            {
                return Fail(
                    $"Proposal {id} is '{manifest.Status}'; cannot move to '{newStatus}' from there. " +
                    $"Allowed from: {string.Join(", ", allowedFrom)}.");
            }

            mutate(manifest);
            manifest.Status = newStatus;
            WriteManifest(manifestPath, manifest);

            return Ok($"Proposal {id} is now '{newStatus}'.{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
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
    public static CommandResult Duplicate(string storeDir, string sourceProposalId, string newDraftName)
    {
        try
        {
            var store = new ProposalStore(storeDir);
            var draftPath = store.DraftPath(newDraftName);
            if (File.Exists(draftPath))
            {
                return Fail(
                    $"Draft '{newDraftName}' already exists at '{draftPath}'. Finalize or delete it " +
                    "before duplicating a Proposal into a draft with this name.");
            }

            var sourceId = NormalizeId(sourceProposalId);
            var manifestPath = store.ManifestPath(sourceId);
            if (!File.Exists(manifestPath))
                return Fail(ProposalNotFoundMessage(store, sourceId));

            var manifest = ReadManifest(manifestPath);
            var objectPath = store.ObjectPath(manifest.CurrentIntentDigest);
            if (!File.Exists(objectPath))
                return Fail(StoreInconsistencyMessage(sourceId, manifest.CurrentIntentDigest, objectPath));

            var envelope = ProposalJsonParser.Parse(File.ReadAllText(objectPath));

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

            store.EnsureDirectoriesExist();
            WriteDraft(draftPath, draft);

            var sb = new StringBuilder();
            sb.AppendLine($"Duplicated Proposal {sourceId} into new draft '{newDraftName}'.");
            sb.AppendLine($"  proposalId: {draft.ProposalId}  (a new Proposal; the source is untouched)");
            sb.AppendLine($"  operations: {draft.Operations.Count}");
            sb.AppendLine("Finalizing this draft will commit it as a brand-new Proposal.");
            return Ok(sb);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
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
        string storeDir, string draftName, IReadOnlyList<string> operationIds, bool force)
    {
        try
        {
            var store = new ProposalStore(storeDir);
            var draftPath = store.DraftPath(draftName);
            if (!File.Exists(draftPath))
                return Fail(DraftNotFoundMessage(store, draftName));

            if (operationIds.Count == 0)
                return Fail("Specify at least one operation id to remove.");

            var draft = ReadDraft(draftPath);

            var requestedIds = new List<string>();
            foreach (var raw in operationIds)
            {
                if (!CanonicalId.TryParse(raw, out var id, out var error))
                    return Fail($"'{raw}' is not a valid canonical operation id: {error}");
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
            WriteDraft(draftPath, draft);

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
        string storeDir, string sourceProposalId, IReadOnlyList<SplitGroup> groups, bool force)
    {
        try
        {
            if (groups.Count == 0)
                return Fail("Specify at least one group to split into.");

            var store = new ProposalStore(storeDir);
            var sourceId = NormalizeId(sourceProposalId);
            var manifestPath = store.ManifestPath(sourceId);
            if (!File.Exists(manifestPath))
                return Fail(ProposalNotFoundMessage(store, sourceId));

            var manifest = ReadManifest(manifestPath);
            var objectPath = store.ObjectPath(manifest.CurrentIntentDigest);
            if (!File.Exists(objectPath))
                return Fail(StoreInconsistencyMessage(sourceId, manifest.CurrentIntentDigest, objectPath));

            var envelope = ProposalJsonParser.Parse(File.ReadAllText(objectPath));
            var sourceOperations = envelope.Operations.Select(ToDraftOperation).ToList();
            var allIds = sourceOperations.Select(o => o.OperationId).ToList();

            foreach (var draftName in groups.Select(g => g.DraftName))
            {
                if (File.Exists(store.DraftPath(draftName)))
                {
                    return Fail(
                        $"Draft '{draftName}' already exists at '{store.DraftPath(draftName)}'. Finalize or " +
                        "delete it before splitting into a draft with this name.");
                }
            }
            if (groups.Select(g => g.DraftName).Distinct(StringComparer.Ordinal).Count() != groups.Count)
                return Fail("Each split group must target a distinct draft name.");

            // Validate the groups partition every source operation exactly once.
            var groupOfId = new Dictionary<string, string>(StringComparer.Ordinal);
            var duplicates = new List<string>();
            var unknown = new List<string>();
            foreach (var group in groups)
            {
                foreach (var rawId in group.OperationIds)
                {
                    if (!CanonicalId.TryParse(rawId, out var id, out var idError))
                        return Fail($"'{rawId}' is not a valid canonical operation id: {idError}");

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

            store.EnsureDirectoriesExist();
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
                WriteDraft(store.DraftPath(group.DraftName), draft);

                sb2.AppendLine($"  draft '{group.DraftName}': proposalId={draft.ProposalId} operations={draft.Operations.Count}");
            }

            sb2.AppendLine($"  (the source Proposal {sourceId} is unchanged)");
            sb2.AppendLine("Finalize each draft to commit it as a brand-new Proposal.");
            return Ok(sb2);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static string DescribeOperationIds(IReadOnlyList<string> ids) =>
        ids.Count == 1 ? $"operation '{ids[0]}'" : $"operations {string.Join(", ", ids.Select(i => $"'{i}'"))}";

    public static CommandResult List(string storeDir, UsageLog? usage = null)
    {
        usage?.Record("list", new[] { UsageArgumentShape.Text("storeDir") });
        var (exitCode, projection, error) = BuildProposalList(storeDir);
        return projection is not null ? Ok(CommandTextRenderer.Render(projection)) : new CommandResult(exitCode, error!);
    }

    /// <summary>The <c>list</c> report as JSON — the same <see cref="ProposalListProjection"/> <see cref="List"/> renders as text.</summary>
    public static CommandResult ListJson(string storeDir, UsageLog? usage = null)
    {
        usage?.Record("list", new[] { UsageArgumentShape.Text("storeDir") });
        var (exitCode, projection, error) = BuildProposalList(storeDir);
        return projection is not null ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
            : new CommandResult(exitCode, error!);
    }

    private static (int ExitCode, ProposalListProjection? Projection, string? Error) BuildProposalList(string storeDir)
    {
        try
        {
            var store = new ProposalStore(storeDir);
            if (!Directory.Exists(store.ManifestsDirectory))
                return (0, new ProposalListProjection(Array.Empty<ProposalListItem>()), null);

            var manifests = Directory.GetFiles(store.ManifestsDirectory, "*.json")
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(ReadManifest)
                .ToList();

            return (0, ProposalListProjectionBuilder.Build(manifests), null);
        }
        catch (Exception ex)
        {
            return (1, null, FailText(ex.Message));
        }
    }

    public static CommandResult Show(string storeDir, string proposalId, UsageLog? usage = null)
    {
        usage?.Record("show", new[] { UsageArgumentShape.Text("storeDir"), UsageArgumentShape.Text("proposalId") });
        var (exitCode, projection, error) = BuildProposalDetail(storeDir, proposalId);
        return projection is not null ? Ok(CommandTextRenderer.Render(projection)) : new CommandResult(exitCode, error!);
    }

    /// <summary>The <c>show</c> report as JSON — the same <see cref="ProposalDetailProjection"/> <see cref="Show"/> renders as text.</summary>
    public static CommandResult ShowJson(string storeDir, string proposalId, UsageLog? usage = null)
    {
        usage?.Record("show", new[] { UsageArgumentShape.Text("storeDir"), UsageArgumentShape.Text("proposalId") });
        var (exitCode, projection, error) = BuildProposalDetail(storeDir, proposalId);
        return projection is not null ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
            : new CommandResult(exitCode, error!);
    }

    private static (int ExitCode, ProposalDetailProjection? Projection, string? Error) BuildProposalDetail(
        string storeDir, string proposalId)
    {
        try
        {
            var store = new ProposalStore(storeDir);
            var id = NormalizeId(proposalId);
            var manifestPath = store.ManifestPath(id);
            if (!File.Exists(manifestPath))
                return (1, null, FailText(ProposalNotFoundMessage(store, id)));

            var manifest = ReadManifest(manifestPath);
            var objectPath = store.ObjectPath(manifest.CurrentIntentDigest);
            if (!File.Exists(objectPath))
                return (1, null, FailText(StoreInconsistencyMessage(id, manifest.CurrentIntentDigest, objectPath)));

            var envelope = ProposalJsonParser.Parse(File.ReadAllText(objectPath));
            return (0, ProposalDetailProjectionBuilder.Build(id, manifest, envelope), null);
        }
        catch (Exception ex)
        {
            return (1, null, FailText(ex.Message));
        }
    }

    public static CommandResult DryRun(string storeDir, string proposalId, string fwDataPath, UsageLog? usage = null)
    {
        RecordDryRunUsage(usage, "storeDir", "proposalId", "fwDataPath");
        var (exitCode, projection, error) = BuildFileDryRunProjection(storeDir, proposalId, fwDataPath);
        if (projection is null) return new CommandResult(exitCode, error!);

        var sb = new StringBuilder(CommandTextRenderer.Render(projection));
        // State the side effect: a dry run reads as "nothing happens", but it saved the project first (ADR 0016).
        sb.AppendLine("  (the project was saved before measuring, so the scratch copy matched it; " +
                      "the project itself was not modified by this dry run)");
        return Ok(sb);
    }

    /// <summary>The <c>dry-run</c> report as JSON — the same <see cref="DryRunProjection"/> <see cref="DryRun(string,string,string,UsageLog)"/> renders as text.</summary>
    public static CommandResult DryRunJson(string storeDir, string proposalId, string fwDataPath, UsageLog? usage = null)
    {
        RecordDryRunUsage(usage, "storeDir", "proposalId", "fwDataPath");
        var (exitCode, projection, error) = BuildFileDryRunProjection(storeDir, proposalId, fwDataPath);
        return projection is not null ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
            : new CommandResult(exitCode, error!);
    }

    private static (int ExitCode, DryRunProjection? Projection, string? Error) BuildFileDryRunProjection(
        string storeDir, string proposalId, string fwDataPath)
    {
        LcmCache? cache = null;
        DryRunScratch? scratch = null;
        try
        {
            var store = new ProposalStore(storeDir);
            var id = NormalizeId(proposalId);
            var manifestPath = store.ManifestPath(id);
            if (!File.Exists(manifestPath))
                return (1, null, FailText(ProposalNotFoundMessage(store, id)));

            var manifest = ReadManifest(manifestPath);
            var objectPath = store.ObjectPath(manifest.CurrentIntentDigest);
            if (!File.Exists(objectPath))
                return (1, null, FailText(StoreInconsistencyMessage(id, manifest.CurrentIntentDigest, objectPath)));

            var envelope = ProposalJsonParser.Parse(File.ReadAllText(objectPath));

            var fullFwDataPath = ResolveProjectPath(fwDataPath);
            var loader = new FwDataProjectLoader();

            // Hold the project open throughout: one writer at a time (ADR 0006 decision 4), or the anchor means nothing.
            cache = loader.LoadCache(fullFwDataPath);

            // Save before the copy: the scratch copies the FILE, so an uncommitted edit is invisible to it (ADR 0016).
            loader.Save(cache);

            // Mutate a throwaway copy and delete it: no rollback, so nothing leaves derived caches stale.
            var scratchRoot = Path.Combine(
                Path.GetTempPath(), "SIL.Motif.DryRun", Guid.NewGuid().ToString("N"));
            scratch = DryRunScratch.Adopt(
                new ScratchCacheFactory(loader).CreateFromFileCopy(fullFwDataPath, scratchRoot),
                $"file copy of {fullFwDataPath}",
                onDisposed: () => TryDeleteDirectory(scratchRoot));

            var dryRun = ProposalDryRunner.Run(scratch, envelope);

            // Persist the bound-DryRun anchor (docs/adr/0004 decision 3): apply requires it present and unmoved.
            manifest.Anchor = dryRun.Anchor;
            WriteManifest(manifestPath, manifest);

            return (0, DryRunProjectionBuilder.Build(id, dryRun), null);
        }
        catch (LcmFileLockedException)
        {
            return (1, null, FailText(ProjectInUseMessage(fwDataPath, "dry-run")));
        }
        catch (Exception ex)
        {
            return (1, null, FailText(ex.Message));
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
    public static CommandResult Apply(string storeDir, string proposalId, string fwDataPath, string user, UsageLog? usage = null)
    {
        RecordApplyUsage(usage, "storeDir", "proposalId", "fwDataPath", "user");
        var (exitCode, projection, error) = BuildFileApplyProjection(storeDir, proposalId, fwDataPath, user);
        return projection is not null ? Ok(CommandTextRenderer.Render(projection)) : new CommandResult(exitCode, error!);
    }

    /// <summary>The <c>apply</c> report as JSON — the same <see cref="ApplyProjection"/> <see cref="Apply(string,string,string,string,UsageLog)"/> renders as text.</summary>
    public static CommandResult ApplyJson(string storeDir, string proposalId, string fwDataPath, string user, UsageLog? usage = null)
    {
        RecordApplyUsage(usage, "storeDir", "proposalId", "fwDataPath", "user");
        var (exitCode, projection, error) = BuildFileApplyProjection(storeDir, proposalId, fwDataPath, user);
        return projection is not null ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
            : new CommandResult(exitCode, error!);
    }

    private static (int ExitCode, ApplyProjection? Projection, string? Error) BuildFileApplyProjection(
        string storeDir, string proposalId, string fwDataPath, string user)
    {
        LcmCache? cache = null;
        try
        {
            var store = new ProposalStore(storeDir);
            var id = NormalizeId(proposalId);
            var manifestPath = store.ManifestPath(id);
            if (!File.Exists(manifestPath))
                return (1, null, FailText(ProposalNotFoundMessage(store, id)));

            var manifest = ReadManifest(manifestPath);
            var objectPath = store.ObjectPath(manifest.CurrentIntentDigest);
            if (!File.Exists(objectPath))
                return (1, null, FailText(StoreInconsistencyMessage(id, manifest.CurrentIntentDigest, objectPath)));

            // ADR 0004 decision 3: a bare apply with no bound DryRun is a hard error, checked before loading the project.
            if (manifest.Anchor is null)
            {
                return (1, null, FailText(
                    $"Proposal {id} has no bound DryRun recorded. Run " +
                    $"'dry-run {id} --project <fwdata>' first, then 'apply'."));
            }

            var envelope = ProposalJsonParser.Parse(File.ReadAllText(objectPath));

            var fullFwDataPath = ResolveProjectPath(fwDataPath);
            var loader = new FwDataProjectLoader();
            cache = loader.LoadCache(fullFwDataPath);
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
                    manifest.Status = ManifestStatus.Applied;
                    WriteManifest(manifestPath, manifest);
                }
                catch (Exception ex)
                {
                    throw new NeedsReconciliationException(
                        ReconciliationBoundary.ReceiptRecording,
                        $"Proposal {id} was applied and saved to the project, but recording that in the " +
                        $"proposal store failed: manifest '{manifestPath}' was not updated. The project " +
                        "and the store now disagree about whether this Proposal is applied. Do not retry " +
                        "automatically -- inspect the manifest before doing anything else with it.",
                        ex);
                }

                return (0, ApplyProjectionBuilder.Build(id, receipt), null);
            }
            finally
            {
                if (cache is { IsDisposed: false }) cache.Dispose();
            }
        }
        catch (LcmFileLockedException)
        {
            return (1, null, FailText(ProjectInUseMessage(fwDataPath, "apply")));
        }
        catch (NeedsReconciliationException ex)
        {
            // Distinct from the rollback wording below: the mutation may already be durable.
            return (1, null, FailText(ex.Message));
        }
        catch (Exception ex)
        {
            // A failed apply rolled back, not Undo: derived caches may be stale (ADR 0016) -- see the remarks above.
            return (1, null, FailText(
                ex.Message +
                " [This LcmCache is no longer trustworthy: a failed apply rolls back, which does not " +
                "refresh LibLCM's derived caches. Discard it and reload the project.]"));
        }
    }

    private static void RecordApplyUsage(UsageLog? usage, params string[] names) =>
        usage?.Record("apply", names.Select(UsageArgumentShape.Text).ToList());

    /// <summary>
    /// Session-backed counterpart to <see cref="DryRun(string,string,string,UsageLog)"/>: dry-runs against
    /// <paramref name="session"/>'s already-open live cache and footprint-gated pristine scratch
    /// instead of loading and disposing a fresh <see cref="LcmCache"/> per call, so N calls against one
    /// session cost at most one live project load (<see cref="CliSession.Open"/>) plus footprint-gated
    /// scratch rebuilds (<see cref="CliSession.PristineRebuildCount"/>).
    /// </summary>
    public static CommandResult DryRun(CliSession session, string storeDir, string proposalId, UsageLog? usage = null)
    {
        RecordDryRunUsage(usage, "storeDir", "proposalId");
        var (exitCode, projection, error) = BuildSessionDryRunProjection(session, storeDir, proposalId);
        return projection is not null ? Ok(CommandTextRenderer.Render(projection)) : new CommandResult(exitCode, error!);
    }

    /// <summary>The <c>dry-run</c> report as JSON, session-backed — see <see cref="DryRun(CliSession,string,string,UsageLog)"/>.</summary>
    public static CommandResult DryRunJson(CliSession session, string storeDir, string proposalId, UsageLog? usage = null)
    {
        RecordDryRunUsage(usage, "storeDir", "proposalId");
        var (exitCode, projection, error) = BuildSessionDryRunProjection(session, storeDir, proposalId);
        return projection is not null ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
            : new CommandResult(exitCode, error!);
    }

    private static (int ExitCode, DryRunProjection? Projection, string? Error) BuildSessionDryRunProjection(
        CliSession session, string storeDir, string proposalId)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        try
        {
            if (!TryLoadProposalForRun(
                    storeDir, proposalId, out var manifest, out var manifestPath, out var id,
                    out var envelope, out var failure))
            {
                return (failure!.ExitCode, null, failure.Output);
            }

            var dryRun = session.DryRun(envelope);

            // Persist the bound-DryRun anchor (docs/adr/0004 decision 3): apply requires it present and unmoved.
            manifest.Anchor = dryRun.Anchor;
            WriteManifest(manifestPath, manifest);

            return (0, DryRunProjectionBuilder.Build(id, dryRun), null);
        }
        catch (Exception ex)
        {
            return (1, null, FailText(ex.Message));
        }
    }

    /// <summary>
    /// Session-backed counterpart to <see cref="Apply(string,string,string,string,UsageLog)"/>: applies against
    /// <paramref name="session"/>'s live cache and saves through it, rather than loading and disposing a
    /// fresh <see cref="LcmCache"/>. On failure <see cref="CliSession.Apply"/> has already discarded the
    /// session's live cache (ADR 0016); the caller must open a new session rather than reuse this one.
    /// </summary>
    public static CommandResult Apply(CliSession session, string storeDir, string proposalId, string user, UsageLog? usage = null)
    {
        RecordApplyUsage(usage, "storeDir", "proposalId", "user");
        var (exitCode, projection, error) = BuildSessionApplyProjection(session, storeDir, proposalId, user);
        return projection is not null ? Ok(CommandTextRenderer.Render(projection)) : new CommandResult(exitCode, error!);
    }

    /// <summary>The <c>apply</c> report as JSON, session-backed — see <see cref="Apply(CliSession,string,string,string,UsageLog)"/>.</summary>
    public static CommandResult ApplyJson(CliSession session, string storeDir, string proposalId, string user, UsageLog? usage = null)
    {
        RecordApplyUsage(usage, "storeDir", "proposalId", "user");
        var (exitCode, projection, error) = BuildSessionApplyProjection(session, storeDir, proposalId, user);
        return projection is not null ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
            : new CommandResult(exitCode, error!);
    }

    // On failure the session already discarded its live cache (ADR 0016); the caller must open a new one.
    private static (int ExitCode, ApplyProjection? Projection, string? Error) BuildSessionApplyProjection(
        CliSession session, string storeDir, string proposalId, string user)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        try
        {
            if (!TryLoadProposalForRun(
                    storeDir, proposalId, out var manifest, out var manifestPath, out var id,
                    out var envelope, out var failure))
            {
                return (failure!.ExitCode, null, failure.Output);
            }

            // ADR 0004 decision 3: a bare apply with no bound DryRun is a hard error, checked before applying.
            if (manifest.Anchor is null)
            {
                return (1, null, FailText(
                    $"Proposal {id} has no bound DryRun recorded. Run " +
                    $"'dry-run {id} --project <fwdata>' first, then 'apply'."));
            }

            var description = manifest.Label ?? "";
            var receipt = session.Apply(envelope, manifest.Anchor, user, description);

            try
            {
                manifest.Status = ManifestStatus.Applied;
                WriteManifest(manifestPath, manifest);
            }
            catch (Exception ex)
            {
                throw new NeedsReconciliationException(
                    ReconciliationBoundary.ReceiptRecording,
                    $"Proposal {id} was applied and saved to the project, but recording that in the " +
                    $"proposal store failed: manifest '{manifestPath}' was not updated. The project and " +
                    "the store now disagree about whether this Proposal is applied. Do not retry " +
                    "automatically -- inspect the manifest before doing anything else with it.",
                    ex);
            }

            return (0, ApplyProjectionBuilder.Build(id, receipt), null);
        }
        catch (NeedsReconciliationException ex)
        {
            // Distinct from the rollback wording below: the mutation may already be durable.
            return (1, null, FailText(ex.Message));
        }
        catch (Exception ex)
        {
            // A failed apply rolled back, not Undo: the session already discarded its live cache above.
            return (1, null, FailText(
                ex.Message +
                " [This session's live cache is no longer trustworthy: a failed apply rolls back, which " +
                "does not refresh LibLCM's derived caches. Open a new session rather than reuse this one.]"));
        }
    }

    /// <summary>Loads the manifest and Proposal envelope a dry-run/apply needs, or the failure to return.</summary>
    private static bool TryLoadProposalForRun(
        string storeDir, string proposalId,
        out ManifestDocument manifest, out string manifestPath, out string normalizedId,
        out SIL.Motif.Contract.Model.Proposal envelope, out CommandResult? failure)
    {
        var store = new ProposalStore(storeDir);
        normalizedId = NormalizeId(proposalId);
        manifestPath = store.ManifestPath(normalizedId);
        if (!File.Exists(manifestPath))
        {
            manifest = null!;
            envelope = null!;
            failure = Fail(ProposalNotFoundMessage(store, normalizedId));
            return false;
        }

        manifest = ReadManifest(manifestPath);
        var objectPath = store.ObjectPath(manifest.CurrentIntentDigest);
        if (!File.Exists(objectPath))
        {
            envelope = null!;
            failure = Fail(StoreInconsistencyMessage(normalizedId, manifest.CurrentIntentDigest, objectPath));
            return false;
        }

        envelope = ProposalJsonParser.Parse(File.ReadAllText(objectPath));
        failure = null;
        return true;
    }

    public static CommandResult Log(string fwDataPath, UsageLog? usage = null)
    {
        usage?.Record("log", new[] { UsageArgumentShape.Text("fwDataPath") });
        var (exitCode, projection, error) = BuildAppliedLog(fwDataPath);
        return projection is not null ? Ok(CommandTextRenderer.Render(projection)) : new CommandResult(exitCode, error!);
    }

    /// <summary>The <c>log</c> report as JSON — the same <see cref="AppliedLogProjection"/> <see cref="Log(string,UsageLog)"/> renders as text.</summary>
    public static CommandResult LogJson(string fwDataPath, UsageLog? usage = null)
    {
        usage?.Record("log", new[] { UsageArgumentShape.Text("fwDataPath") });
        var (exitCode, projection, error) = BuildAppliedLog(fwDataPath);
        return projection is not null ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
            : new CommandResult(exitCode, error!);
    }

    private static (int ExitCode, AppliedLogProjection? Projection, string? Error) BuildAppliedLog(string fwDataPath)
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

            return (0, AppliedLogProjectionBuilder.Build(fullFwDataPath, entries, diagnostics), null);
        }
        catch (Exception ex)
        {
            return (1, null, FailText(ex.Message));
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

    private static string DraftNotFoundMessage(ProposalStore store, string draftName) =>
        $"Draft '{draftName}' not found in store '{store.RootDirectory}'. Run 'new --draft {draftName}' first.";

    private static string ProposalNotFoundMessage(ProposalStore store, string id) =>
        $"Proposal '{id}' not found in store '{store.RootDirectory}'. Run 'list' to see committed proposals.";

    private static string StoreInconsistencyMessage(string id, string currentIntentDigest, string objectPath) =>
        $"Proposal '{id}' manifest points at intentDigest '{currentIntentDigest}', but no object " +
        $"exists at '{objectPath}' (store inconsistency).";

    private static DraftDocument ReadDraft(string path) =>
        JsonSerializer.Deserialize<DraftDocument>(File.ReadAllText(path), DraftJsonOptions)
        ?? throw new InvalidOperationException($"Draft file '{path}' is empty or invalid.");

    private static void WriteDraft(string path, DraftDocument draft) =>
        File.WriteAllText(path, JsonSerializer.Serialize(draft, DraftJsonOptions));

    private static ManifestDocument ReadManifest(string path) =>
        JsonSerializer.Deserialize<ManifestDocument>(File.ReadAllText(path), DraftJsonOptions)
        ?? throw new InvalidOperationException($"Manifest file '{path}' is empty or invalid.");

    private static void WriteManifest(string path, ManifestDocument manifest) =>
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, DraftJsonOptions));

    private static CommandResult Ok(StringBuilder sb) => new(0, sb.ToString());

    private static CommandResult Ok(string text) => new(0, text);

    private static CommandResult Fail(string message) => new(1, FailText(message));

    // Same rendering as Fail, for a Build* helper returning a bare string rather than a CommandResult.
    private static string FailText(string message) => "error: " + message + Environment.NewLine;
}
