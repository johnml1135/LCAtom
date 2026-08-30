namespace SIL.Motif.Contract.Responses;

/// <summary>One computed and stored Difference Assessment, as <c>compare</c> prints it.</summary>
public sealed record CompareResponse(
    string AssessmentId,
    string FromAssessmentId,
    string ToAssessmentId,
    string Assessor,
    int FromWordCount,
    int ToWordCount,
    int SharedWordCount,
    bool TokeniserMismatch,
    string? TokeniserWarning,
    string Text);
