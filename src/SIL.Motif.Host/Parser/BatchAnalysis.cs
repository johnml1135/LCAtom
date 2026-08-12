using System.Globalization;

namespace SIL.Motif.Host.Parser;

/// <summary>What happened to one word. Kept distinct because a grammar coverage figure that merges them is not a
/// figure about the grammar.</summary>
public enum WordOutcome
{
    /// <summary>The parser produced at least one analysis.</summary>
    Analysed,

    /// <summary>The parser found no analysis. **This is the real signal** — a genuine gap in the grammar.</summary>
    NoAnalysis,

    /// <summary>
    /// The per-word deadline expired. **Not a failure**, and never to be counted as one: it is a fact about
    /// the machine, the thread count and the cap, not about the grammar. A figure containing any of these
    /// is a lower bound.
    /// </summary>
    TimedOut,

    /// <summary>The parser declined to attempt the word — not an analysis result at all.</summary>
    Skipped,
}

/// <summary>One row of a batch run.</summary>
public sealed record WordAnalysis(int Index, string Word, int ElapsedMs, WordOutcome Outcome, string Signature);

/// <summary>
/// A completed batch run, with the provenance a grammar coverage figure is required to carry
/// (ADR 0032 §4).
/// </summary>
/// <remarks>
/// <see cref="TimedOut"/> is surfaced beside the counts on purpose: a caller computing grammar coverage must be able
/// to see that its number is a lower bound without inspecting every row, and a caller that ignores it produces
/// a figure that moves when the machine is busy.
/// </remarks>
public sealed record BatchAnalysis(
    IReadOnlyList<WordAnalysis> Words,
    ParserEngine Engine,
    int? PerWordTimeoutMs,
    string ProjectPath,
    IReadOnlyList<string> Warnings)
{
    public int Analysed => Words.Count(w => w.Outcome == WordOutcome.Analysed);
    public int NoAnalysis => Words.Count(w => w.Outcome == WordOutcome.NoAnalysis);
    public int TimedOut => Words.Count(w => w.Outcome == WordOutcome.TimedOut);
    public int Skipped => Words.Count(w => w.Outcome == WordOutcome.Skipped);

    /// <summary>
    /// Words the parser actually reached a verdict on — the only honest denominator for grammar coverage, since a
    /// timed-out or skipped word has no verdict either way.
    /// </summary>
    public int Adjudicated => Analysed + NoAnalysis;

    /// <summary>
    /// <c>true</c> when any word timed out, so any grammar coverage figure derived from this run is a **lower bound**
    /// and must say so rather than presenting itself as a measurement.
    /// </summary>
    public bool IsLowerBound => TimedOut > 0;
}

/// <summary>
/// Parses <c>pangloss batch</c>'s TSV output. Deliberately separate from the process invocation so the
/// mapping from the parser's vocabulary to Motif's can be tested against captured real output with no
/// executable present.
/// </summary>
public static class BatchTsvParser
{
    /// <summary>
    /// Reads <c>idx\tword\tms\tstatus\tsignature</c> rows. Unknown statuses throw rather than defaulting:
    /// silently bucketing an unrecognised status as a failure would shift a grammar coverage number
    /// without anyone noticing, so a new parser status must break the build loudly instead.
    /// </summary>
    public static IReadOnlyList<WordAnalysis> Parse(string tsv)
    {
        var results = new List<WordAnalysis>();

        foreach (var raw in tsv.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            var columns = line.Split('\t');
            if (columns.Length < 5) continue; // the STARTED progress marker and any header

            // The STARTED marker reuses the row shape with a non-numeric elapsed column.
            if (!int.TryParse(columns[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var elapsedMs))
                continue;

            if (!int.TryParse(columns[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                continue;

            results.Add(new WordAnalysis(
                Index: index,
                Word: columns[1],
                ElapsedMs: elapsedMs,
                Outcome: ToOutcome(columns[3], columns[4]),
                Signature: columns[4]));
        }

        return results;
    }

    private static WordOutcome ToOutcome(string status, string signature) => status switch
    {
        "ok" => WordOutcome.Analysed,
        "TIMEOUT" => WordOutcome.TimedOut,
        "SKIPPED" => WordOutcome.Skipped,
        "none" or "NONE" or "no-analysis" => WordOutcome.NoAnalysis,
        _ => throw new InvalidOperationException(
            $"Unrecognised parser status '{status}' (signature '{signature}'). Add it to " +
            $"{nameof(BatchTsvParser)} deliberately: bucketing an unknown status as a failure would move " +
            "grammar coverage numbers silently."),
    };
}
