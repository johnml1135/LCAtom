using System;
using System.Collections.Generic;
using System.IO;
using SIL.LCAtom.Cli;

// Thin argument dispatcher: every verb below is a plain call into SIL.LCAtom.Cli.Commands, which
// does all the real work (files store + Contract/Runner/Host calls) and returns a captured exit
// code + output. Keeping Program.cs thin is what lets tests drive the exact same command handlers
// directly, without shelling out to this executable. See docs/build-stages.md, Stage E.

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
        : Path.Combine(Directory.GetCurrentDirectory(), ".lcatom");

    CommandResult result;

    switch (verb)
    {
        case "open":
            if (positionals.Count != 1)
                return Usage("Usage: lcatom open <path-to-.fwdata>");
            result = Commands.Open(positionals[0]);
            break;

        case "new":
            if (!flags.TryGetValue("draft", out var newDraftName))
                return Usage("Usage: lcatom new --draft <name> [--label <text>]");
            result = Commands.New(storeDir, newDraftName, flags.GetValueOrDefault("label"));
            break;

        case "add-set-gloss":
            if (!flags.TryGetValue("draft", out var addDraftName) ||
                !flags.TryGetValue("target", out var addTarget) ||
                !flags.TryGetValue("ws", out var addWs) ||
                !flags.TryGetValue("text", out var addText))
            {
                return Usage(
                    "Usage: lcatom add-set-gloss --draft <name> --target <canonicalId> --ws <wsTag> --text <text>");
            }
            result = Commands.AddSetGloss(storeDir, addDraftName, addTarget, addWs, addText);
            break;

        case "label":
            if (!flags.TryGetValue("draft", out var labelDraftName) || positionals.Count != 1)
                return Usage("Usage: lcatom label --draft <name> <text>");
            result = Commands.Label(storeDir, labelDraftName, positionals[0]);
            break;

        case "comment":
            if (!flags.TryGetValue("draft", out var commentDraftName) || positionals.Count != 1)
                return Usage("Usage: lcatom comment --draft <name> <text>");
            result = Commands.Comment(storeDir, commentDraftName, positionals[0]);
            break;

        case "finalize":
            if (!flags.TryGetValue("draft", out var finalizeDraftName))
                return Usage("Usage: lcatom finalize --draft <name>");
            result = Commands.Finalize(storeDir, finalizeDraftName);
            break;

        case "reopen":
            if (!flags.TryGetValue("draft", out var reopenDraftName) || positionals.Count != 1)
                return Usage("Usage: lcatom reopen --draft <name> <changeSetId>");
            result = Commands.Reopen(storeDir, reopenDraftName, positionals[0]);
            break;

        case "list":
            result = Commands.List(storeDir);
            break;

        case "show":
            if (positionals.Count != 1)
                return Usage("Usage: lcatom show <changeSetId>");
            result = Commands.Show(storeDir, positionals[0]);
            break;

        case "assess":
            if (positionals.Count != 1 || !flags.TryGetValue("project", out var assessProject))
                return Usage("Usage: lcatom assess <changeSetId> --project <fwdata>");
            result = Commands.Assess(storeDir, positionals[0], assessProject);
            break;

        case "apply":
            if (positionals.Count != 1 ||
                !flags.TryGetValue("project", out var applyProject) ||
                !flags.TryGetValue("user", out var applyUser))
            {
                return Usage("Usage: lcatom apply <changeSetId> --project <fwdata> --user <name>");
            }
            result = Commands.Apply(storeDir, positionals[0], applyProject, applyUser);
            break;

        case "log":
            if (!flags.TryGetValue("project", out var logProject))
                return Usage("Usage: lcatom log --project <fwdata>");
            result = Commands.Log(logProject);
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
    writer.WriteLine("Usage: lcatom <command> [options]");
    writer.WriteLine();
    writer.WriteLine("Commands:");
    writer.WriteLine("  open <fwdata>");
    writer.WriteLine("  new --draft <name> [--label <text>]");
    writer.WriteLine("  add-set-gloss --draft <name> --target <canonicalId> --ws <wsTag> --text <text>");
    writer.WriteLine("  label --draft <name> <text>");
    writer.WriteLine("  comment --draft <name> <text>");
    writer.WriteLine("  finalize --draft <name>");
    writer.WriteLine("  reopen --draft <name> <changeSetId>");
    writer.WriteLine("  list");
    writer.WriteLine("  show <changeSetId>");
    writer.WriteLine("  assess <changeSetId> --project <fwdata>");
    writer.WriteLine("  apply <changeSetId> --project <fwdata> --user <name>");
    writer.WriteLine("  log --project <fwdata>");
    writer.WriteLine();
    writer.WriteLine("Global option: --store <dir>  (default: ./.lcatom)");
}

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
            if (i + 1 >= tokens.Length)
                throw new ArgumentException($"Flag '--{name}' requires a value.");
            flags[name] = tokens[++i];
        }
        else
        {
            positionals.Add(token);
        }
    }

    return (flags, positionals);
}
