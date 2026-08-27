using ModelAppliedLogEntry = SIL.Motif.Model.AppliedLog.AppliedLogEntry;
using SIL.Motif.Contract.Responses;
using System.Collections.Generic;
using System.Linq;
using System;

namespace SIL.Motif.Projection;

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
