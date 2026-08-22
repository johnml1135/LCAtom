using System;
using System.Collections.Generic;
using System.Linq;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Launcher;

/// <summary>Chooses an installed worker using wire compatibility before product ordering.</summary>
public static class WorkerSelector
{
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
