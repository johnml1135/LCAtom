using System;
using System.Collections.Generic;
using System.Linq;
using SIL.Motif.Model.AppliedLog;
using SIL.LCModel;

namespace SIL.Motif.Runner.AppliedLog;

/// <summary>
/// Reads and writes Motif's applied-change log, stored as <c>CmResource</c> entries in
/// <c>LangProject.LexDbOA.ResourcesOC</c>: <c>Version</c> (a <c>Guid</c>) holds the stable
/// <c>changeSetId</c>, the field used for identity matching, and <c>Name</c> holds a packed,
/// single-line provenance string of the form
/// <c>Motif|&lt;format&gt;|&lt;timestamp&gt;|&lt;user&gt;|&lt;intentDigest&gt;|&lt;description&gt;</c>.
/// </summary>
/// <remarks>
/// Foreign entries (no <c>Motif</c> prefix) are never read, rewritten, or deleted — they are
/// skipped entirely by <see cref="ReadAll"/>. An <c>Motif</c>-prefixed entry that fails to parse is
/// reported through <paramref name="onUnparseableEntry"/> (a diagnostic) and otherwise left
/// untouched.
/// </remarks>
public static class ProjectAppliedLog
{
    /// <summary>
    /// Enumerates every successfully-parsed <c>Motif</c>-prefixed entry currently in the project.
    /// Reading a <c>CmResource</c>'s properties is a plain getter and legal at any transaction
    /// state (docs/adr/0006, decision 1), so this never requires an open unit of work.
    /// </summary>
    public static IReadOnlyList<AppliedLogEntry> ReadAll(
        LcmCache cache, Action<string, string>? onUnparseableEntry = null)
    {
        if (cache is null) throw new ArgumentNullException(nameof(cache));

        var entries = new List<AppliedLogEntry>();

        foreach (var resource in cache.LangProject.LexDbOA.ResourcesOC)
        {
            if (!AppliedLogFormat.TryParse(resource.Name, resource.Version, out var entry, out var isForeign, out var error))
            {
                if (!isForeign)
                    onUnparseableEntry?.Invoke(resource.Name ?? string.Empty, error ?? "unknown parse error");
                continue;
            }

            entries.Add(entry!);
        }

        return entries;
    }

    /// <summary>
    /// The idempotence check: does the applied-change log already contain an entry for
    /// <paramref name="proposalId"/>? Presence means Motif already applied this proposal to this
    /// project, at some point, by some user — it does not mean the proposal's effects are still
    /// present, only that they were applied. Absence means Motif never applied it.
    /// </summary>
    public static bool TryFindByProposalId(LcmCache cache, Guid proposalId, out AppliedLogEntry? entry)
    {
        entry = ReadAll(cache).FirstOrDefault(e => e.ProposalId == proposalId);
        return entry is not null;
    }

    /// <summary>
    /// Writes exactly one new applied-log entry: creates a <c>CmResource</c> via
    /// <c>ICmResourceFactory</c>, adds it to <c>LexDb.ResourcesOC</c>, and sets its
    /// <c>Version</c>/<c>Name</c>. Must run inside an already-open unit of work (mirrors
    /// <see cref="SIL.Motif.Runner.Operations.SetGlossLowering"/>: the caller owns opening,
    /// committing, and rolling back that unit of work — see docs/adr/0006, decision 5).
    /// </summary>
    public static void WriteEntry(LcmCache cache, AppliedLogEntry entry)
    {
        if (cache is null) throw new ArgumentNullException(nameof(cache));
        if (entry is null) throw new ArgumentNullException(nameof(entry));

        // Format() validates every field and throws before any mutation happens on invalid input.
        var name = AppliedLogFormat.Format(entry);

        var resource = cache.ServiceLocator.GetInstance<ICmResourceFactory>().Create();
        cache.LangProject.LexDbOA.ResourcesOC.Add(resource);
        resource.Version = entry.ProposalId;
        resource.Name = name;
    }
}
