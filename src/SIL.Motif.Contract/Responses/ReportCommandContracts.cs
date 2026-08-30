using System.Collections.Generic;

namespace SIL.Motif.Contract.Responses;

/// <summary>One report kind <c>report --list-kinds</c> names, and what it means.</summary>
public sealed record ReportKindResponse(string Kind, string Description);

/// <summary>The <c>report --list-kinds</c> report: every kind that may be asked for.</summary>
public sealed record ReportKindListResponse(IReadOnlyList<ReportKindResponse> Kinds);

/// <summary>One computed and stored Report, as <c>report</c> prints it.</summary>
public sealed record ReportResponse(string ReportId, string AssessmentId, string Kind, string Text);
