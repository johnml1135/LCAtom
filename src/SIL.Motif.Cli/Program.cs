using SIL.Motif.Contract.Responses;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SIL.Motif.Cli;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using SIL.Motif.Projection.Usage;
using SIL.Motif.Worker;
using SIL.Motif.Worker.Projects;

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

    // Every invocation naming a project upserts it into the machine store (ADR 0041 decision 4).
    if (flags.TryGetValue("project", out var projectForRegistry))
        RecordKnownProject(projectForRegistry);

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
                        analysesProject, assessmentId!, currentCorpusSha256!, currentGrammarSha256!, usage)
                    : Commands.Analyses(
                        analysesProject, assessmentId!, currentCorpusSha256!, currentGrammarSha256!, usage)
                : asJson
                    ? Commands.AnalysesJson(analysesProject, usage)
                    : Commands.Analyses(analysesProject, usage);
            break;

        case "new":
            if (!flags.TryGetValue("project", out var newProject) || !flags.TryGetValue("draft", out var newDraftName))
                return Usage("Usage: motif new --project <fwdata> --draft <name> [--label <text>]", asJson);
            result = Commands.New(newProject, CliProductVersion(), newDraftName, flags.GetValueOrDefault("label"));
            break;

        case "add-set-gloss":
            if (!flags.TryGetValue("project", out var addProject) ||
                !flags.TryGetValue("draft", out var addDraftName) ||
                !flags.TryGetValue("target", out var addTarget) ||
                !flags.TryGetValue("ws", out var addWs) ||
                !flags.TryGetValue("text", out var addText))
            {
                return Usage(
                    "Usage: motif add-set-gloss --project <fwdata> --draft <name> --target <canonicalId> " +
                    "--ws <wsTag> --text <text> [--depends-on <opId>[,<opId>...]]", asJson);
            }
            var addDependsOn = flags.TryGetValue("depends-on", out var addDependsOnRaw)
                ? addDependsOnRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : null;
            result = Commands.AddSetGloss(
                addProject, CliProductVersion(), addDraftName, addTarget, addWs, addText, addDependsOn);
            break;

        case "add-delete-lexeme-form":
            if (!flags.TryGetValue("project", out var addDelProject) ||
                !flags.TryGetValue("draft", out var addDelDraftName) ||
                !flags.TryGetValue("target", out var addDelTarget))
            {
                return Usage(
                    "Usage: motif add-delete-lexeme-form --project <fwdata> --draft <name> --target <canonicalId>",
                    asJson);
            }
            result = Commands.AddDeleteLexemeForm(addDelProject, CliProductVersion(), addDelDraftName, addDelTarget);
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
            result = Commands.ComposeAuthorLexemeForm(
                composeProject, CliProductVersion(), composeDraftName, composeIntent);
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
                composeFsProject, CliProductVersion(), composeFsDraftName, composeFsIntent);
            break;

        case "promote-gloss":
            if (!flags.TryGetValue("project", out var promoteProject) ||
                !flags.TryGetValue("draft", out var promoteDraftName) ||
                !flags.TryGetValue("target", out var promoteTarget) ||
                !flags.TryGetValue("ws", out var promoteWs) ||
                !flags.TryGetValue("text", out var promoteText) ||
                !flags.TryGetValue("corpus", out var promoteCorpus))
            {
                return Usage(
                    "Usage: motif promote-gloss --project <fwdata> --draft <name> --target <canonicalId> " +
                    "--ws <wsTag> --text <text> --corpus <corpusId> [--document <docId>]", asJson);
            }
            result = Commands.PromoteGloss(
                promoteProject, CliProductVersion(), promoteDraftName, promoteTarget, promoteWs, promoteText,
                promoteCorpus, flags.GetValueOrDefault("document"));
            break;

        case "label":
            if (!flags.TryGetValue("project", out var labelProject) ||
                !flags.TryGetValue("draft", out var labelDraftName) || positionals.Count != 1)
                return Usage("Usage: motif label --project <fwdata> --draft <name> <text>", asJson);
            result = Commands.Label(labelProject, CliProductVersion(), labelDraftName, positionals[0]);
            break;

        case "comment":
            if (!flags.TryGetValue("project", out var commentProject) ||
                !flags.TryGetValue("draft", out var commentDraftName) || positionals.Count != 1)
                return Usage("Usage: motif comment --project <fwdata> --draft <name> <text>", asJson);
            result = Commands.Comment(commentProject, CliProductVersion(), commentDraftName, positionals[0]);
            break;

        case "finalize":
            if (!flags.TryGetValue("project", out var finalizeProject) ||
                !flags.TryGetValue("draft", out var finalizeDraftName))
                return Usage("Usage: motif finalize --project <fwdata> --draft <name>", asJson);
            result = Commands.Finalize(finalizeProject, CliProductVersion(), finalizeDraftName);
            break;

        case "reopen":
            if (!flags.TryGetValue("project", out var reopenProject) ||
                !flags.TryGetValue("draft", out var reopenDraftName) || positionals.Count != 1)
                return Usage("Usage: motif reopen --project <fwdata> --draft <name> <proposalId>", asJson);
            result = Commands.Reopen(reopenProject, CliProductVersion(), reopenDraftName, positionals[0]);
            break;

        case "duplicate":
            if (!flags.TryGetValue("project", out var dupProject) ||
                !flags.TryGetValue("draft", out var dupDraftName) || positionals.Count != 1)
                return Usage("Usage: motif duplicate --project <fwdata> --draft <newName> <proposalId>", asJson);
            result = Commands.Duplicate(dupProject, CliProductVersion(), positionals[0], dupDraftName);
            break;

        case "remove-operations":
            if (!flags.TryGetValue("project", out var removeProject) ||
                !flags.TryGetValue("draft", out var removeDraftName) || positionals.Count == 0)
            {
                return Usage(
                    "Usage: motif remove-operations --project <fwdata> --draft <name> <operationId> " +
                    "[<operationId>...] [--force]", asJson);
            }
            var removeForce = flags.TryGetValue("force", out var removeForceRaw) && IsTruthyFlag(removeForceRaw);
            result = Commands.RemoveOperations(
                removeProject, CliProductVersion(), removeDraftName, positionals, removeForce);
            break;

        case "split":
            if (!flags.TryGetValue("project", out var splitProject) || positionals.Count < 2)
            {
                return Usage(
                    "Usage: motif split --project <fwdata> <proposalId> <draftName>=<opId>[,<opId>...] " +
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
            result = Commands.Split(splitProject, CliProductVersion(), positionals[0], splitGroups, splitForce);
            break;

        case "defer":
            if (!flags.TryGetValue("project", out var deferProject) || positionals.Count != 1)
                return Usage("Usage: motif defer --project <fwdata> <proposalId>", asJson);
            result = Commands.Defer(deferProject, CliProductVersion(), positionals[0]);
            break;

        case "approve":
            if (!flags.TryGetValue("project", out var approveProject) ||
                positionals.Count != 1 ||
                !flags.TryGetValue("actor-type", out var approveActorType) ||
                !flags.TryGetValue("actor-id", out var approveActorId))
            {
                return Usage(
                    "Usage: motif approve --project <fwdata> <proposalId> --actor-type human|ai --actor-id <name> " +
                    "[--comment <text>]", asJson);
            }
            result = Commands.Approve(
                approveProject, CliProductVersion(), positionals[0], approveActorType, approveActorId,
                flags.GetValueOrDefault("comment"));
            break;

        case "reject":
            if (!flags.TryGetValue("project", out var rejectProject) ||
                positionals.Count != 1 ||
                !flags.TryGetValue("actor-type", out var rejectActorType) ||
                !flags.TryGetValue("actor-id", out var rejectActorId))
            {
                return Usage(
                    "Usage: motif reject --project <fwdata> <proposalId> --actor-type human|ai --actor-id <name> " +
                    "[--comment <text>]", asJson);
            }
            result = Commands.Reject(
                rejectProject, CliProductVersion(), positionals[0], rejectActorType, rejectActorId,
                flags.GetValueOrDefault("comment"));
            break;

        case "supersede":
            if (!flags.TryGetValue("project", out var supersedeProject) || positionals.Count != 2)
                return Usage("Usage: motif supersede --project <fwdata> <proposalId> <supersededByProposalId>", asJson);
            result = Commands.Supersede(supersedeProject, CliProductVersion(), positionals[0], positionals[1]);
            break;

        case "list":
            if (!flags.TryGetValue("project", out var listProject))
                return Usage("Usage: motif list --project <fwdata> [--json]", asJson);
            result = asJson
                ? Commands.ListJson(listProject, CliProductVersion(), usage)
                : Commands.List(listProject, CliProductVersion(), usage);
            break;

        case "show":
            if (!flags.TryGetValue("project", out var showProject) || positionals.Count != 1)
                return Usage("Usage: motif show --project <fwdata> <proposalId> [--json]", asJson);
            result = asJson
                ? Commands.ShowJson(showProject, CliProductVersion(), positionals[0], usage)
                : Commands.Show(showProject, CliProductVersion(), positionals[0], usage);
            break;

        case "dry-run":
            if (positionals.Count != 1 || !flags.TryGetValue("project", out var dryRunProject))
            {
                return Usage(
                    "Usage: motif dry-run --project <fwdata> <proposalId> [--wait] [--json]", asJson);
            }
            result = JobCommands.EnqueueDryRun(dryRunProject, CliProductVersion(), positionals[0], usage);
            if (result.ExitCode == 0 && flags.ContainsKey("wait"))
            {
                var dryRunJobId = result.Output.Trim();
                var waitTimeout = flags.TryGetValue("wait-timeout-ms", out var waitTimeoutRaw) &&
                    int.TryParse(waitTimeoutRaw, out var waitTimeoutMs)
                    ? TimeSpan.FromMilliseconds(waitTimeoutMs)
                    : JobCommands.DefaultWaitTimeout;
                result = JobCommands.WaitForDryRun(
                    dryRunProject, CliProductVersion(), positionals[0], dryRunJobId, asJson, waitTimeout);
            }
            break;

        case "apply":
            if (positionals.Count != 1 ||
                !flags.TryGetValue("project", out var applyProject) ||
                !flags.TryGetValue("user", out var applyUser))
            {
                return Usage("Usage: motif apply <proposalId> --project <fwdata> --user <name> [--json]", asJson);
            }
            result = asJson
                ? Commands.ApplyJson(applyProject, CliProductVersion(), positionals[0], applyUser, usage)
                : Commands.Apply(applyProject, CliProductVersion(), positionals[0], applyUser, usage);
            break;

        case "log":
            if (!flags.TryGetValue("project", out var logProject))
                return Usage("Usage: motif log --project <fwdata> [--json]", asJson);
            result = asJson ? Commands.LogJson(logProject, usage) : Commands.Log(logProject, usage);
            break;

        case "add-corpus":
            if (!flags.TryGetValue("project", out var addCorpusProject) ||
                !flags.TryGetValue("id", out var corpusId) ||
                !flags.TryGetValue("description", out var corpusDescription) ||
                !flags.TryGetValue("tokeniser", out var corpusTokeniser) ||
                !flags.TryGetValue("tokeniser-version", out var corpusTokeniserVersion))
            {
                return Usage(
                    "Usage: motif add-corpus --project <fwdata> --id <id> --description <text> --tokeniser <name> " +
                    "--tokeniser-version <v> [--uri <url>] [--licence <text>] [--tokeniser-notes <text>] " +
                    "[--may-derive true|false] [--may-redistribute true|false] " +
                    "[--may-use-commercially true|false] [--requires-attribution true|false] " +
                    "[--licence-basis <text>]", asJson);
            }

            result = CorpusCommands.AddCorpus(
                addCorpusProject,
                CliProductVersion(),
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
            if (!flags.TryGetValue("project", out var addDocumentProject) ||
                !flags.TryGetValue("corpus", out var documentCorpus) ||
                !flags.TryGetValue("doc", out var documentId) ||
                !flags.TryGetValue("source", out var documentPathOrUrl))
            {
                return Usage(
                    "Usage: motif add-document --project <fwdata> --corpus <id> --doc <id> " +
                    "--source <file-or-url> [--title <text>] [--licence <text>] [--may-derive true|false] " +
                    "[--licence-basis <text>]", asJson);
            }

            // No flags means "same as corpus", not "nothing established": pass null, not Unknown, or it overrides one.
            var documentCapabilities = HasAnyLicenceFlag(flags)
                ? CorpusCommands.CapabilitiesFromFlags(flags)
                : null;

            result = CorpusCommands.AddDocument(
                addDocumentProject,
                CliProductVersion(),
                documentCorpus,
                documentId,
                documentPathOrUrl,
                flags.GetValueOrDefault("title"),
                flags.GetValueOrDefault("licence"),
                documentCapabilities);
            break;

        case "add-corpus-bundle":
            if (!flags.TryGetValue("project", out var addBundleProject) ||
                !flags.TryGetValue("bundle", out var bundlePath))
                return Usage("Usage: motif add-corpus-bundle --project <fwdata> --bundle <path-to-bundle.json>", asJson);
            result = CorpusCommands.AddBundle(addBundleProject, CliProductVersion(), bundlePath);
            break;

        case "corpora":
            if (!flags.TryGetValue("project", out var corporaProject))
                return Usage("Usage: motif corpora --project <fwdata> [--json]", asJson);
            result = asJson
                ? CorpusCommands.ListCorporaJson(corporaProject, CliProductVersion(), usage)
                : CorpusCommands.ListCorpora(corporaProject, CliProductVersion(), usage);
            break;

        case "show-corpus":
            if (!flags.TryGetValue("project", out var showCorpusProject) || positionals.Count != 1)
                return Usage("Usage: motif show-corpus --project <fwdata> <corpusId> [--json]", asJson);
            result = asJson
                ? CorpusCommands.ShowCorpusJson(showCorpusProject, CliProductVersion(), positionals[0], usage)
                : CorpusCommands.ShowCorpus(showCorpusProject, CliProductVersion(), positionals[0], usage);
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

    // One process is one call; the machine store is what accumulates a session (ADR 0021 decision 4).
    if (usage.Entries.Count > 0)
    {
        using var machine = MachineDatabase.Open(RunnerOptions.ResolveRoot());
        var machineUsage = new MachineUsageLog(machine);
        foreach (var entry in usage.Entries)
            machineUsage.Append(entry);
    }

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
    "--current-grammar-sha256 <sha256> [--json]";

static void PrintUsage(TextWriter writer)
{
    writer.WriteLine("Usage: motif <command> [options]");
    writer.WriteLine();
    writer.WriteLine("Commands:");
    writer.WriteLine("  open <fwdata> [--json]");
    writer.WriteLine("  analyses --project <fwdata> [--json]");
    writer.WriteLine(
        "  analyses --project <fwdata> --assessment <assessmentId> --current-corpus-sha256 <sha256> " +
        "--current-grammar-sha256 <sha256> [--json]");
    writer.WriteLine("  new --project <fwdata> --draft <name> [--label <text>]");
    writer.WriteLine(
        "  add-set-gloss --project <fwdata> --draft <name> --target <canonicalId> --ws <wsTag> --text <text> " +
        "[--depends-on <opId>[,<opId>...]]");
    writer.WriteLine("  add-delete-lexeme-form --project <fwdata> --draft <name> --target <canonicalId>");
    writer.WriteLine(
        "  compose-author-lexeme-form --draft <name> --project <fwdata> --intent " +
        "'{\"entry\":...,\"morphType\":...,\"ws\":...,\"text\":...}'");
    writer.WriteLine(
        "  compose-author-feature-structure --draft <name> --project <fwdata> --intent '{\"msa\":...}'");
    writer.WriteLine(
        "  promote-gloss --project <fwdata> --draft <name> --target <canonicalId> --ws <wsTag> --text <text> " +
        "--corpus <corpusId> [--document <docId>]");
    writer.WriteLine("  label --project <fwdata> --draft <name> <text>");
    writer.WriteLine("  comment --project <fwdata> --draft <name> <text>");
    writer.WriteLine("  finalize --project <fwdata> --draft <name>");
    writer.WriteLine("  reopen --project <fwdata> --draft <name> <proposalId>");
    writer.WriteLine("  duplicate --project <fwdata> --draft <newName> <proposalId>");
    writer.WriteLine(
        "  remove-operations --project <fwdata> --draft <name> <operationId> [<operationId>...] [--force]");
    writer.WriteLine(
        "  split --project <fwdata> <proposalId> <draftName>=<opId>[,<opId>...] " +
        "[<draftName>=<opId>[,<opId>...] ...] [--force]");
    writer.WriteLine("  defer --project <fwdata> <proposalId>");
    writer.WriteLine(
        "  approve --project <fwdata> <proposalId> --actor-type human|ai --actor-id <name> [--comment <text>]");
    writer.WriteLine(
        "  reject --project <fwdata> <proposalId> --actor-type human|ai --actor-id <name> [--comment <text>]");
    writer.WriteLine("  supersede --project <fwdata> <proposalId> <supersededByProposalId>");
    writer.WriteLine("  list --project <fwdata> [--json]");
    writer.WriteLine("  show --project <fwdata> <proposalId> [--json]");
    writer.WriteLine("  dry-run --project <fwdata> <proposalId> [--wait] [--json]");
    writer.WriteLine("  apply <proposalId> --project <fwdata> --user <name> [--json]");
    writer.WriteLine("  log --project <fwdata> [--json]");
    writer.WriteLine();
    writer.WriteLine("Corpus (text Motif measures against; never part of the FieldWorks project):");
    writer.WriteLine(
        "  add-corpus --project <fwdata> --id <id> --description <text> --tokeniser <name> " +
        "--tokeniser-version <v> [--uri <url>] [--licence <text>] [--tokeniser-notes <text>] " +
        "[--may-derive true|false] [--may-redistribute true|false] [--may-use-commercially true|false] " +
        "[--licence-basis <text>]");
    writer.WriteLine(
        "  add-document --project <fwdata> --corpus <id> --doc <id> --source <file-or-url> " +
        "[--title <text>] [--licence <text>] [--may-derive true|false] [--licence-basis <text>]");
    writer.WriteLine(
        "  add-corpus-bundle --project <fwdata> --bundle <path>   (the handoff a fetching tool writes)");
    writer.WriteLine("  corpora --project <fwdata> [--json]");
    writer.WriteLine("  show-corpus --project <fwdata> <corpusId> [--json]");
    writer.WriteLine();
    writer.WriteLine("Global options: --json  (structured output; supported by " +
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

/// <summary>Upserts a named project into the machine store's <c>KnownProjects</c>.</summary>
static void RecordKnownProject(string fwDataPath)
{
    try
    {
        var fullPath = Path.GetFullPath(fwDataPath);
        if (!File.Exists(fullPath)) return;

        var project = new ProjectLocator(fullPath, Path.GetFileNameWithoutExtension(fullPath));
        using var machine = MachineDatabase.Open(RunnerOptions.ResolveRoot());
        new KnownProjectRegistry(machine).Record(
            ProjectWorkspaceKey.Compute(project), project.FullFwDataPath, DateTimeOffset.UtcNow);
    }
    catch (Exception exception) when (
        exception is ArgumentException or IOException or InvalidDataException or NotSupportedException)
    {
        // Reported, not thrown: an unregistered project is never swept, so silence would hide lost work.
        Console.Error.WriteLine("warning: this project could not be recorded for background work (" +
            exception.Message + "). Queued jobs will not run until it is.");
    }
}
