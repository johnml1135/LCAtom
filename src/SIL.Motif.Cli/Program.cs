using SIL.Motif.Contract.Responses;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SIL.Motif.Cli;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Projection.Usage;

// Thin dispatcher: verbs call straight into Commands, so tests exercise the same handlers without shelling out.

if (args.Length == 0)
{
    PrintUsage(Console.Error);
    return 1;
}

var verb = args[0];
var rest = args[1..];

try
{
    var (flags, positionals) = ParseArgs(rest);
    var storeDir = flags.TryGetValue("store", out var storeOverride)
        ? Path.GetFullPath(storeOverride)
        : Path.Combine(Directory.GetCurrentDirectory(), ".motif");

    // Every migrated read surface renders both ways from one projection (ADR 0021 decision 2).
    var asJson = flags.ContainsKey("json");
    var usage = new UsageLog();

    CommandResult result;

    switch (verb)
    {
        case "open":
            if (positionals.Count != 1)
                return Usage("Usage: motif open <path-to-.fwdata> [--json]", asJson);
            result = asJson ? Commands.OpenJson(positionals[0], usage) : Commands.Open(positionals[0], usage);
            break;

        case "analyses":
            if (!flags.TryGetValue("project", out var analysesProject))
                return Usage(AnalysesUsage(), asJson);
            var hasAssessment = flags.TryGetValue("assessment", out var assessmentId);
            var hasCurrentCorpus = flags.TryGetValue("current-corpus-sha256", out var currentCorpusSha256);
            var hasCurrentGrammar = flags.TryGetValue("current-grammar-sha256", out var currentGrammarSha256);
            if ((hasAssessment || hasCurrentCorpus || hasCurrentGrammar)
                && !(hasAssessment && hasCurrentCorpus && hasCurrentGrammar))
            {
                return Usage(AnalysesUsage(), asJson);
            }
            if (hasAssessment
                && (!Sha256Value.IsCanonical(assessmentId)
                    || !Sha256Value.IsCanonical(currentCorpusSha256)
                    || !Sha256Value.IsCanonical(currentGrammarSha256)))
            {
                return Usage(AnalysesUsage(), asJson);
            }
            result = hasAssessment
                ? asJson
                    ? Commands.AnalysesJson(
                        storeDir, analysesProject, assessmentId!, currentCorpusSha256!, currentGrammarSha256!, usage)
                    : Commands.Analyses(
                        storeDir, analysesProject, assessmentId!, currentCorpusSha256!, currentGrammarSha256!, usage)
                : asJson
                    ? Commands.AnalysesJson(analysesProject, usage)
                    : Commands.Analyses(analysesProject, usage);
            break;

        case "new":
            if (!flags.TryGetValue("draft", out var newDraftName))
                return Usage("Usage: motif new --draft <name> [--label <text>]", asJson);
            result = Commands.New(storeDir, newDraftName, flags.GetValueOrDefault("label"));
            break;

        case "add-set-gloss":
            if (!flags.TryGetValue("draft", out var addDraftName) ||
                !flags.TryGetValue("target", out var addTarget) ||
                !flags.TryGetValue("ws", out var addWs) ||
                !flags.TryGetValue("text", out var addText))
            {
                return Usage(
                    "Usage: motif add-set-gloss --draft <name> --target <canonicalId> --ws <wsTag> --text <text> " +
                    "[--depends-on <opId>[,<opId>...]]", asJson);
            }
            var addDependsOn = flags.TryGetValue("depends-on", out var addDependsOnRaw)
                ? addDependsOnRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : null;
            result = Commands.AddSetGloss(storeDir, addDraftName, addTarget, addWs, addText, addDependsOn);
            break;

        case "add-delete-lexeme-form":
            if (!flags.TryGetValue("draft", out var addDelDraftName) ||
                !flags.TryGetValue("target", out var addDelTarget))
            {
                return Usage("Usage: motif add-delete-lexeme-form --draft <name> --target <canonicalId>", asJson);
            }
            result = Commands.AddDeleteLexemeForm(storeDir, addDelDraftName, addDelTarget);
            break;

        case "compose-author-lexeme-form":
            if (!flags.TryGetValue("draft", out var composeDraftName) ||
                !flags.TryGetValue("project", out var composeProject) ||
                !flags.TryGetValue("intent", out var composeIntent))
            {
                return Usage(
                    "Usage: motif compose-author-lexeme-form --draft <name> --project <fwdata> --intent " +
                    "'{\"entry\":...,\"morphType\":...,\"ws\":...,\"text\":...}'", asJson);
            }
            result = Commands.ComposeAuthorLexemeForm(storeDir, composeDraftName, composeProject, composeIntent);
            break;

        case "compose-author-feature-structure":
            if (!flags.TryGetValue("draft", out var composeFsDraftName) ||
                !flags.TryGetValue("project", out var composeFsProject) ||
                !flags.TryGetValue("intent", out var composeFsIntent))
            {
                return Usage(
                    "Usage: motif compose-author-feature-structure --draft <name> --project <fwdata> " +
                    "--intent '{\"msa\":...}'", asJson);
            }
            result = Commands.ComposeAuthorFeatureStructure(
                storeDir, composeFsDraftName, composeFsProject, composeFsIntent);
            break;

        case "promote-gloss":
            if (!flags.TryGetValue("draft", out var promoteDraftName) ||
                !flags.TryGetValue("target", out var promoteTarget) ||
                !flags.TryGetValue("ws", out var promoteWs) ||
                !flags.TryGetValue("text", out var promoteText) ||
                !flags.TryGetValue("corpus", out var promoteCorpus))
            {
                return Usage(
                    "Usage: motif promote-gloss --draft <name> --target <canonicalId> --ws <wsTag> " +
                    "--text <text> --corpus <corpusId> [--document <docId>]", asJson);
            }
            result = Commands.PromoteGloss(
                storeDir, promoteDraftName, promoteTarget, promoteWs, promoteText, promoteCorpus,
                flags.GetValueOrDefault("document"));
            break;

        case "label":
            if (!flags.TryGetValue("draft", out var labelDraftName) || positionals.Count != 1)
                return Usage("Usage: motif label --draft <name> <text>", asJson);
            result = Commands.Label(storeDir, labelDraftName, positionals[0]);
            break;

        case "comment":
            if (!flags.TryGetValue("draft", out var commentDraftName) || positionals.Count != 1)
                return Usage("Usage: motif comment --draft <name> <text>", asJson);
            result = Commands.Comment(storeDir, commentDraftName, positionals[0]);
            break;

        case "finalize":
            if (!flags.TryGetValue("draft", out var finalizeDraftName))
                return Usage("Usage: motif finalize --draft <name>", asJson);
            result = Commands.Finalize(storeDir, finalizeDraftName);
            break;

        case "reopen":
            if (!flags.TryGetValue("draft", out var reopenDraftName) || positionals.Count != 1)
                return Usage("Usage: motif reopen --draft <name> <proposalId>", asJson);
            result = Commands.Reopen(storeDir, reopenDraftName, positionals[0]);
            break;

        case "duplicate":
            if (!flags.TryGetValue("draft", out var dupDraftName) || positionals.Count != 1)
                return Usage("Usage: motif duplicate --draft <newName> <proposalId>", asJson);
            result = Commands.Duplicate(storeDir, positionals[0], dupDraftName);
            break;

        case "remove-operations":
            if (!flags.TryGetValue("draft", out var removeDraftName) || positionals.Count == 0)
                return Usage("Usage: motif remove-operations --draft <name> <operationId> [<operationId>...] [--force]", asJson);
            var removeForce = flags.TryGetValue("force", out var removeForceRaw) && IsTruthyFlag(removeForceRaw);
            result = Commands.RemoveOperations(storeDir, removeDraftName, positionals, removeForce);
            break;

        case "split":
            if (positionals.Count < 2)
            {
                return Usage(
                    "Usage: motif split <proposalId> <draftName>=<opId>[,<opId>...] " +
                    "[<draftName>=<opId>[,<opId>...] ...] [--force]", asJson);
            }
            var splitForce = flags.TryGetValue("force", out var splitForceRaw) && IsTruthyFlag(splitForceRaw);
            var splitGroups = new List<Commands.SplitGroup>();
            foreach (var spec in positionals.Skip(1))
            {
                var eq = spec.IndexOf('=');
                if (eq <= 0 || eq == spec.Length - 1)
                {
                    return Usage(
                        $"Invalid split group '{spec}'. Expected '<draftName>=<opId>[,<opId>...]'.", asJson);
                }
                var groupDraftName = spec[..eq];
                var groupOpIds = spec[(eq + 1)..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                splitGroups.Add(new Commands.SplitGroup(groupDraftName, groupOpIds));
            }
            result = Commands.Split(storeDir, positionals[0], splitGroups, splitForce);
            break;

        case "defer":
            if (positionals.Count != 1)
                return Usage("Usage: motif defer <proposalId>", asJson);
            result = Commands.Defer(storeDir, positionals[0]);
            break;

        case "approve":
            if (positionals.Count != 1 ||
                !flags.TryGetValue("actor-type", out var approveActorType) ||
                !flags.TryGetValue("actor-id", out var approveActorId))
            {
                return Usage(
                    "Usage: motif approve <proposalId> --actor-type human|ai --actor-id <name> [--comment <text>]", asJson);
            }
            result = Commands.Approve(
                storeDir, positionals[0], approveActorType, approveActorId, flags.GetValueOrDefault("comment"));
            break;

        case "reject":
            if (positionals.Count != 1 ||
                !flags.TryGetValue("actor-type", out var rejectActorType) ||
                !flags.TryGetValue("actor-id", out var rejectActorId))
            {
                return Usage(
                    "Usage: motif reject <proposalId> --actor-type human|ai --actor-id <name> [--comment <text>]", asJson);
            }
            result = Commands.Reject(
                storeDir, positionals[0], rejectActorType, rejectActorId, flags.GetValueOrDefault("comment"));
            break;

        case "supersede":
            if (positionals.Count != 2)
                return Usage("Usage: motif supersede <proposalId> <supersededByProposalId>", asJson);
            result = Commands.Supersede(storeDir, positionals[0], positionals[1]);
            break;

        case "list":
            result = asJson ? Commands.ListJson(storeDir, usage) : Commands.List(storeDir, usage);
            break;

        case "show":
            if (positionals.Count != 1)
                return Usage("Usage: motif show <proposalId> [--json]", asJson);
            result = asJson
                ? Commands.ShowJson(storeDir, positionals[0], usage)
                : Commands.Show(storeDir, positionals[0], usage);
            break;

        case "dry-run":
            if (positionals.Count != 1 || !flags.TryGetValue("project", out var dryRunProject))
                return Usage("Usage: motif dry-run <proposalId> --project <fwdata> [--json]", asJson);
            result = asJson
                ? Commands.DryRunJson(storeDir, positionals[0], dryRunProject, usage)
                : Commands.DryRun(storeDir, positionals[0], dryRunProject, usage);
            break;

        case "apply":
            if (positionals.Count != 1 ||
                !flags.TryGetValue("project", out var applyProject) ||
                !flags.TryGetValue("user", out var applyUser))
            {
                return Usage("Usage: motif apply <proposalId> --project <fwdata> --user <name> [--json]", asJson);
            }
            result = asJson
                ? Commands.ApplyJson(storeDir, positionals[0], applyProject, applyUser, usage)
                : Commands.Apply(storeDir, positionals[0], applyProject, applyUser, usage);
            break;

        case "log":
            if (!flags.TryGetValue("project", out var logProject))
                return Usage("Usage: motif log --project <fwdata> [--json]", asJson);
            result = asJson ? Commands.LogJson(logProject, usage) : Commands.Log(logProject, usage);
            break;

        case "add-corpus":
            if (!flags.TryGetValue("id", out var corpusId) ||
                !flags.TryGetValue("description", out var corpusDescription) ||
                !flags.TryGetValue("tokeniser", out var corpusTokeniser) ||
                !flags.TryGetValue("tokeniser-version", out var corpusTokeniserVersion))
            {
                return Usage(
                    "Usage: motif add-corpus --id <id> --description <text> --tokeniser <name> " +
                    "--tokeniser-version <v> [--uri <url>] [--licence <text>] [--tokeniser-notes <text>] " +
                    "[--may-derive true|false] [--may-redistribute true|false] " +
                    "[--may-use-commercially true|false] [--requires-attribution true|false] " +
                    "[--licence-basis <text>]", asJson);
            }

            result = CorpusCommands.AddCorpus(
                storeDir,
                corpusId,
                corpusDescription,
                flags.GetValueOrDefault("uri"),
                flags.GetValueOrDefault("licence"),
                CorpusCommands.CapabilitiesFromFlags(flags),
                corpusTokeniser,
                corpusTokeniserVersion,
                flags.GetValueOrDefault("tokeniser-notes"));
            break;

        case "add-document":
            if (!flags.TryGetValue("corpus", out var documentCorpus) ||
                !flags.TryGetValue("doc", out var documentId) ||
                !flags.TryGetValue("source", out var documentPathOrUrl))
            {
                return Usage(
                    "Usage: motif add-document --corpus <id> --doc <id> --source <file-or-url> " +
                    "[--title <text>] [--licence <text>] [--may-derive true|false] [--licence-basis <text>]", asJson);
            }

            // No flags means "same as corpus", not "nothing established": pass null, not Unknown, or it overrides one.
            var documentCapabilities = HasAnyLicenceFlag(flags)
                ? CorpusCommands.CapabilitiesFromFlags(flags)
                : null;

            result = CorpusCommands.AddDocument(
                storeDir,
                documentCorpus,
                documentId,
                documentPathOrUrl,
                flags.GetValueOrDefault("title"),
                flags.GetValueOrDefault("licence"),
                documentCapabilities);
            break;

        case "add-corpus-bundle":
            if (!flags.TryGetValue("bundle", out var bundlePath))
                return Usage("Usage: motif add-corpus-bundle --bundle <path-to-bundle.json>", asJson);
            result = CorpusCommands.AddBundle(storeDir, bundlePath);
            break;

        case "corpora":
            result = asJson
                ? CorpusCommands.ListCorporaJson(storeDir, usage)
                : CorpusCommands.ListCorpora(storeDir, usage);
            break;

        case "show-corpus":
            if (positionals.Count != 1)
                return Usage("Usage: motif show-corpus <corpusId>", asJson);
            result = asJson
                ? CorpusCommands.ShowCorpusJson(storeDir, positionals[0], usage)
                : CorpusCommands.ShowCorpus(storeDir, positionals[0], usage);
            break;

        case "baseline-refresh":
            if (!flags.TryGetValue("project", out var refreshProject))
                return Usage("Usage: motif baseline-refresh --project <fwdata>", asJson);
            result = JobCommands.EnqueueBaselineRefresh(refreshProject, CliProductVersion());
            break;

        case "jobs":
            if (positionals.Count != 2 || positionals[0] != "show")
                return Usage("Usage: motif jobs show <jobId> --project <fwdata> [--json]", asJson);
            if (!flags.TryGetValue("project", out var jobsProject))
                return Usage("Usage: motif jobs show <jobId> --project <fwdata> [--json]", asJson);
            result = JobCommands.Show(jobsProject, positionals[1], CliProductVersion(), asJson);
            break;

        default:
            return Usage($"Unknown command '{verb}'.", asJson, withUsageBanner: true);
    }

    // One process is one call; appending to a local file is what accumulates a session (ADR 0021 decision 4).
    foreach (var entry in usage.Entries)
        UsageLogFile.Append(Path.Combine(storeDir, "usage.jsonl"), entry);

    if (result.ExitCode == 0)
    {
        Console.Out.Write(result.Output);
        return result.ExitCode;
    }
    // A caller that asked for JSON gets JSON when it goes wrong too.
    Console.Error.Write(asJson && result.Reason is { } reason
        ? ProjectionJson.Serialize(new FailureEnvelope(reason, result.Output.Trim())) + Environment.NewLine
        : result.Output);
    return result.ExitCode;
}
catch (Exception ex)
{
    // Nothing decided this; it escaped. That is a bug, not a refusal, and it gets its own code.
    Console.Error.WriteLine($"error: {ex.Message}");
    return FailureEnvelope.ExitCodeFor(FailureReason.StoreInconsistent);
}

static int Usage(string message, bool asJson = false, bool withUsageBanner = false)
{
    if (asJson)
    {
        Console.Error.WriteLine(ProjectionJson.Serialize(
            new FailureEnvelope(FailureReason.InvalidArgument, message)));
        return FailureEnvelope.ExitCodeFor(FailureReason.InvalidArgument);
    }
    Console.Error.WriteLine(message);
    if (withUsageBanner) PrintUsage(Console.Error);
    return FailureEnvelope.ExitCodeFor(FailureReason.InvalidArgument);
}

static string AnalysesUsage() =>
    "Usage: motif analyses --project <fwdata> [--json] OR motif analyses --project <fwdata> " +
    "--assessment <assessmentId> --current-corpus-sha256 <sha256> " +
    "--current-grammar-sha256 <sha256> [--store <dir>] [--json]";

static void PrintUsage(TextWriter writer)
{
    writer.WriteLine("Usage: motif <command> [options]");
    writer.WriteLine();
    writer.WriteLine("Commands:");
    writer.WriteLine("  open <fwdata> [--json]");
    writer.WriteLine("  analyses --project <fwdata> [--json]");
    writer.WriteLine(
        "  analyses --project <fwdata> --assessment <assessmentId> --current-corpus-sha256 <sha256> " +
        "--current-grammar-sha256 <sha256> [--store <dir>] [--json]");
    writer.WriteLine("  new --draft <name> [--label <text>]");
    writer.WriteLine(
        "  add-set-gloss --draft <name> --target <canonicalId> --ws <wsTag> --text <text> " +
        "[--depends-on <opId>[,<opId>...]]");
    writer.WriteLine("  add-delete-lexeme-form --draft <name> --target <canonicalId>");
    writer.WriteLine(
        "  compose-author-lexeme-form --draft <name> --project <fwdata> --intent " +
        "'{\"entry\":...,\"morphType\":...,\"ws\":...,\"text\":...}'");
    writer.WriteLine(
        "  compose-author-feature-structure --draft <name> --project <fwdata> --intent '{\"msa\":...}'");
    writer.WriteLine(
        "  promote-gloss --draft <name> --target <canonicalId> --ws <wsTag> --text <text> " +
        "--corpus <corpusId> [--document <docId>]");
    writer.WriteLine("  label --draft <name> <text>");
    writer.WriteLine("  comment --draft <name> <text>");
    writer.WriteLine("  finalize --draft <name>");
    writer.WriteLine("  reopen --draft <name> <proposalId>");
    writer.WriteLine("  duplicate --draft <newName> <proposalId>");
    writer.WriteLine("  remove-operations --draft <name> <operationId> [<operationId>...] [--force]");
    writer.WriteLine(
        "  split <proposalId> <draftName>=<opId>[,<opId>...] [<draftName>=<opId>[,<opId>...] ...] [--force]");
    writer.WriteLine("  defer <proposalId>");
    writer.WriteLine("  approve <proposalId> --actor-type human|ai --actor-id <name> [--comment <text>]");
    writer.WriteLine("  reject <proposalId> --actor-type human|ai --actor-id <name> [--comment <text>]");
    writer.WriteLine("  supersede <proposalId> <supersededByProposalId>");
    writer.WriteLine("  list [--json]");
    writer.WriteLine("  show <proposalId> [--json]");
    writer.WriteLine("  dry-run <proposalId> --project <fwdata> [--json]");
    writer.WriteLine("  apply <proposalId> --project <fwdata> --user <name> [--json]");
    writer.WriteLine("  log --project <fwdata> [--json]");
    writer.WriteLine();
    writer.WriteLine("Corpus (text Motif measures against; never part of the FieldWorks project):");
    writer.WriteLine(
        "  add-corpus --id <id> --description <text> --tokeniser <name> --tokeniser-version <v> " +
        "[--uri <url>] [--licence <text>] [--tokeniser-notes <text>] [--may-derive true|false] " +
        "[--may-redistribute true|false] [--may-use-commercially true|false] [--licence-basis <text>]");
    writer.WriteLine(
        "  add-document --corpus <id> --doc <id> --source <file-or-url> [--title <text>] " +
        "[--licence <text>] [--may-derive true|false] [--licence-basis <text>]");
    writer.WriteLine("  add-corpus-bundle --bundle <path>   (the handoff a fetching tool writes)");
    writer.WriteLine("  corpora [--json]");
    writer.WriteLine("  show-corpus <corpusId> [--json]");
    writer.WriteLine();
    writer.WriteLine("Global options: --store <dir>  (default: ./.motif)");
    writer.WriteLine(
        "                --json         (structured output; supported by " +
        "open/analyses/list/show/dry-run/apply/log/corpora/show-corpus)");
}

/// <summary>Whether the caller said anything at all about what a licence permits.</summary>
static bool HasAnyLicenceFlag(Dictionary<string, string> flags) =>
    flags.ContainsKey("may-derive")
    || flags.ContainsKey("may-redistribute")
    || flags.ContainsKey("may-use-commercially")
    || flags.ContainsKey("requires-attribution")
    || flags.ContainsKey("licence-basis");

static (Dictionary<string, string> Flags, List<string> Positionals) ParseArgs(string[] tokens)
{
    var flags = new Dictionary<string, string>(StringComparer.Ordinal);
    var positionals = new List<string>();

    for (var i = 0; i < tokens.Length; i++)
    {
        var token = tokens[i];
        if (token.StartsWith("--", StringComparison.Ordinal))
        {
            var name = token[2..];
            // No value, or one followed by another flag, is a bare switch (e.g. --force): no value starts with "--".
            if (i + 1 >= tokens.Length || tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
                flags[name] = "true";
            else
                flags[name] = tokens[++i];
        }
        else
        {
            positionals.Add(token);
        }
    }

    return (flags, positionals);
}

// The version this CLI negotiates with; the worker decides compatibility from the protocol range, not this.
static string CliProductVersion() =>
    typeof(Commands).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

static bool IsTruthyFlag(string value) => !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
