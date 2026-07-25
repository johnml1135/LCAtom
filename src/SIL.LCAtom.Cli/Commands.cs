using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SIL.LCAtom.Cli.Store;
using SIL.LCAtom.Contract.Canonicalization;
using SIL.LCAtom.Contract.Ids;
using SIL.LCAtom.Contract.Parsing;
using SIL.LCAtom.Host.LcmUtils;
using SIL.LCAtom.Model.Effects;
using SIL.LCAtom.Runner.Apply;
using SIL.LCAtom.Runner.AppliedLog;
using SIL.LCAtom.Runner.Assessment;
using SIL.LCAtom.Runner.Operations;
using SIL.LCModel;

namespace SIL.LCAtom.Cli;

/// <summary>The result of one CLI command: a captured exit code and the text to print.</summary>
public sealed record CommandResult(int ExitCode, string Output);

/// <summary>
/// Testable command handlers for every LCAtom CLI verb, driving the Stage E files store (see
/// <see cref="SIL.LCAtom.Cli.Store.ChangeSetStore"/>) and the real Contract/Runner/Host APIs end to
/// end. <c>Program.cs</c> is a thin argument dispatcher over these methods: every method here is a
/// plain function of explicit parameters returning a <see cref="CommandResult"/>, so tests call them
/// directly rather than shelling out to the built executable.
/// </summary>
/// <remarks>
/// This class never re-implements assess/apply/log semantics: it calls
/// <see cref="ChangeSetAssessor.Assess"/>, <see cref="ChangeSetApplier.Apply"/>,
/// <see cref="ProjectAppliedLog.ReadAll"/>, and <see cref="FwDataProjectLoader"/> exactly as Stages
/// C/D/A left them.
/// </remarks>
public static class Commands
{
    static Commands()
    {
        // SIL.LCAtom.Runner.Operations.LexicalSenseOperationKinds registers "lexical/sense/setGloss"
        // with the Contract kernel's OperationKindRegistry via a [ModuleInitializer] (see that
        // type's remarks) — but a module initializer only runs once the CLR actually loads the
        // Runner assembly, and every call site in this class that names SetGloss uses the *const*
        // field, which the C# compiler inlines as a literal string at compile time. That means
        // building a draft's operation JSON and finalizing it (which only ever touches Contract's
        // ChangeSetJsonParser) would never otherwise force the Runner assembly to load, so
        // "Unknown operation kind" would fire even though this same CLI process later calls
        // ChangeSetAssessor/ChangeSetApplier successfully. Forcing the module constructor here,
        // once, up front, makes registration independent of which command runs first.
        RuntimeHelpers.RunModuleConstructor(typeof(LexicalSenseOperationKinds).Module.ModuleHandle);
    }

    private static readonly JsonSerializerOptions DraftJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions ChangeSetJsonOptions = new()
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
            var store = new ChangeSetStore(storeDir);
            store.EnsureDirectoriesExist();
            var draftPath = store.DraftPath(draftName);

            if (File.Exists(draftPath))
            {
                return Fail(
                    $"Draft '{draftName}' already exists at '{draftPath}'. Finalize or delete it " +
                    "before creating a new draft with this name.");
            }

            var changeSetId = CanonicalId.Mint();
            var draft = new DraftDocument
            {
                ChangeSetId = changeSetId.Value,
                ContractVersions = new Dictionary<string, string> { ["lexical"] = "1.0" },
                Requires = new List<string>(),
                Label = label,
                Comment = null,
                Operations = new List<DraftOperation>(),
            };

            WriteDraft(draftPath, draft);

            var sb = new StringBuilder();
            sb.AppendLine($"Created draft '{draftName}' at '{draftPath}'.");
            sb.AppendLine($"  changeSetId: {draft.ChangeSetId}");
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
            var store = new ChangeSetStore(storeDir);
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
            var store = new ChangeSetStore(storeDir);
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
            var store = new ChangeSetStore(storeDir);
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

            var changeSetJson = BuildChangeSetJson(draft);

            SIL.LCAtom.Contract.Model.ChangeSetEnvelope envelope;
            try
            {
                envelope = ChangeSetJsonParser.Parse(changeSetJson);
            }
            catch (ContractParseException ex)
            {
                return Fail($"Draft '{draftName}' failed Change Set validation: {ex.Message}");
            }

