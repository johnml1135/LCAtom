using System.Diagnostics;

namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// Which release of an upstream source a set of descriptions was copied from. Every sentence in
/// <c>manifest/kind-descriptions.tsv</c> that carries a citation was true of <em>some</em> version of
/// liblcm or FieldWorks; without recording which, "cited" degrades over time into "cited against whatever
/// happened to be checked out that day", and a silently reworded upstream sentence changes the text a
/// reviewer relies on without anyone deciding to change it.
/// </summary>
/// <param name="Source">The source's name, matching the <c>Source</c> column of a description row's
/// citation family — <c>liblcm</c> or <c>FieldWorks</c>.</param>
/// <param name="Kind">
/// <c>git-checkout</c> when the release is a <c>git describe</c> of a working tree, or
/// <c>nuget-package</c> when the artifact came from a restored package rather than a checkout — which is
/// how <see cref="ModelSource.ModelPathResolver"/> normally finds <c>MasterLCModel.xml</c>.
/// </param>
/// <param name="Release">
/// <c>git describe --tags --long --always --dirty</c> for a checkout, e.g.
/// <c>FieldWorks9.3.7-beta-l10n-18-gd564a719</c> — deliberately the <c>--long</c> form, because both source
/// repos sit some commits past their most recent tag, and a bare <c>describe</c> would hide that. For a
/// package, the package version.
/// </param>
/// <param name="Commit">The exact commit, or empty for a package (whose version already identifies it).</param>
/// <param name="HarvestedUtc">When the descriptions were last harvested from this release, ISO-8601.</param>
public sealed record SourceRelease(
    string Source,
    string Kind,
    string Release,
    string Commit,
    string HarvestedUtc)
{
    public const string GitCheckoutKind = "git-checkout";
    public const string NuGetPackageKind = "nuget-package";

    /// <summary>
    /// Whether this is the same upstream state as <paramref name="other"/>. Compares release and commit
    /// but not <see cref="HarvestedUtc"/>: re-running the harvest against an unmoved checkout is not a move.
    /// </summary>
    public bool SameStateAs(SourceRelease other) =>
        string.Equals(Release, other.Release, StringComparison.Ordinal) &&
        string.Equals(Commit, other.Commit, StringComparison.Ordinal);

    public string Describe() => Commit.Length > 0 ? $"{Release} ({Commit[..Math.Min(12, Commit.Length)]})" : Release;
}

/// <summary>
/// Reads a checkout's current release with <c>git describe</c>. Used only by the dev-time harvest/refresh
/// commands; nothing in the build shells out to git.
/// </summary>
public static class GitRelease
{
    public static SourceRelease Read(string source, string checkoutPath, DateTime utcNow)
    {
        var describe = RunGit(checkoutPath, "describe", "--tags", "--long", "--always", "--dirty");
        var commit = RunGit(checkoutPath, "rev-parse", "HEAD");

        return new SourceRelease(
            source,
            SourceRelease.GitCheckoutKind,
            describe,
            commit,
            utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new GeneratorException($"Could not start git in '{workingDirectory}'.");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new GeneratorException(
                    $"`git {string.Join(' ', arguments)}` failed in '{workingDirectory}' " +
                    $"(exit {process.ExitCode}): {stderr.Trim()}");
            }

            return stdout.Trim();
        }
        catch (Exception ex) when (ex is not GeneratorException)
        {
            throw new GeneratorException(
                $"Could not run `git {string.Join(' ', arguments)}` in '{workingDirectory}': {ex.Message}", ex);
        }
    }
}
