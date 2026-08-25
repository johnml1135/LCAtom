using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Jobs;

namespace SIL.Motif.Worker.Jobs;

/// <summary>
/// The closed wire shape a Dry Run + Assessment request arrives as. Assessment defaults on: a caller
/// that omits <see cref="IncludeAssessment"/> gets one, and only an explicit <c>false</c> opts out.
/// </summary>
public sealed record DryRunAssessmentCommand(
    string ProposalId, string IntentDigest, BaselineToken Baseline, bool? IncludeAssessment);

/// <summary>Translates the closed <see cref="DryRunAssessmentCommand"/> into a durable pipeline run.</summary>
internal sealed class DryRunAssessmentCommandHandler
{
    private readonly DryRunAssessmentPipeline _pipeline;

    internal DryRunAssessmentCommandHandler(DryRunAssessmentPipeline pipeline)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    /// <summary>Creates the durable job for <paramref name="command"/> and drives it to a terminal state.</summary>
    internal Task<JobRecord> HandleAsync(DryRunAssessmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var request = new DryRunAssessmentRequest(
            CanonicalId.Parse(command.ProposalId), command.IntentDigest, command.Baseline,
            command.IncludeAssessment ?? true);
        return _pipeline.ExecuteAsync(request, cancellationToken);
    }
}
