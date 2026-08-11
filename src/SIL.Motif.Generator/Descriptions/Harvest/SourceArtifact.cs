using System.Diagnostics;

namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// One upstream <b>file</b> the descriptions are copied out of, pinned by its content rather than by its
/// repository's position.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the file and not the repo.</b> The first version of this pinned a repository at a commit, and it
/// tripped within the hour: the FieldWorks checkout advanced by one commit that added three unrelated test
/// files, and a refresh refused to run. A pin that fires on every commit to a large repository trains its
/// reader to accept it without looking, which is worse than not having one. What actually matters is whether
/// any of the three files we read has changed — three cheap hashes, and they change rarely.
/// </para>
/// <para>
/// The release and commit are still recorded, because "which release did this sentence come from" is the
/// question a reviewer asks and a content hash cannot answer. They are reported when they move, and they do
/// not fail the run on their own.
/// </para>
/// </remarks>
/// <param name="Source">The upstream project: <c>liblcm</c> or <c>FieldWorks</c>.</param>
/// <param name="Kind">
/// <c>git-checkout</c> when the release comes from a working tree, or <c>nuget-package</c> when the artifact
/// came from a restored package — which is how <see cref="ModelSource.ModelPathResolver"/> normally finds
/// <c>MasterLCModel.xml</c>.
/// </param>
/// <param name="Release">
/// <c>git describe --tags --long --always --dirty</c> for a checkout, e.g.
/// <c>FieldWorks9.3.7-beta-l10n-18-gd564a719</c> — deliberately the <c>--long</c> form, because both source
/// repos sit some commits past their most recent tag and a bare tag would name two different states
/// identically. For a package, the package version.
/// </param>
/// <param name="Commit">The exact commit, or empty for a package, whose version already identifies it.</param>
/// <param name="Artifact">Repo-relative path of the file itself.</param>
/// <param name="Sha256">
/// <see cref="SourceDigest.OfFile"/> of that file. This is the field the check actually turns on.
/// </param>
/// <param name="HarvestedUtc">When the descriptions were last harvested from it, ISO-8601.</param>
public sealed record SourceArtifact(
    string Source,
    string Kind,
    string Release,
    string Commit,
    string Artifact,
    string Sha256,
    string HarvestedUtc)
{
    public const string GitCheckoutKind = "git-checkout";
    public const string NuGetPackageKind = "nuget-package";

    /// <summary>Identity for comparison: the same file of the same project, whatever release it came from.</summary>
    public (string Source, string Artifact) Key => (Source, Artifact);

    /// <summary>Whether the file's bytes are the ones the descriptions were copied from.</summary>
    public bool SameContentAs(SourceArtifact other) =>
        string.Equals(Sha256, other.Sha256, StringComparison.Ordinal);

    /// <summary>Whether the project sits where it sat — reported, but never fatal on its own.</summary>
    public bool SameReleaseAs(SourceArtifact other) =>
        string.Equals(Release, other.Release, StringComparison.Ordinal) &&
        string.Equals(Commit, other.Commit, StringComparison.Ordinal);

    public string DescribeRelease() =>
        Commit.Length > 0 ? $"{Release} ({Commit[..Math.Min(12, Commit.Length)]})" : Release;
}

/// <summary>
/// Reads a checkout's current release with <c>git describe</c>. Used only by the dev-time harvest/refresh
/// commands; nothing in the build shells out to git.
/// </summary>
public static class GitRelease
{
    /// <returns>The <c>describe</c> string and the commit, or empties when the directory is not a checkout.</returns>
    public static (string Release, string Commit) Read(string checkoutPath)
    {
        var describe = RunGit(checkoutPath, "describe", "--tags", "--long", "--always", "--dirty");
        var commit = RunGit(checkoutPath, "rev-parse", "HEAD");
        return (describe, commit);
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
