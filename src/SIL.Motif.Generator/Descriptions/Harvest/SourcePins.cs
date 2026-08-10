using SIL.Motif.Generator.Tsv;

namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// Reads and writes <c>manifest/source-pins.tsv</c>, and answers the one question that file exists for:
/// <b>have the upstream sources moved since the descriptions were harvested from them?</b>
/// </summary>
/// <remarks>
/// <para>
/// The rule the repository owner asked for: descriptions are generated from a pinned release; if the
/// checkout has moved on, the refresh <b>fails</b> and says to upgrade deliberately, and when you do, the
/// run reports which sentences drifted. The failure is the point — a description harvested from a newer
/// liblcm than the one pinned is not wrong, but nobody has looked at what changed, and "nobody looked" is
/// how a reworded upstream sentence quietly replaces the one a reviewer signed off on.
/// </para>
/// <para>
/// <b>Both repos sit past a tag today</b> (FieldWorks at <c>build-1448-39-g41bf33b61</c>, liblcm at
/// <c>FieldWorks9.3.7-beta-l10n-18-gd564a719</c>), so this compares the full <c>describe --long</c> string
/// and the commit rather than pretending a bare tag name identifies the state.
/// </para>
/// </remarks>
public static class SourcePins
{
    public static readonly string[] Header = ["Source", "Kind", "Release", "Commit", "HarvestedUtc"];

    public static void Write(string path, IEnumerable<SourceRelease> pins) =>
        QuotedTsv.Write(path, Header, pins
            .OrderBy(p => p.Source, StringComparer.Ordinal)
            .Select(p => new[] { p.Source, p.Kind, p.Release, p.Commit, p.HarvestedUtc }));

    /// <summary>
    /// A missing file reads as "nothing is pinned yet", so the first run pins rather than failing. Once the
    /// file exists, a source missing from it is still reported as a move — see <see cref="Compare"/>.
    /// </summary>
    public static IReadOnlyList<SourceRelease> Read(string path) =>
        File.Exists(path)
            ? QuotedTsv.Read(path, Header).Select(c => new SourceRelease(c[0], c[1], c[2], c[3], c[4])).ToList()
            : [];

    /// <summary>
    /// Compares the current state of each source against what is pinned.
    /// </summary>
    /// <returns>
    /// One entry per source that has moved, or is newly pinned. An empty result means every source is
    /// exactly where the checked-in descriptions were harvested from.
    /// </returns>
    public static IReadOnlyList<SourceMove> Compare(
        IReadOnlyList<SourceRelease> pinned, IReadOnlyList<SourceRelease> current)
    {
        var pinnedBySource = pinned.ToDictionary(p => p.Source, StringComparer.Ordinal);
        var moves = new List<SourceMove>();

        foreach (var now in current)
        {
            if (!pinnedBySource.TryGetValue(now.Source, out var before))
            {
                moves.Add(new SourceMove(now.Source, PinnedRelease: null, Current: now));
                continue;
            }

            if (!before.SameStateAs(now))
                moves.Add(new SourceMove(now.Source, before, now));
        }

        return moves;
    }

    /// <summary>
    /// The message a refresh fails with when a source has moved. Separate from the throw site so a test can
    /// assert on the wording — this is the text that has to tell someone what to do next, and "upgrade to
    /// the newest release" is only useful if it also says which repo, from what, to what, and how to accept.
    /// </summary>
    public static string DescribeMoves(IReadOnlyList<SourceMove> moves, string pinsPath)
    {
        var lines = moves.Select(m => m.PinnedRelease is null
            ? $"{m.Source}: not pinned yet; now at {m.Current.Describe()}"
            : $"{m.Source}: pinned at {m.PinnedRelease.Describe()}, checkout is now at {m.Current.Describe()}");

        return
            $"{moves.Count} description source(s) have moved since the descriptions were harvested from " +
            $"them:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", lines) +
            Environment.NewLine +
            "Every cited description in manifest/kind-descriptions.tsv was copied from the pinned release, " +
            "so re-harvesting against a newer one can silently replace a sentence a reviewer already read. " +
            "Upgrade to the newest release deliberately: re-run with --accept-source-move, which re-pins " +
            $"{pinsPath} and reports every description whose text drifted.";
    }
}

/// <param name="PinnedRelease">What the descriptions were harvested from, or <c>null</c> for a source
/// that has no pin yet.</param>
/// <param name="Current">Where the checkout is now.</param>
public sealed record SourceMove(string Source, SourceRelease? PinnedRelease, SourceRelease Current);
