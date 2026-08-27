using System.Globalization;
using System.Text.Json;

namespace SIL.Motif.FakePanGloss;

/// <summary>
/// Stands in for the real <c>pangloss</c> executable at Motif's process boundary.
/// </summary>
/// <remarks>
/// <para>
/// Motif's whole dependency on the parser is: hand it an exported candidate, wait, read the report it
/// wrote. This honours that contract — <c>assess &lt;grammarSource&gt; --report &lt;path&gt;</c> — and
/// nothing else, so a test can exercise the real <see cref="System.Diagnostics.Process"/> boundary
/// without a Rust build or a grammar a parser would accept.
/// </para>
/// <para>
/// Behaviour is read from <c>_fake-pangloss.json</c> beside the grammar source rather than from the
/// environment. A test owns the candidate directory it exports into, so control travels with the
/// invocation instead of leaking through a variable that outlives it — which matters because xUnit runs
/// test classes in parallel and an environment variable is process-wide.
/// </para>
/// </remarks>
internal static class Program
{
    internal const string BehaviourFileName = "_fake-pangloss.json";

    private static int Main(string[] args)
    {
        if (args.Length < 4 || args[0] != "assess" || args[2] != "--report")
        {
            Console.Error.WriteLine("usage: pangloss assess <grammarSource> --report <path>");
            return 64;
        }

        var grammarSource = args[1];
        var reportPath = args[3];
        var behaviour = Behaviour.Read(Path.GetDirectoryName(Path.GetFullPath(grammarSource)));

        if (behaviour.HeartbeatPath is { } heartbeat) return Tick(heartbeat);

        if (behaviour.DelayMilliseconds > 0)
            Thread.Sleep(behaviour.DelayMilliseconds);

        switch (behaviour.Mode)
        {
            case "noReport":
                // Exits cleanly having written nothing: the caller must not read success from the code alone.
                return behaviour.ExitCode;
            case "malformedReport":
                File.WriteAllText(reportPath, "{ this is not json");
                return behaviour.ExitCode;
            case "fail":
                Console.Error.WriteLine(behaviour.StandardError ?? "the fake parser was told to fail");
                return behaviour.ExitCode == 0 ? 1 : behaviour.ExitCode;
            default:
                File.WriteAllText(reportPath, Report(behaviour, grammarSource));
                return behaviour.ExitCode;
        }
    }

    /// Ticks forever so a caller can prove that cancelling it actually stops the process.
    private static int Tick(string heartbeatPath)
    {
        for (var counter = 1; ; counter++)
        {
            File.WriteAllText(heartbeatPath, counter.ToString(CultureInfo.InvariantCulture));
            Thread.Sleep(50);
        }
    }

    private static string Report(Behaviour behaviour, string grammarSource)
    {
        var keys = new List<string>();
        var cases = new List<object>();
        foreach (var word in behaviour.Words)
        {
            var morphemes = new List<int>();
            foreach (var morpheme in word.Morphemes)
            {
                var index = keys.IndexOf(morpheme);
                if (index < 0) { keys.Add(morpheme); index = keys.Count - 1; }
                morphemes.Add(index);
            }
            cases.Add(new
            {
                input = word.Word,
                outcome = word.Outcome,
                analyses = morphemes.Count == 0
                    ? Array.Empty<object>()
                    : [new { identity = new { morphemes, rootIndex = 0 }, identityDigest = word.Word + "-digest" }],
            });
        }

        return JsonSerializer.Serialize(new
        {
            keyTable = keys,
            cases,
            outcomeDigest = behaviour.OutcomeDigest,
            semanticDigest = behaviour.SemanticDigest,
            provenance = new
            {
                sourceSha256 = behaviour.SourceSha256,
                modelFingerprint = behaviour.ModelFingerprint,
            },
            execution = new { pipeline = behaviour.Pipeline },
            diagnostics = Enumerable.Range(0, behaviour.DiagnosticCount)
                .Select(i => new { message = "fake diagnostic " + i }).ToArray(),
            // Recorded so a test can prove the parser was handed the file the exporter actually produced.
            fakeGrammarSource = Path.GetFileName(grammarSource),
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private sealed record FakeWord(string Word, string Outcome, IReadOnlyList<string> Morphemes);

    private sealed record Behaviour
    {
        public string Mode { get; init; } = "succeed";
        public int ExitCode { get; init; }
        public int DelayMilliseconds { get; init; }
        public string? HeartbeatPath { get; init; }
        public string? StandardError { get; init; }
        public string Pipeline { get; init; } = "foma-confirm";
        public string OutcomeDigest { get; init; } = "sha256:" + new string('a', 64);
        public string SemanticDigest { get; init; } = "sha256:" + new string('b', 64);
        public string SourceSha256 { get; init; } = "sha256:" + new string('c', 64);
        public string ModelFingerprint { get; init; } = "fp-1";
        public int DiagnosticCount { get; init; }
        public IReadOnlyList<FakeWord> Words { get; init; } =
            [new FakeWord("motifa", "complete", ["11111111-1111-1111-1111-111111111111"])];

        internal static Behaviour Read(string? directory)
        {
            if (directory is null) return new Behaviour();
            var path = Path.Combine(directory, BehaviourFileName);
            if (!File.Exists(path)) return new Behaviour();
            return JsonSerializer.Deserialize<Behaviour>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Behaviour();
        }
    }
}