            var intentDigest = IntentDigest.Compute(envelope);

            store.EnsureDirectoriesExist();
            var objectPath = store.ObjectPath(draft.ChangeSetId);
            var manifestPath = store.ManifestPath(draft.ChangeSetId);

            // Immutable once written: finalize is the only writer of objects/<id>.json.
            File.WriteAllText(objectPath, changeSetJson);

            var manifest = new ManifestDocument
            {
                ChangeSetId = draft.ChangeSetId,
                Status = ManifestStatus.Proposed,
                Label = draft.Label,
                Comment = draft.Comment,
                IntentDigest = intentDigest,
            };
            WriteManifest(manifestPath, manifest);

            File.Delete(draftPath);

            var sb = new StringBuilder();
            sb.AppendLine($"Finalized draft '{draftName}' -> Change Set {draft.ChangeSetId} (status: proposed).");
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

    public static CommandResult List(string storeDir)
    {
        try
        {
            var store = new ChangeSetStore(storeDir);
            var sb = new StringBuilder();

            if (!Directory.Exists(store.ManifestsDirectory))
            {
                sb.AppendLine("No change sets in store.");
                return Ok(sb);
            }

            var manifestFiles = Directory.GetFiles(store.ManifestsDirectory, "*.json")
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            if (manifestFiles.Count == 0)
            {
                sb.AppendLine("No change sets in store.");
                return Ok(sb);
            }

            foreach (var file in manifestFiles)
            {
                var manifest = ReadManifest(file);
                sb.AppendLine($"{manifest.ChangeSetId}  {manifest.Status,-8}  {manifest.Label}");
            }
            return Ok(sb);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public static CommandResult Show(string storeDir, string changeSetId)
    {
        try
        {
            var store = new ChangeSetStore(storeDir);
            var id = NormalizeId(changeSetId);
            var objectPath = store.ObjectPath(id);
            var manifestPath = store.ManifestPath(id);

            if (!File.Exists(objectPath) || !File.Exists(manifestPath))
                return Fail(ChangeSetNotFoundMessage(store, id));

            var manifest = ReadManifest(manifestPath);
            var objectJson = File.ReadAllText(objectPath);

            var sb = new StringBuilder();
            sb.AppendLine($"Change Set {id}");
            sb.AppendLine($"  status:       {manifest.Status}");
            sb.AppendLine($"  label:        {manifest.Label}");
            sb.AppendLine($"  comment:      {manifest.Comment}");
            sb.AppendLine($"  intentDigest: {manifest.IntentDigest}");
            sb.AppendLine();
            sb.AppendLine(objectJson.TrimEnd());
            return Ok(sb);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public static CommandResult Assess(string storeDir, string changeSetId, string fwDataPath)
    {
        try
        {
            var store = new ChangeSetStore(storeDir);
            var id = NormalizeId(changeSetId);
            var objectPath = store.ObjectPath(id);
            if (!File.Exists(objectPath))
                return Fail(ChangeSetNotFoundMessage(store, id));

            var envelope = ChangeSetJsonParser.Parse(File.ReadAllText(objectPath));

            var fullFwDataPath = ResolveProjectPath(fwDataPath);
            var loader = new FwDataProjectLoader();
            // Read-only usage: Assess itself never commits (Stage C rolls back internally), and
            // this call performs no Save, so the project on disk is left untouched.
            using var cache = loader.LoadCache(fullFwDataPath);

            var assessment = ChangeSetAssessor.Assess(cache, envelope);

            var sb = new StringBuilder();
            sb.AppendLine($"Assessment of Change Set {id}");
            sb.AppendLine($"  intentDigest: {assessment.IntentDigest}");
            sb.AppendLine($"  baseline:     {assessment.BaselineNote}");
            sb.AppendLine($"  effects ({assessment.ExpectedEffects.Count}):");
            foreach (var effect in assessment.ExpectedEffects)
                AppendEffect(sb, effect);
            sb.AppendLine($"  effectDigest: {assessment.EffectDigest}");
            return Ok(sb);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public static CommandResult Apply(string storeDir, string changeSetId, string fwDataPath, string user)
    {
        try
        {
            var store = new ChangeSetStore(storeDir);
            var id = NormalizeId(changeSetId);
            var objectPath = store.ObjectPath(id);
            var manifestPath = store.ManifestPath(id);
            if (!File.Exists(objectPath) || !File.Exists(manifestPath))
                return Fail(ChangeSetNotFoundMessage(store, id));

            var envelope = ChangeSetJsonParser.Parse(File.ReadAllText(objectPath));
            var manifest = ReadManifest(manifestPath);

            var fullFwDataPath = ResolveProjectPath(fwDataPath);
            var loader = new FwDataProjectLoader();
            var cache = loader.LoadCache(fullFwDataPath);
            try
            {
                var description = manifest.Label ?? "";
                var receipt = ChangeSetApplier.Apply(cache, envelope, user, description);

                var sb = new StringBuilder();
                if (receipt.AlreadyApplied)
                {
                    sb.AppendLine($"Change Set {id} was already applied (idempotent; no mutation performed).");
                    sb.AppendLine($"  {receipt.ResultNote}");
                }
                else
                {
                    // The core never saves; the host does, only after the unit of work has closed.
                    loader.Save(cache);

                    sb.AppendLine($"Applied Change Set {id}.");
                    sb.AppendLine($"  {receipt.ResultNote}");
                    sb.AppendLine($"  effects ({receipt.ActualEffects.Count}):");
                    foreach (var effect in receipt.ActualEffects)
                        AppendEffect(sb, effect);
                    sb.AppendLine($"  effectDigest: {receipt.EffectDigest}");
                }

                sb.AppendLine(
                    $"  applied-log entry: changeSetId={receipt.AppliedLogEntry.ChangeSetId:D} " +
                    $"timestamp={receipt.AppliedLogEntry.TimestampUtc} user='{receipt.AppliedLogEntry.User}' " +
                    $"intentDigest={receipt.AppliedLogEntry.IntentDigest}");

                manifest.Status = ManifestStatus.Applied;
                WriteManifest(manifestPath, manifest);

                return Ok(sb);
            }
            finally
            {
                if (!cache.IsDisposed) cache.Dispose();
            }
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
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
                (name, error) => diagnostics.Add($"  [unparseable LCAtom entry] name='{name}' error='{error}'"));

            var sb = new StringBuilder();
            sb.AppendLine(
                $"Applied-change log for '{fullFwDataPath}' " +
                $"({entries.Count} LCAtom entr{(entries.Count == 1 ? "y" : "ies")}):");

            foreach (var entry in entries.OrderBy(e => e.TimestampUtc, StringComparer.Ordinal))
            {
                sb.AppendLine(
                    $"  {entry.ChangeSetId:D}  ts={entry.TimestampUtc}  user='{entry.User}'  " +
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

    private static string BuildChangeSetJson(DraftDocument draft)
    {
        var document = new
        {
            contractVersions = draft.ContractVersions,
            changeSetId = draft.ChangeSetId,
            requires = draft.Requires,
            operations = draft.Operations.Select(op => new
            {
                operationId = op.OperationId,
                kind = op.Kind,
                target = op.Target,
                after = op.After,
            }).ToList(),
        };

        return JsonSerializer.Serialize(document, ChangeSetJsonOptions);
    }

    private static string ResolveProjectPath(string fwDataPath)
    {
        var full = Path.GetFullPath(fwDataPath);
        if (!File.Exists(full))
            throw new FileNotFoundException($"Project file not found: '{full}'.", full);
        return full;
    }

    private static string NormalizeId(string changeSetId)
    {
        if (!CanonicalId.TryParse(changeSetId, out var id, out var error))
            throw new ArgumentException($"'{changeSetId}' is not a valid canonical Change Set id: {error}");
        return id.Value;
    }

    private static string DraftNotFoundMessage(ChangeSetStore store, string draftName) =>
        $"Draft '{draftName}' not found in store '{store.RootDirectory}'. Run 'new --draft {draftName}' first.";

    private static string ChangeSetNotFoundMessage(ChangeSetStore store, string id) =>
        $"Change Set '{id}' not found in store '{store.RootDirectory}'. Run 'list' to see committed change sets.";

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
