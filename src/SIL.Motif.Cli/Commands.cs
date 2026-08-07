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
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Model.Effects;
using SIL.Motif.Runner.Apply;
using SIL.Motif.Runner.AppliedLog;
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
/// This class never re-implements dry-run/apply/log semantics: it calls
/// <see cref="ProposalDryRunner.Run"/>, <see cref="ProposalApplier.Apply"/>,
/// <see cref="ProjectAppliedLog.ReadAll"/>, and <see cref="FwDataProjectLoader"/> exactly as Stages
/// C/D/A left them.
/// </remarks>
public static class Commands
{
    static Commands()
    {
        // SIL.Motif.Runner.Operations.LexicalSenseOperationKinds registers "lexical/lexSense/setGloss"
        // with the Contract kernel's OperationKindRegistry via a [ModuleInitializer] (see that
        // type's remarks) — but a module initializer only runs once the CLR actually loads the
        // Runner assembly, and every call site in this class that names SetGloss uses the *const*
        // field, which the C# compiler inlines as a literal string at compile time. That means
        // building a draft's operation JSON and finalizing it (which only ever touches Contract's
        // ProposalJsonParser) would never otherwise force the Runner assembly to load, so
        // "Unknown operation kind" would fire even though this same CLI process later calls
        // ProposalDryRunner/ProposalApplier successfully. Forcing the module constructor here,
        // once, up front, makes registration independent of which command runs first.
        RuntimeHelpers.RunModuleConstructor(typeof(LexicalSenseOperationKinds).Module.ModuleHandle);
    }

