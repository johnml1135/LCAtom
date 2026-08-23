using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Launcher;

/// <summary>Chooses an installed worker using wire compatibility before product ordering.</summary>
public static class WorkerSelector
{
    /// <summary>Requires an installed registration to agree with compiled build metadata.</summary>
    public static void RequireMatch(WorkerBuildMetadata compiled, InstalledWorker manifest)
    {
        if (compiled is null)
            throw new ArgumentNullException(nameof(compiled));
        if (manifest is null)
            throw new ArgumentNullException(nameof(manifest));
        WorkerBuildMetadata fromManifest;
        try
        {
            fromManifest = new WorkerBuildMetadata(manifest.ProductVersion.ToString(), manifest.Protocols,
                manifest.Capabilities);
        }
        catch (Exception exception) when (exception is ArgumentException || exception is ArgumentNullException)
        {
            throw new InvalidDataException("The installed worker metadata is invalid.", exception);
        }
        if (!string.Equals(compiled.ProductVersion, fromManifest.ProductVersion, StringComparison.Ordinal) ||
            compiled.Protocols.Minimum != fromManifest.Protocols.Minimum ||
            compiled.Protocols.Maximum != fromManifest.Protocols.Maximum ||
            !compiled.Capabilities.SequenceEqual(fromManifest.Capabilities, StringComparer.Ordinal) ||
            !string.Equals(compiled.MetadataDigest, fromManifest.MetadataDigest, StringComparison.Ordinal))
            throw new InvalidDataException("The installed worker metadata does not match the compiled worker.");
    }

    /// <summary>Requires a connected handshake offer to agree with its installed registration.</summary>
    public static void RequireMatch(WorkerHandshakeOffer offer, InstalledWorker manifest)
    {
        if (offer is null)
            throw new ArgumentNullException(nameof(offer));
        if (manifest is null)
            throw new ArgumentNullException(nameof(manifest));
        WorkerBuildMetadata compiled;
        try
        {
            compiled = new WorkerBuildMetadata(offer.ProductVersion, offer.Protocols, offer.Capabilities);
        }
        catch (Exception exception) when (exception is ArgumentException || exception is ArgumentNullException)
        {
            throw new InvalidDataException("The connected worker metadata is invalid.", exception);
        }
        RequireMatch(compiled, manifest);
    }

    /// <summary>Returns the newest worker that overlaps protocol and capability requirements.</summary>
    public static InstalledWorker SelectNewestCompatible(
        IEnumerable<InstalledWorker> installed,
        WorkerHandshakeRequest client)
    {
        if (installed is null)
            throw new ArgumentNullException(nameof(installed));
        if (client is null)
            throw new ArgumentNullException(nameof(client));
        var candidates = installed.ToArray();
        var seenVersions = new HashSet<Version>();
        foreach (var candidate in candidates)
        {
            ValidateCandidate(candidate);
            if (!seenVersions.Add(candidate.ProductVersion))
                throw new InvalidOperationException("The installed worker catalog has an ambiguous product version.");
        }
        var required = new HashSet<string>(client.Capabilities, StringComparer.Ordinal);
        var compatible = candidates.Where(candidate =>
            HasProtocolOverlap(candidate.Protocols, client.Protocols) &&
            required.All(capability => candidate.Capabilities.Contains(capability, StringComparer.Ordinal)))
            .OrderByDescending(candidate => candidate.ProductVersion);
        var selected = compatible.FirstOrDefault();
        if (selected is null)
            throw new InvalidOperationException(
                "No installed worker overlaps the client's protocol and capability requirements.");
        return selected;
    }

    private static void ValidateCandidate(InstalledWorker candidate)
    {
        if (candidate is null)
            throw new ArgumentException("The installed worker catalog contains a null registration.", nameof(candidate));
        if (candidate.ProductVersion is null || candidate.ProductVersion.Major < 0 ||
            candidate.ProductVersion.Minor < 0 || candidate.ProductVersion.Build < -1 ||
            candidate.ProductVersion.Revision < -1)
            throw new InvalidOperationException("The installed worker catalog contains an invalid product version.");
        if (candidate.Protocols is null)
            throw new InvalidOperationException("The installed worker catalog contains an invalid protocol range.");
        if (string.IsNullOrWhiteSpace(candidate.ExecutablePath) ||
            !System.IO.Path.IsPathRooted(candidate.ExecutablePath))
            throw new InvalidOperationException("The installed worker catalog contains a non-absolute executable path.");
        if (candidate.Capabilities is null || candidate.Capabilities.Count > 128)
            throw new InvalidOperationException("The installed worker catalog contains unbounded capabilities.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in candidate.Capabilities)
        {
            if (string.IsNullOrWhiteSpace(capability) || capability.Length > 128 ||
                capability.Any(char.IsControl) || !seen.Add(capability))
                throw new InvalidOperationException("The installed worker catalog contains malformed capabilities.");
        }
    }

    private static bool HasProtocolOverlap(ProtocolRange left, ProtocolRange right) =>
        Math.Max(left.Minimum, right.Minimum) <= Math.Min(left.Maximum, right.Maximum);
}

/// <summary>Validates that compiled, installed, and connected worker metadata agree.</summary>
public static class WorkerMetadataAgreement
{
    /// <summary>Requires an installed registration to agree with compiled build metadata.</summary>
    public static void RequireMatch(WorkerBuildMetadata compiled, InstalledWorker manifest) =>
        WorkerSelector.RequireMatch(compiled, manifest);

    /// <summary>Requires a connected handshake offer to agree with its installed registration.</summary>
    public static void RequireMatch(WorkerHandshakeOffer offer, InstalledWorker manifest) =>
        WorkerSelector.RequireMatch(offer, manifest);
}
