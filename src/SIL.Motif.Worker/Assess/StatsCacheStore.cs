using SIL.Motif.Host.Assess;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Worker.Assess;

/// <summary>
/// Where an Assessor's stats cache lives on disk: a file, under the worker root beside Baselines, keyed by
/// grammar digest, Assessor and engine (ADR 0041 decision 9, ADR 0042 decision 8).
/// </summary>
/// <remarks>
/// <para>
/// The path is built from the three key parts as readable segments rather than one opaque hash, so a cache
/// can be found on disk without a database open — the same instinct <c>BaselineWorkspaceCatalog</c> follows
/// for a project's Baseline. Folding the engine into the path is what keeps two engines from ever sharing
/// one file: PanGloss's own cache treats mixed engines as invalid, and giving each engine its own path
/// means that condition is never something Motif's own run has to encounter.
/// </para>
/// <para>
/// This type only resolves the path. The digest an Assessment records alongside it is a hash of what the
/// Assessor actually wrote, computed by the caller after the write — the same order <c>BaselineRefresh</c>
/// uses, and for the same reason: trust the bytes on disk, not the exit code that produced them.
/// </para>
/// </remarks>
public sealed class StatsCacheStore : IAssessorCachePathResolver
{
    private const string RootSegment = "stats-cache";

    private readonly IWorkspaceOwnership _ownership;

    public StatsCacheStore(IWorkspaceOwnership ownership) =>
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));

    /// <inheritdoc />
    public string PathFor(string grammarSourceSha256, string assessor, string engine)
    {
        if (string.IsNullOrWhiteSpace(grammarSourceSha256))
            throw new ArgumentException("Required.", nameof(grammarSourceSha256));

        var path = Path.Combine(_ownership.WorkerRoot, RootSegment,
            SafeSegment(assessor, nameof(assessor)),
            SafeSegment(engine, nameof(engine)),
            GrammarFileName(grammarSourceSha256));

        if (!_ownership.IsOwned(path))
        {
            throw new InvalidOperationException(
                "The stats cache path is refused: it resolves outside the worker-owned root or through a " +
                "reparse point.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static string GrammarFileName(string grammarSourceSha256)
    {
        var body = grammarSourceSha256.StartsWith("sha256:", StringComparison.Ordinal)
            ? grammarSourceSha256["sha256:".Length..]
            : grammarSourceSha256;
        return SafeSegment(body, nameof(grammarSourceSha256)) + ".sqlite";
    }

    // One safe path segment: no separators, no drive marker, no "." or "..".
    private static string SafeSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) >= 0)
            throw new ArgumentException($"'{value}' is not a safe path segment.", parameterName);
        return value;
    }
}