    private static readonly JsonSerializerOptions DraftJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // ManifestDocument.Anchor is a nested BoundDryRunAnchor record: System.Text.Json matches
        // JSON properties to that record's positional-constructor parameters, and case-insensitive
        // matching makes that robust regardless of exactly how constructor-parameter-name matching
        // interacts with PropertyNamingPolicy across runtimes.
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions ProposalJsonOptions = new()
    {
        WriteIndented = true,
        // entityId is absent on most operations and DraftOperation models that as null; omit it
        // entirely rather than writing a JSON null, since ProposalJsonParser.ParseOptionalId treats
        // a present-but-null property as a type error ("must be a string"), not "absent".
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static CommandResult Open(string fwDataPath)
    {
        try
        {
            var fullPath = ResolveProjectPath(fwDataPath);
            var loader = new FwDataProjectLoader();
            using var cache = loader.LoadCache(fullPath);

            var entryRepo = cache.ServiceLocator.GetInstance<ILexEntryRepository>();
            var sb = new StringBuilder();
            sb.AppendLine($"Project: {cache.ProjectId.Name}");
            sb.AppendLine($"Lexical entries: {entryRepo.Count}");
            return Ok(sb);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
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
                ContractVersions = new Dictionary<string, string> { ["lexical"] = "1.0" },
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
                After = new Dictionary<string, string> { ["ws"] = ws, ["text"] = text },
            });

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
    /// exposed here so MOT-18's removal analysis has a genuine cascading-delete operation to test
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
                After = new Dictionary<string, string>(),
            });

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

    /// <summary>Validates that every id in <paramref name="dependsOn"/> is a canonical id already
    /// present as an operation in <paramref name="draft"/> — the natural authoring order (the
    /// dependency must already exist to be depended on).</summary>
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

            // Write-once: never overwrite an existing object (this exact content may already be
            // committed, e.g. a no-op re-finalize), and never revisit/mutate one afterward — the
            // content-addressed key makes that impossible by construction anyway, since any edit
            // changes the digest and therefore the path.
            if (!File.Exists(objectPath))
                File.WriteAllText(objectPath, proposalJson);

            // A manifest already present under this frozen proposalId means this draft came from
            // `reopen`: re-finalizing it is an amend (docs/stage2-change-management.md, S1/S3) —
            // same id, a new object, the manifest's pointer moved, not created. Prior object
            // versions are retained (never deleted or revisited).
            var isAmend = File.Exists(manifestPath);
            ManifestDocument manifest;
            if (isAmend)
            {
                manifest = ReadManifest(manifestPath);
                manifest.CurrentIntentDigest = intentDigest;
                // Approval is effect-digest-scoped: any content change invalidates it, so amend
                // always resets to proposed regardless of the pre-amend status.
                manifest.Status = ManifestStatus.Proposed;
                manifest.Label = draft.Label ?? manifest.Label;
                manifest.Comment = draft.Comment ?? manifest.Comment;
                // The prior Anchor was bound to the PREVIOUS content's footprint/effect digest; an
                // amend invalidates it just as it invalidates approval (ADR 0004, decision 3).
                manifest.Anchor = null;
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

            // Loads the committed envelope's current content into a NEW draft carrying the SAME
            // frozen proposalId (docs/stage2-change-management.md, S3, "Reopen for editing").
            // Re-committing (finalize) produces a new intentDigest under that id — an amend.
            var draft = new DraftDocument
            {
                ProposalId = id,
                ContractVersions = new Dictionary<string, string>(envelope.ContractVersions),
                Requires = envelope.Requires.Select(r => r.Value).ToList(),
                Label = manifest.Label,
                Comment = manifest.Comment,
                Operations = envelope.Operations.Select(ToDraftOperation).ToList(),
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

        var afterDict = JsonSerializer.Deserialize<Dictionary<string, string>>(after.GetRawText())
            ?? new Dictionary<string, string>();

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
    /// <see cref="Reopen"/>, which keeps the source id and produces an amend). See MOT-18
    /// ("duplicate, remove, split").
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
    /// <see cref="Reopen"/>), applying ADR 0021 decision 6 (<c>J43</c>): a removal with no dependents
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

            // Decision 6, point 4: force never means "guess". A cascading delete's true reach is
            // discovered-only (LibLCM's ownership cascade), never declared in the Proposal, so no
            // consequence set for removing it can be honestly stated — refuse outright.
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

            // Force accepts the full enumerated set: the requested operations AND every transitive
            // dependent named above. Leaving a dependent behind with a dangling dependsOn/target
            // would silently produce an inconsistent Proposal; removing it too is the honest reading
            // of "the caller accepted this consequence set."
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
    /// its own freshly minted <c>proposalId</c> (<c>J44</c>: the unit of splitting is the individual
    /// operation, subject to <c>requires</c>/<c>dependsOn</c>). <paramref name="groups"/> must
    /// partition every operation in the source exactly once. If a declared dependency (<c>dependsOn</c>
    /// or a <c>target</c> naming another operation's <c>entityId</c>) would be severed by landing its
    /// two ends in different groups, that is named as a consequence and requires
    /// <paramref name="force"/> — the same warn/enumerate/force rule as <see cref="RemoveOperations"/>
    /// (decision 6), because nothing here is discovered-only: every edge is declared in the source
    /// Proposal, so this never hits the "cannot be enumerated" refusal.
    /// </summary>
    /// <remarks>
    /// The source Proposal is left exactly as it was — split does not supersede or discard it. MOT-10
    /// (not yet built) is what would define a "superseded" status transition; deciding that here would
    /// be silently picking an answer the plan does not commit to, so this intentionally does not.
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

    public static CommandResult List(string storeDir)
    {
        try
        {
            var store = new ProposalStore(storeDir);
            var sb = new StringBuilder();

            if (!Directory.Exists(store.ManifestsDirectory))
            {
                sb.AppendLine("No proposals in store.");
                return Ok(sb);
            }

            var manifestFiles = Directory.GetFiles(store.ManifestsDirectory, "*.json")
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            if (manifestFiles.Count == 0)
            {
                sb.AppendLine("No proposals in store.");
                return Ok(sb);
            }

            foreach (var file in manifestFiles)
            {
                var manifest = ReadManifest(file);
                sb.AppendLine($"{manifest.ProposalId}  {manifest.Status,-8}  {manifest.Label}");
            }
            return Ok(sb);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public static CommandResult Show(string storeDir, string proposalId)
    {
        try
        {
            var store = new ProposalStore(storeDir);
            var id = NormalizeId(proposalId);
            var manifestPath = store.ManifestPath(id);
            if (!File.Exists(manifestPath))
                return Fail(ProposalNotFoundMessage(store, id));

            var manifest = ReadManifest(manifestPath);
            var objectPath = store.ObjectPath(manifest.CurrentIntentDigest);
            if (!File.Exists(objectPath))
                return Fail(StoreInconsistencyMessage(id, manifest.CurrentIntentDigest, objectPath));

            var objectJson = File.ReadAllText(objectPath);

            var sb = new StringBuilder();
            sb.AppendLine($"Proposal {id}");
            sb.AppendLine($"  status:              {manifest.Status}");
            sb.AppendLine($"  label:               {manifest.Label}");
            sb.AppendLine($"  comment:             {manifest.Comment}");
            sb.AppendLine($"  currentIntentDigest: {manifest.CurrentIntentDigest}");
            sb.AppendLine();
            sb.AppendLine(objectJson.TrimEnd());
            return Ok(sb);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public static CommandResult DryRun(string storeDir, string proposalId, string fwDataPath)
    {
        LcmCache? cache = null;
        DryRunScratch? scratch = null;
        try
        {
            var store = new ProposalStore(storeDir);
            var id = NormalizeId(proposalId);
            var manifestPath = store.ManifestPath(id);
            if (!File.Exists(manifestPath))
                return Fail(ProposalNotFoundMessage(store, id));

            var manifest = ReadManifest(manifestPath);
            var objectPath = store.ObjectPath(manifest.CurrentIntentDigest);
            if (!File.Exists(objectPath))
                return Fail(StoreInconsistencyMessage(id, manifest.CurrentIntentDigest, objectPath));

            var envelope = ProposalJsonParser.Parse(File.ReadAllText(objectPath));

            var fullFwDataPath = ResolveProjectPath(fwDataPath);
            var loader = new FwDataProjectLoader();

            // Open the live project and hold it open for the whole command. Motif-as-CLI is a
            // FieldWorks-class writer of this project, not a bystander reading around one: there is
            // exactly one writer at a time, and while Motif has the project, FieldWorks must not
            // (ADR 0006 decision 4). Holding it here is what makes the anchor mean anything — a
            // baseline measured while someone else could still be editing is not a baseline.
            cache = loader.LoadCache(fullFwDataPath);

            // Save before the copy, not before the apply (ADR 0016 as amended 2026-08-06). The
            // scratch is a copy of the FILE, so any uncommitted edit in this cache would be invisible
            // to it and the resulting anchor would describe a state the live cache is not in — which
            // Apply would then report as drift that never happened. Nothing is pending on a
            // freshly-opened project, so this is cheap here; it is written out anyway because the
            // FieldWorks host will run the identical sequence with real in-flight edits behind it.
            loader.Save(cache);

            // Mutate a throwaway copy and delete it: no rollback anywhere, so nothing can leave this
            // cache's derived caches stale.
            var scratchRoot = Path.Combine(
                Path.GetTempPath(), "SIL.Motif.DryRun", Guid.NewGuid().ToString("N"));
            scratch = DryRunScratch.Adopt(
                new ScratchCacheFactory(loader).CreateFromFileCopy(fullFwDataPath, scratchRoot),
                $"file copy of {fullFwDataPath}",
                onDisposed: () => TryDeleteDirectory(scratchRoot));

            var dryRun = ProposalDryRunner.Run(scratch, envelope);

            // Persist the bound-DryRun anchor (docs/adr/0004, decision 3): `apply` requires
            // this to be present and unmoved before it will touch the project.
            manifest.Anchor = dryRun.Anchor;
            WriteManifest(manifestPath, manifest);

            var sb = new StringBuilder();
            sb.AppendLine($"DryRun of Proposal {id}");
            sb.AppendLine($"  intentDigest: {dryRun.IntentDigest}");
            sb.AppendLine($"  baseline:     {dryRun.BaselineNote}");
            sb.AppendLine($"  effects ({dryRun.ExpectedEffects.Count}):");
            foreach (var effect in dryRun.ExpectedEffects)
                AppendEffect(sb, effect);
            sb.AppendLine($"  effectDigest: {dryRun.EffectDigest}");
            sb.AppendLine($"  footprintDigest: {dryRun.Anchor.FootprintDigest}");
            sb.AppendLine("  (bound-DryRun anchor recorded on the manifest; 'apply' will require it)");

            // State the side effect rather than performing it quietly. A dry run reads as "nothing
            // happens", and mostly nothing does — but it saved the project to make the copy an honest
            // picture of it (ADR 0016), and a save is the user's business even when it commits only what
            // they had already authored.
            sb.AppendLine("  (the project was saved before measuring, so the scratch copy matched it; " +
                          "the project itself was not modified by this dry run)");
            return Ok(sb);
        }
        catch (LcmFileLockedException)
        {
            return Fail(ProjectInUseMessage(fwDataPath, "dry-run"));
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            // Discard, never revert: disposing the scratch is what undoes the dry run's mutations.
            scratch?.Dispose();

            // And release the project, so the linguist can have FieldWorks back.
            if (cache is { IsDisposed: false }) cache.Dispose();
        }
    }

    public static CommandResult Apply(string storeDir, string proposalId, string fwDataPath, string user)
    {
        LcmCache? cache = null;
        try
        {
            var store = new ProposalStore(storeDir);
            var id = NormalizeId(proposalId);
            var manifestPath = store.ManifestPath(id);
            if (!File.Exists(manifestPath))
                return Fail(ProposalNotFoundMessage(store, id));

            var manifest = ReadManifest(manifestPath);
            var objectPath = store.ObjectPath(manifest.CurrentIntentDigest);
            if (!File.Exists(objectPath))
                return Fail(StoreInconsistencyMessage(id, manifest.CurrentIntentDigest, objectPath));

            // ADR 0004, decision 3: a bare apply with no bound DryRun is a hard error. Enforced
            // here (the CLI's own precondition, checked before even loading the project) as well as
            // inside ProposalApplier.Apply itself (which requires a non-null anchor argument).
            if (manifest.Anchor is null)
            {
                return Fail(
                    $"Proposal {id} has no bound DryRun recorded. Run " +
                    $"'dry-run {id} --project <fwdata>' first, then 'apply'.");
            }

            var envelope = ProposalJsonParser.Parse(File.ReadAllText(objectPath));

            var fullFwDataPath = ResolveProjectPath(fwDataPath);
            var loader = new FwDataProjectLoader();
            cache = loader.LoadCache(fullFwDataPath);
            try
            {
                var description = manifest.Label ?? "";
                var receipt = ProposalApplier.Apply(cache, envelope, manifest.Anchor, user, description);

                var sb = new StringBuilder();
                if (receipt.AlreadyApplied)
                {
                    sb.AppendLine($"Proposal {id} was already applied (idempotent; no mutation performed).");
                    sb.AppendLine($"  {receipt.ResultNote}");
                }
                else
                {
                    // The core never saves; the host does, only after the unit of work has closed.
                    loader.Save(cache);

                    sb.AppendLine($"Applied Proposal {id}.");
                    sb.AppendLine($"  {receipt.ResultNote}");
                    sb.AppendLine($"  effects ({receipt.ActualEffects.Count}):");
                    foreach (var effect in receipt.ActualEffects)
                        AppendEffect(sb, effect);
                    sb.AppendLine($"  effectDigest: {receipt.EffectDigest}");
                }

                sb.AppendLine(
                    $"  applied-log entry: proposalId={receipt.AppliedLogEntry.ProposalId:D} " +
                    $"timestamp={receipt.AppliedLogEntry.TimestampUtc} user='{receipt.AppliedLogEntry.User}' " +
                    $"intentDigest={receipt.AppliedLogEntry.IntentDigest}");

                manifest.Status = ManifestStatus.Applied;
                WriteManifest(manifestPath, manifest);

                return Ok(sb);
            }
            finally
            {
                if (cache is { IsDisposed: false }) cache.Dispose();
            }
        }
        catch (LcmFileLockedException)
        {
            return Fail(ProjectInUseMessage(fwDataPath, "apply"));
        }
        catch (Exception ex)
        {
            // A failed apply rolled back, and a rollback is not an Undo: LexEntry headword/homograph
            // and MoStemAllomorph monomorphemic caches can be stale afterwards, and ADR 0005's
            // non-undoable schema phase can survive outright. There is no field list to consult and
            // nothing to repair — the rule is unconditional and belongs to whoever holds the cache
            // (ADR 0016, amended 2026-08-06). This process discards it either way; a caller reusing
            // one LcmCache across several library calls has to reload, so say so rather than assume.
            return Fail(
                ex.Message +
                " [This LcmCache is no longer trustworthy: a failed apply rolls back, which does not " +
                "refresh LibLCM's derived caches. Discard it and reload the project.]");
        }
    }

    public static CommandResult Log(string fwDataPath)
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

            var sb = new StringBuilder();
            sb.AppendLine(
                $"Applied-change log for '{fullFwDataPath}' " +
                $"({entries.Count} Motif entr{(entries.Count == 1 ? "y" : "ies")}):");

            foreach (var entry in entries.OrderBy(e => e.TimestampUtc, StringComparer.Ordinal))
            {
                sb.AppendLine(
                    $"  {entry.ProposalId:D}  ts={entry.TimestampUtc}  user='{entry.User}'  " +
                    $"intentDigest={entry.IntentDigest}  description=\"{entry.Description}\"");
            }

            foreach (var diagnostic in diagnostics)
                sb.AppendLine(diagnostic);

            return Ok(sb);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// Explains a refused open in Motif's own terms. LibLCM's message for a locked project says
    /// "FieldWorks cannot open the project ... because another program is using it", which is both
    /// confusing (the thing that could not open it was Motif) and short of the one instruction that
    /// resolves it.
    /// </summary>
    /// <remarks>
    /// Refusing here is correct, not a limitation: exactly one program writes a project at a time, and
    /// Motif takes the same <c>{project}.fwdata.lock</c> FieldWorks does
    /// (docs/adr/0030-one-writer-cli-locks-like-fieldworks.md).
    /// </remarks>
    private static string ProjectInUseMessage(string fwDataPath, string verb) =>
        $"Cannot {verb}: the project '{Path.GetFileNameWithoutExtension(fwDataPath)}' is in use by " +
        "another program — most likely FieldWorks, or another Motif command that has not finished. " +
        "Only one program may hold a FieldWorks project at a time, and Motif takes the same lock " +
        "FieldWorks does. Close the other program and try again.";

    /// <summary>
    /// Deletes a Dry Run scratch copy. Best effort by design: a leaked temp directory is a nuisance,
    /// but failing the command over one would turn a successful Dry Run into a reported failure.
    /// </summary>
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

    private static void AppendEffect(StringBuilder sb, ExpectedEffect effect)
    {
        sb.AppendLine($"    {effect.CanonicalId.Value}  field={effect.Field}");

        var wsKeys = effect.Before.Keys.Union(effect.After.Keys).OrderBy(k => k, StringComparer.Ordinal);
        var any = false;
        foreach (var ws in wsKeys)
        {
            var before = effect.Before.TryGetValue(ws, out var b) ? b : "(absent)";
            var after = effect.After.TryGetValue(ws, out var a) ? a : "(absent)";
            if (string.Equals(before, after, StringComparison.Ordinal))
                continue;

            sb.AppendLine($"      [{ws}] \"{before}\" -> \"{after}\"");
            any = true;
        }

        if (!any)
            sb.AppendLine("      (no observable before/after change)");
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
        };

        return JsonSerializer.Serialize(document, ProposalJsonOptions);
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

    private static CommandResult Fail(string message) => new(1, "error: " + message + Environment.NewLine);
}
