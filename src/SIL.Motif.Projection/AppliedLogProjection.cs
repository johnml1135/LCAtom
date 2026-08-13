using System;
using System.Collections.Generic;
using System.Linq;
using ModelAppliedLogEntry = SIL.Motif.Model.AppliedLog.AppliedLogEntry;

namespace SIL.Motif.Projection;

/// <summary>One row of the <c>log</c> report — a successfully-parsed applied-change-log entry.</summary>
public sealed record AppliedLogEntryView(
    string ProposalId, string TimestampUtc, string User, string IntentDigest, string Description);

/// <summary>
/// The <c>log</c> report: every Motif entry recorded in one project's applied-change log, in
/// timestamp order, plus any entry that failed to parse.
/// </summary>
public sealed record AppliedLogProjection(
    string ProjectPath,
    int EntryCount,
    IReadOnlyList<AppliedLogEntryView> Entries,
    IReadOnlyList<string> Diagnostics);

/// <summary>Shapes a project's applied-change-log entries into an <see cref="AppliedLogProjection"/>.</summary>
public static class AppliedLogProjectionBuilder
{
    public static AppliedLogProjection Build(
        string projectPath, IReadOnlyList<ModelAppliedLogEntry> entries, IReadOnlyList<string> diagnostics)
    {
        var ordered = entries
            .OrderBy(e => e.TimestampUtc, StringComparer.Ordinal)
            .Select(e => new AppliedLogEntryView(
                e.ProposalId.ToString("D"), e.TimestampUtc, e.User, e.IntentDigest, e.Description))
            .ToList();

        return new AppliedLogProjection(projectPath, ordered.Count, ordered, diagnostics);
    }
}
