using System.Runtime.InteropServices;

namespace SIL.Motif.Host.Parser;

/// <summary>
/// Locates the <c>pangloss</c> executable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Motif shells out to the parser, and that is a deliberate first step rather than the end state.</b> The
/// only route that yields GUID-keyed analyses — the ones Motif can tie back to the entry or rule a Proposal
/// touched — goes through a project file, and the C ABI has no entry point that takes one: <c>hc_grammar_load</c>
/// accepts HermitCrab XML only, whose identities are synthetic and uncorrelatable. See
/// <c>docs/research/2026-08-07-parser-seam-goes-through-the-project-file.md</c>.
/// </para>
/// <para>
/// So a process boundary is the price of correct identities today. It is contained to this file and
/// <see cref="PanGlossParser"/> so that the FFI entry point <c>MOT-15</c> asks for — which scope 2 needs, since
/// FieldWorks must host the parser in-process on <c>net48</c> — can replace it without touching any caller.
/// </para>
/// </remarks>
public static class PanGlossExecutable
{
    /// <summary>Overrides discovery. Set this when the parser lives somewhere unusual, or in CI.</summary>
    public const string PathVariable = "MOTIF_PANGLOSS_EXE";

    private static string FileName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "pangloss.exe" : "pangloss";

    /// <summary>
    /// Returns the executable's path, or <c>null</c> when it cannot be found.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing because "the parser is not built here" is an ordinary state of a
    /// developer's machine, and a missing parser must be distinguishable from a grammar the parser refused —
    /// conflating them is how a build-environment problem gets recorded as a linguistic finding.
    /// </remarks>
    public static string? TryLocate()
    {
        var configured = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? configured : null;

        // The conventional sibling checkout: PanGloss beside motif, release build. Searched relative to the
        // repository root rather than the working directory, which a test runner sets wherever it likes.
        var root = TryFindRepositoryRoot();
        if (root is null) return null;

        var candidate = Path.Combine(
            root, "..", "PanGloss", "rust", "target", "release", FileName);

        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    /// <summary>Walks up looking for the marker files that identify this repository's root.</summary>
    private static string? TryFindRepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "AGENTS.md")) && Directory.Exists(Path.Combine(dir, "manifest")))
                return dir;

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return null;
    }
}
