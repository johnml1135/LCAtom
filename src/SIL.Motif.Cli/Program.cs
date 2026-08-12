using System;
using System.Collections.Generic;
using System.IO;
using SIL.Motif.Cli;

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

    CommandResult result;

    switch (verb)
    {
        case "open":
            if (positionals.Count != 1)
                return Usage("Usage: motif open <path-to-.fwdata>");
            result = Commands.Open(positionals[0]);
            break;

        case "new":
            if (!flags.TryGetValue("draft", out var newDraftName))
                return Usage("Usage: motif new --draft <name> [--label <text>]");
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
                    "[--depends-on <opId>[,<opId>...]]");
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
                return Usage("Usage: motif add-delete-lexeme-form --draft <name> --target <canonicalId>");
            }
            result = Commands.AddDeleteLexemeForm(storeDir, addDelDraftName, addDelTarget);
            break;

        case "label":
            if (!flags.TryGetValue("draft", out var labelDraftName) || positionals.Count != 1)
                return Usage("Usage: motif label --draft <name> <text>");
            result = Commands.Label(storeDir, labelDraftName, positionals[0]);
            break;

        case "comment":
            if (!flags.TryGetValue("draft", out var commentDraftName) || positionals.Count != 1)
                return Usage("Usage: motif comment --draft <name> <text>");
            result = Commands.Comment(storeDir, commentDraftName, positionals[0]);
            break;

        case "finalize":
            if (!flags.TryGetValue("draft", out var finalizeDraftName))
                return Usage("Usage: motif finalize --draft <name>");
            result = Commands.Finalize(storeDir, finalizeDraftName);
            break;

        case "reopen":
            if (!flags.TryGetValue("draft", out var reopenDraftName) || positionals.Count != 1)
                return Usage("Usage: motif reopen --draft <name> <proposalId>");
            result = Commands.Reopen(storeDir, reopenDraftName, positionals[0]);
            break;

        case "duplicate":
            if (!flags.TryGetValue("draft", out var dupDraftName) || positionals.Count != 1)
                return Usage("Usage: motif duplicate --draft <newName> <proposalId>");
            result = Commands.Duplicate(storeDir, positionals[0], dupDraftName);
            break;

        case "remove-operations":
            if (!flags.TryGetValue("draft", out var removeDraftName) || positionals.Count == 0)
                return Usage("Usage: motif remove-operations --draft <name> <operationId> [<operationId>...] [--force]");
            var removeForce = flags.TryGetValue("force", out var removeForceRaw) && IsTruthyFlag(removeForceRaw);
            result = Commands.RemoveOperations(storeDir, removeDraftName, positionals, removeForce);
            break;

        case "split":
            if (positionals.Count < 2)
            {
                return Usage(
                    "Usage: motif split <proposalId> <draftName>=<opId>[,<opId>...] " +
                    "[<draftName>=<opId>[,<opId>...] ...] [--force]");
            }
            var splitForce = flags.TryGetValue("force", out var splitForceRaw) && IsTruthyFlag(splitForceRaw);
            var splitGroups = new List<Commands.SplitGroup>();
            foreach (var spec in positionals.Skip(1))
            {
                var eq = spec.IndexOf('=');
                if (eq <= 0 || eq == spec.Length - 1)
                {
                    return Usage(
                        $"Invalid split group '{spec}'. Expected '<draftName>=<opId>[,<opId>...]'.");
                }
                var groupDraftName = spec[..eq];
                var groupOpIds = spec[(eq + 1)..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                splitGroups.Add(new Commands.SplitGroup(groupDraftName, groupOpIds));
            }
            result = Commands.Split(storeDir, positionals[0], splitGroups, splitForce);
            break;

        case "list":
            result = Commands.List(storeDir);
            break;

        case "show":
            if (positionals.Count != 1)
                return Usage("Usage: motif show <proposalId>");
            result = Commands.Show(storeDir, positionals[0]);
            break;

        case "dry-run":
            if (positionals.Count != 1 || !flags.TryGetValue("project", out var dryRunProject))
                return Usage("Usage: motif dry-run <proposalId> --project <fwdata>");
            result = Commands.DryRun(storeDir, positionals[0], dryRunProject);
            break;

        case "apply":
            if (positionals.Count != 1 ||
                !flags.TryGetValue("project", out var applyProject) ||
                !flags.TryGetValue("user", out var applyUser))
            {
                return Usage("Usage: motif apply <proposalId> --project <fwdata> --user <name>");
            }
            result = Commands.Apply(storeDir, positionals[0], applyProject, applyUser);
            break;

        case "log":
            if (!flags.TryGetValue("project", out var logProject))
                return Usage("Usage: motif log --project <fwdata>");
            result = Commands.Log(logProject);
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
                    "[--licence-basis <text>]");
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
                    "[--title <text>] [--licence <text>] [--may-derive true|false] [--licence-basis <text>]");
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
                return Usage("Usage: motif add-corpus-bundle --bundle <path-to-bundle.json>");
            result = CorpusCommands.AddBundle(storeDir, bundlePath);
            break;

        case "corpora":
            result = CorpusCommands.ListCorpora(storeDir);
            break;

        case "show-corpus":
            if (positionals.Count != 1)
                return Usage("Usage: motif show-corpus <corpusId>");
            result = CorpusCommands.ShowCorpus(storeDir, positionals[0]);
            break;

        default:
            Console.Error.WriteLine($"Unknown command '{verb}'.");
            PrintUsage(Console.Error);
            return 1;
    }

    var stream = result.ExitCode == 0 ? Console.Out : Console.Error;
    stream.Write(result.Output);
    return result.ExitCode;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static int Usage(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static void PrintUsage(TextWriter writer)
{
    writer.WriteLine("Usage: motif <command> [options]");
    writer.WriteLine();
    writer.WriteLine("Commands:");
    writer.WriteLine("  open <fwdata>");
    writer.WriteLine("  new --draft <name> [--label <text>]");
    writer.WriteLine(
        "  add-set-gloss --draft <name> --target <canonicalId> --ws <wsTag> --text <text> " +
        "[--depends-on <opId>[,<opId>...]]");
    writer.WriteLine("  add-delete-lexeme-form --draft <name> --target <canonicalId>");
    writer.WriteLine("  label --draft <name> <text>");
    writer.WriteLine("  comment --draft <name> <text>");
    writer.WriteLine("  finalize --draft <name>");
    writer.WriteLine("  reopen --draft <name> <proposalId>");
    writer.WriteLine("  duplicate --draft <newName> <proposalId>");
    writer.WriteLine("  remove-operations --draft <name> <operationId> [<operationId>...] [--force]");
    writer.WriteLine(
        "  split <proposalId> <draftName>=<opId>[,<opId>...] [<draftName>=<opId>[,<opId>...] ...] [--force]");
    writer.WriteLine("  list");
    writer.WriteLine("  show <proposalId>");
    writer.WriteLine("  dry-run <proposalId> --project <fwdata>");
    writer.WriteLine("  apply <proposalId> --project <fwdata> --user <name>");
    writer.WriteLine("  log --project <fwdata>");
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
    writer.WriteLine("  corpora");
    writer.WriteLine("  show-corpus <corpusId>");
    writer.WriteLine();
    writer.WriteLine("Global option: --store <dir>  (default: ./.motif)");
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

static bool IsTruthyFlag(string value) => !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
