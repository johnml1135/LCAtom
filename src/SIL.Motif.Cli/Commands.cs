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

    public static CommandResult AddSetGloss(string storeDir, string draftName, string target, string ws, string text)
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
            var operationId = CanonicalId.Mint();

            draft.Operations.Add(new DraftOperation
            {
                OperationId = operationId.Value,
                Kind = LexicalSenseOperationKinds.SetGloss,
                Target = targetId.Value,
                After = new Dictionary<string, string> { ["ws"] = ws, ["text"] = text },
            });

            WriteDraft(draftPath, draft);

            var sb = new StringBuilder();
            sb.AppendLine(
                $"Added operation '{operationId.Value}' ({LexicalSenseOperationKinds.SetGloss}) to draft '{draftName}'.");
            sb.AppendLine($"  target: {targetId.Value}");
            sb.AppendLine($"  after:  ws={ws} text=\"{text}\"");
            sb.AppendLine($"Draft now has {draft.Operations.Count} operation(s).");
            return Ok(sb);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
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
            After = afterDict,
        };
    }

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
                target = op.Target,
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
