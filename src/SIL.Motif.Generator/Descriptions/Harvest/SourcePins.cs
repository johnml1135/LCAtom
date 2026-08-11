using SIL.Motif.Generator.Tsv;

namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// Reads and writes <c>manifest/source-pins.tsv</c>, and answers the two questions it exists for:
/// <b>has any file the descriptions were copied from changed?</b> — which stops a refresh — and
/// <b>has either project moved?</b> — which is worth saying out loud and nothing more.
/// </summary>
/// <remarks>
/// <para>
/// The rule the repository owner asked for: descriptions are generated from a pinned release; if the source
/// has moved on, the refresh fails and says to upgrade deliberately, and when you do, the run reports which
/// sentences drifted. The refinement, after the first version fired on an unrelated commit within the hour,
/// is that "moved on" means <em>the bytes we read are different</em>, not <em>the repository has commits we
/// have not seen</em>. A check that fires when nothing relevant happened gets clicked through.
/// </para>
/// <para>
/// So there are three cheap hashes — <c>MasterLCModel.xml</c>, <c>ContextHelp.xml</c>, and the compiled help
/// file — and they change rarely. The <c>.chm</c> is pinned even though <c>refresh-descriptions</c> never
/// opens it, because the checked-in harvest derived from it would otherwise go stale invisibly: a changed
/// digest there means "re-run <c>harvest-help</c>", and nothing else would say so.
/// </para>
/// </remarks>
public static class SourcePins
{
    public static readonly string[] Header =
        ["Source", "Kind", "Release", "Commit", "Artifact", "Sha256", "HarvestedUtc"];

    public static void Write(string path, IEnumerable<SourceArtifact> pins) =>
        QuotedTsv.Write(path, Header, pins
            .OrderBy(p => p.Source, StringComparer.Ordinal)
            .ThenBy(p => p.Artifact, StringComparer.Ordinal)
            .Select(p => new[] { p.Source, p.Kind, p.Release, p.Commit, p.Artifact, p.Sha256, p.HarvestedUtc }));

    /// <summary>
    /// A missing file reads as "nothing is pinned yet", so the first run pins rather than failing. Once the
    /// file exists, an artifact missing from it is still reported — see <see cref="Compare"/>.
    /// </summary>
    public static IReadOnlyList<SourceArtifact> Read(string path) =>
        File.Exists(path)
            ? QuotedTsv.Read(path, Header)
                .Select(c => new SourceArtifact(c[0], c[1], c[2], c[3], c[4], c[5], c[6]))
                .ToList()
            : [];

    /// <summary>
    /// Compares each current artifact against its pin.
    /// </summary>
    /// <returns>
    /// One entry per artifact whose content or release differs. Check
    /// <see cref="SourceMove.ContentChanged"/> to tell the two apart: only that one is a reason to stop.
    /// </returns>
    public static IReadOnlyList<SourceMove> Compare(
        IReadOnlyList<SourceArtifact> pinned, IReadOnlyList<SourceArtifact> current)
    {
        var pinnedByArtifact = pinned.ToDictionary(p => p.Key);
        var moves = new List<SourceMove>();

        foreach (var now in current)
        {
            if (!pinnedByArtifact.TryGetValue(now.Key, out var before))
            {
                moves.Add(new SourceMove(now.Source, now.Artifact, Pinned: null, Current: now));
                continue;
            }

            if (!before.SameContentAs(now) || !before.SameReleaseAs(now))
                moves.Add(new SourceMove(now.Source, now.Artifact, before, now));
        }

        return moves;
    }

    /// <summary>Only the moves that are a reason to stop: a file whose bytes are not the pinned bytes.</summary>
    public static IReadOnlyList<SourceMove> ContentChanges(IReadOnlyList<SourceMove> moves) =>
        moves.Where(m => m.ContentChanged).ToList();

    /// <summary>
    /// The message a refresh fails with. Separate from the throw site so a test can assert on the wording —
    /// this is the text that has to tell someone what to do next, and "upgrade to the newest release" is
    /// only useful if it also says which file, in which project, at which two releases, and how to accept.
    /// </summary>
    public static string DescribeContentChanges(IReadOnlyList<SourceMove> contentChanges, string pinsPath)
    {
        var lines = contentChanges.Select(m => m.Pinned is null
            ? $"{m.Source}: {m.Artifact} is not pinned yet ({m.Current.DescribeRelease()})"
            : $"{m.Source}: {m.Artifact} changed{Environment.NewLine}" +
              $"      pinned at {m.Pinned.DescribeRelease()}, {Shorten(m.Pinned.Sha256)}{Environment.NewLine}" +
              $"      now at    {m.Current.DescribeRelease()}, {Shorten(m.Current.Sha256)}");

        return
            $"{contentChanges.Count} source file(s) have changed since the descriptions were copied out of " +
            $"them:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", lines) +
            Environment.NewLine +
            "Every cited description was copied from the pinned bytes, so re-harvesting against new ones can " +
            "silently replace a sentence a reviewer already read. Upgrade to the newest release " +
            $"deliberately: re-run with --accept-source-move, which re-pins {pinsPath} and reports every " +
            "description whose upstream fragment drifted.";
    }

    /// <summary>
    /// The note a refresh prints when a project moved but every file it reads is byte-identical. Not a
    /// failure, and worth saying anyway: it is the evidence that the check looked and found nothing, which
    /// is what stops the next person from wondering.
    /// </summary>
    public static string DescribeReleaseOnlyMoves(IReadOnlyList<SourceMove> moves)
    {
        var releaseOnly = moves.Where(m => !m.ContentChanged && m.Pinned is not null).ToList();
        if (releaseOnly.Count == 0) return "";

        var lines = releaseOnly.Select(m =>
            $"{m.Source}: {m.Pinned!.DescribeRelease()} -> {m.Current.DescribeRelease()} " +
            $"({m.Artifact} unchanged)");

        return
            $"  {releaseOnly.Count} source(s) moved without changing anything read here:{Environment.NewLine}    " +
            string.Join(Environment.NewLine + "    ", lines);
    }

    private static string Shorten(string sha256) =>
        sha256.StartsWith("sha256:", StringComparison.Ordinal) && sha256.Length >= 19
            ? sha256[..19] + "..."
            : sha256;
}

/// <param name="Pinned">What the descriptions were copied from, or <c>null</c> for an artifact with no pin yet.</param>
/// <param name="Current">The file as it is now.</param>
public sealed record SourceMove(string Source, string Artifact, SourceArtifact? Pinned, SourceArtifact Current)
{
    /// <summary>
    /// The bytes differ — or there is no pin to compare against. This is the only condition that stops a
    /// refresh; a release that moved over unchanged files does not.
    /// </summary>
    public bool ContentChanged => Pinned is null || !Pinned.SameContentAs(Current);
}
