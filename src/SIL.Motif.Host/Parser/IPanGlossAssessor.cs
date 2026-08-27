namespace SIL.Motif.Host.Parser;

/// <summary>Runs one assessment over an already-exported candidate and returns what the parser reported.</summary>
/// <remarks>
/// <para>
/// This is the whole of Motif's dependency on the parser: hand it a directory, wait, read the result. It
/// names no engine, no grammar, no cache and no mode, because Motif needs none of those to decide what to
/// do with an assessment — which is what makes the parser substitutable at all.
/// </para>
/// <para>
/// The interface exists so callers can be exercised without launching anything.
/// <see cref="PanGlossAssessmentProcess"/> is the implementation that really launches the parser, and a
/// test that means to cover the process boundary itself should use it against a fake executable rather
/// than replace it here.
/// </para>
/// </remarks>
public interface IPanGlossAssessor
{
    /// <summary>Assesses the grammar source found in <paramref name="exportedCandidate"/>.</summary>
    Task<AssessReport> RunAsync(string exportedCandidate, CancellationToken cancellationToken);
}
