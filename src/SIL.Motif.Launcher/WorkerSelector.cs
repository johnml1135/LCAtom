using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SIL.Motif.Contract.Runner;

namespace SIL.Motif.Launcher;

/// <summary>Chooses an installed job runner by the database schema it can open, then by version.</summary>
/// <remarks>
/// Compatibility used to mean an overlapping wire protocol range. There is no wire, so it now means the
/// only thing that can actually break a user: whether a runner reaches the schema generation the paired
/// database is at. A runner that supports generation N opens every database up to N and migrates older
/// ones; one that does not is refused before it is started rather than after it fails to open anything.
/// </remarks>
public static class WorkerSelector
{
    /// <summary>Requires an installed registration to agree with compiled build metadata.</summary>
    public static void RequireMatch(RunnerBuildMetadata compiled, InstalledWorker manifest)
    {
        if (compiled is null)
            throw new ArgumentNullException(nameof(compiled));
        if (manifest is null)
            throw new ArgumentNullException(nameof(manifest));
        RunnerBuildMetadata fromManifest;
        try
        {
            fromManifest = new RunnerBuildMetadata(manifest.ProductVersion.ToString(), manifest.SupportedSchema);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentNullException
            or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("The installed runner metadata is invalid.", exception);
        }
        if (!string.Equals(compiled.ProductVersion, fromManifest.ProductVersion, StringComparison.Ordinal) ||
            compiled.SupportedSchema != fromManifest.SupportedSchema)
            throw new InvalidDataException("The installed runner metadata does not match the compiled runner.");
    }

    /// <summary>Returns the newest runner that can open a database at the required generation.</summary>
    public static InstalledWorker SelectNewestCompatible(
        IEnumerable<InstalledWorker> installed, int requiredSchema)
    {
        if (installed is null)
            throw new ArgumentNullException(nameof(installed));
        if (requiredSchema < 1)
            throw new ArgumentOutOfRangeException(nameof(requiredSchema),
                "A required schema generation starts at one.");
        var candidates = installed.ToArray();
        var seenVersions = new HashSet<Version>();
        foreach (var candidate in candidates)
        {
            ValidateCandidate(candidate);
            if (!seenVersions.Add(candidate.ProductVersion))
                throw new InvalidOperationException("The installed runner catalog has an ambiguous product version.");
        }
        var selected = candidates
            .Where(candidate => candidate.SupportedSchema >= requiredSchema)
            .OrderByDescending(candidate => candidate.ProductVersion)
            .FirstOrDefault();
        if (selected is null)
            throw new InvalidOperationException(
                "No installed runner supports schema generation " + requiredSchema + ".");
        return selected;
    }

    private static void ValidateCandidate(InstalledWorker candidate)
    {
        if (candidate is null)
            throw new ArgumentException("An installed runner entry is required.", nameof(candidate));
        if (candidate.ProductVersion is null)
            throw new InvalidOperationException("An installed runner has no product version.");
        if (candidate.SupportedSchema < 1)
            throw new InvalidOperationException("An installed runner has an invalid supported schema.");
        if (string.IsNullOrWhiteSpace(candidate.ExecutablePath))
            throw new InvalidOperationException("An installed runner has no executable path.");
        // A relative path resolves against whatever directory the caller happens to be in.
        if (!Path.IsPathRooted(candidate.ExecutablePath))
            throw new InvalidOperationException("An installed runner path must be absolute.");
    }
}
