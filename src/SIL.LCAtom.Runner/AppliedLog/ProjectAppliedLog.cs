using System;
using System.Collections.Generic;
using System.Linq;
using SIL.LCAtom.Model.AppliedLog;
using SIL.LCModel;

namespace SIL.LCAtom.Runner.AppliedLog;

/// <summary>
/// Reads and writes LCAtom's applied-change log, stored as <c>CmResource</c> entries in
/// <c>LangProject.LexDbOA.ResourcesOC</c>. See docs/applied-log.md, "Where it lives" and "Record
/// format".
/// </summary>
/// <remarks>
/// Foreign entries (no <c>LCAtom</c> prefix) are never read, rewritten, or deleted — they are
/// skipped entirely by <see cref="ReadAll"/>. An <c>LCAtom</c>-prefixed entry that fails to parse is
/// reported through <paramref name="onUnparseableEntry"/> (a diagnostic) and otherwise left
/// untouched, per docs/applied-log.md, "Foreign entries".
/// </remarks>
public static class ProjectAppliedLog
{
    /// <summary>
    /// Enumerates every successfully-parsed <c>LCAtom</c>-prefixed entry currently in the project.
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
    /// <paramref name="changeSetId"/>? See docs/applied-log.md, "What presence and absence mean".
    /// </summary>
    public static bool TryFindByChangeSetId(LcmCache cache, Guid changeSetId, out AppliedLogEntry? entry)
    {
        entry = ReadAll(cache).FirstOrDefault(e => e.ChangeSetId == changeSetId);
        return entry is not null;
    }

    /// <summary>
    /// Writes exactly one new applied-log entry: creates a <c>CmResource</c> via
    /// <c>ICmResourceFactory</c>, adds it to <c>LexDb.ResourcesOC</c>, and sets its
    /// <c>Version</c>/<c>Name</c>. Must run inside an already-open unit of work (mirrors
    /// <see cref="SIL.LCAtom.Runner.Operations.SetGlossLowering"/>: the caller owns opening,
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
        resource.Version = entry.ChangeSetId;
        resource.Name = name;
    }
}
