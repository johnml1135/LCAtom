using System;
using System.Collections.Generic;
using System.Linq;

namespace SIL.Motif.Projection.Usage;

/// <summary>
/// One recorded call: which surface command ran, when, and the *shape* of its arguments —
/// never their values. See <see cref="UsageArgumentShape"/> for what "shape" means here.
/// </summary>
public sealed record UsageLogEntry(string TimestampUtc, string Command, IReadOnlyList<string> ArgumentShape);

/// <summary>
/// Builds argument-shape tokens that describe an argument's kind and, for a collection, its
/// cardinality — never the value itself. A canonical id, a gloss, and a project path all reduce to
/// <c>"name:text"</c>; only the parameter name and the fact that it was a single piece of text
/// survive.
/// </summary>
public static class UsageArgumentShape
{
    public static string Text(string name) => $"{name}:text";
    public static string List(string name, int count) => $"{name}:list({count})";
    public static string Flag(string name) => $"{name}:flag";
}

/// <summary>How many times each command ran, and how many times one command immediately followed another.</summary>
public sealed record AdjacentCommandPair(string First, string Second, int Count);

/// <param name="CallCounts">How many times each command was recorded.</param>
/// <param name="BackToBack">
/// Every ordered pair of commands that ran one immediately after the other, with how often that
/// pairing occurred — the signal ADR 0021 decision 4 calls "a candidate composite report."
/// </param>
public sealed record UsageSummary(
    IReadOnlyDictionary<string, int> CallCounts,
    IReadOnlyList<AdjacentCommandPair> BackToBack);

/// <summary>
/// A session-scoped record of which read-surface commands were called, how often, and in what
/// sequence — the evidence ADR 0021 decision 4 asks for towards scope 2's screen list. It never
/// carries a project path, entity id, gloss, or any other authored content: every entry is a
/// command name plus its argument shape, built by <see cref="UsageArgumentShape"/>.
/// </summary>
public sealed class UsageLog
{
    private readonly List<UsageLogEntry> _entries = new();

    public IReadOnlyList<UsageLogEntry> Entries => _entries;

    /// <summary>Records one call. <paramref name="argumentShape"/> must never carry an argument's value.</summary>
    public void Record(string command, IReadOnlyList<string> argumentShape) =>
        _entries.Add(new UsageLogEntry(DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ"), command, argumentShape));

    public UsageSummary Summarize()
    {
        var counts = _entries
            .GroupBy(e => e.Command, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var pairCounts = new List<AdjacentCommandPair>();
        for (var i = 0; i < _entries.Count - 1; i++)
        {
            var first = _entries[i].Command;
            var second = _entries[i + 1].Command;
            var index = pairCounts.FindIndex(p =>
                string.Equals(p.First, first, StringComparison.Ordinal) &&
                string.Equals(p.Second, second, StringComparison.Ordinal));

            if (index >= 0)
                pairCounts[index] = pairCounts[index] with { Count = pairCounts[index].Count + 1 };
            else
                pairCounts.Add(new AdjacentCommandPair(first, second, 1));
        }

        return new UsageSummary(counts, pairCounts);
    }
}
