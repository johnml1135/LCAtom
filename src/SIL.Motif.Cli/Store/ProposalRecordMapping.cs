using System.Text.Json;
using SIL.Motif.Model.DryRun;
using SIL.Motif.Projection.Store;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Cli.Store;

/// <summary>
/// Builds the CLI's rendering shapes (<see cref="ManifestDocument"/>) from a repository row, so the
/// projection builders and <see cref="Commands"/> stay written against the same shape regardless of
/// where a Proposal's bytes are persisted.
/// </summary>
public static class ProposalRecordMapping
{
    private static readonly JsonSerializerOptions AnchorJsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Projects one repository row — committed or Draft — into its manifest rendering shape.</summary>
    public static ManifestDocument ToManifest(ProposalRecord record) => new()
    {
        ProposalId = record.ProposalId.Value,
        Status = record.Status,
        Label = record.Label,
        Comment = record.Comment,
        CurrentIntentDigest = record.IntentDigest,
        SupersededBy = record.SupersededBy,
        Anchor = record.AnchorJson is null
            ? null
            : JsonSerializer.Deserialize<BoundDryRunAnchor>(record.AnchorJson, AnchorJsonOptions),
        Decision = record.Decision is null
            ? null
            : new Decision
            {
                Outcome = record.Decision.Outcome,
                ActorType = record.Decision.ActorType,
                ActorId = record.Decision.ActorId,
                Comment = record.Decision.Comment,
                BoundIntentDigest = record.Decision.IntentDigest,
                TimestampUtc = record.Decision.TimestampUtc,
            },
    };
}
