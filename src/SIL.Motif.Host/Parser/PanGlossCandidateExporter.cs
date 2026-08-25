using SIL.LCModel;
using SIL.Motif.Host.LcmUtils;

namespace SIL.Motif.Host.Parser;

/// <summary>
/// Saves a candidate <see cref="LcmCache"/> and copies its backing project into an empty destination
/// directory, so PanGloss can read it without the cache staying open.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hazard this exists to prevent:</b> <see cref="FwDataProjectLoader.Save"/> always writes back to
/// the file the cache was opened from — it takes no destination path. A candidate opened in place from a
/// published Baseline directory (<c>BaselineScratchFactory.OpenSingleUse</c>) is therefore one
/// <c>Save</c> call away from mutating a directory that must survive byte-for-byte. Exporting is
/// save-then-copy, so it can only ever be safe for a candidate whose backing file already lives somewhere
/// disposable.
/// </para>
/// <para>
/// Pinned by `ExportAsync_RefusesACandidateBackedByAPublishedBaselineDirectory_AndLeavesItByteForByteUnchanged`.
/// </para>
/// <para>
/// That is what <paramref name="writableScratchRoot"/> is for at construction: it names the one place a
/// candidate is allowed to live for this to proceed. <see cref="ExportAsync"/> refuses, before touching
/// anything, when the candidate's own backing path falls outside it.
/// </para>
/// </remarks>
public sealed class PanGlossCandidateExporter : IPanGlossCandidateExporter
{
    private readonly string _writableScratchRoot;
    private readonly FwDataProjectLoader _loader;

    /// <param name="writableScratchRoot">
    /// The disposable scratch root every exportable candidate's backing project must live under. A
    /// candidate backed by anything outside it — most importantly a published Baseline directory — is
    /// refused rather than saved.
    /// </param>
    /// <param name="loader">Saves the candidate before it is copied; defaults to a real one.</param>
    public PanGlossCandidateExporter(string writableScratchRoot, FwDataProjectLoader? loader = null)
    {
        if (string.IsNullOrWhiteSpace(writableScratchRoot))
            throw new ArgumentException("Required.", nameof(writableScratchRoot));

        _writableScratchRoot = Path.GetFullPath(writableScratchRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _loader = loader ?? new FwDataProjectLoader();
    }

    /// <inheritdoc />
    public Task ExportAsync(LcmCache candidate, string emptyDestination, CancellationToken cancellationToken)
    {
        if (candidate is null) throw new ArgumentNullException(nameof(candidate));
        if (string.IsNullOrWhiteSpace(emptyDestination))
            throw new ArgumentException("Required.", nameof(emptyDestination));

        cancellationToken.ThrowIfCancellationRequested();

        var candidatePath = candidate.ProjectId.Path;
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            throw new InvalidOperationException(
                "The candidate cache has no backing project path, so it cannot be saved and copied.");
        }

        var candidateFolder = Path.GetDirectoryName(Path.GetFullPath(candidatePath))
            ?? throw new InvalidOperationException(
                $"Could not determine the candidate's project folder from '{candidatePath}'.");

        if (!IsWithinWritableRoot(candidateFolder))
        {
            throw new InvalidOperationException(
                $"Refusing to export a candidate backed by '{candidateFolder}': it is outside the writable " +
                $"scratch root '{_writableScratchRoot}'. Saving it would write back to whatever produced " +
                "it — for a candidate opened in place from a published Baseline, that would mutate a " +
                "directory that must remain byte-for-byte immutable.");
        }

        EnsureGenuinelyEmpty(emptyDestination);

        cancellationToken.ThrowIfCancellationRequested();
        _loader.Save(candidate);

        cancellationToken.ThrowIfCancellationRequested();
        CopyDirectoryContents(candidateFolder, emptyDestination);

        return Task.CompletedTask;
    }

    private bool IsWithinWritableRoot(string candidateFolder)
    {
        string full;
        try { full = Path.GetFullPath(candidateFolder); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return false; }

        var relative = Path.GetRelativePath(_writableScratchRoot, full);
        return relative != "." && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private static void EnsureGenuinelyEmpty(string destination)
    {
        if (!Directory.Exists(destination))
        {
            throw new ArgumentException(
                $"The export destination '{destination}' does not exist.", nameof(destination));
        }

        if (Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new ArgumentException(
                $"The export destination '{destination}' is not empty.", nameof(destination));
        }
    }

    private static void CopyDirectoryContents(string sourceDir, string destinationDir)
    {
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)));

        foreach (var subDir in Directory.GetDirectories(sourceDir))
            CopyDirectory(subDir, Path.Combine(destinationDir, Path.GetFileName(subDir)));
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        CopyDirectoryContents(sourceDir, destinationDir);
    }
}
