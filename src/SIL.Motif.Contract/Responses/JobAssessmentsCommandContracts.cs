using System.Collections.Generic;

namespace SIL.Motif.Contract.Responses;

/// <summary>One Assessment a job produced, as <c>jobs assessments</c> lists it.</summary>
public sealed record JobAssessmentSummary(string AssessmentId, string Assessor, string Kind, string SavedUtc);

/// <summary>What one job produced, as <c>jobs assessments &lt;jobId&gt;</c> reports it.</summary>
public sealed record JobAssessmentsResponse(string JobId, IReadOnlyList<JobAssessmentSummary> Assessments);
