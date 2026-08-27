using System;
using System.Collections.Generic;
using System.Linq;

namespace SIL.Motif.Contract.Responses;

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
