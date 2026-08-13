using System.Collections.Generic;
using System.Linq;
using SIL.Motif.Contract.Model;
using SIL.Motif.Projection.Store;

namespace SIL.Motif.Projection;

/// <summary>One operation inside the <c>show</c> report, with every id rendered as its text form.</summary>
public sealed record ProposalOperationView(
    string OperationId,
    string Kind,
    string? Target,
    string? EntityId,
    IReadOnlyList<string> DependsOn,
    string AfterJson);

/// <summary>The most recent human or AI verdict on a Proposal, shaped for the <c>show</c> report.</summary>
public sealed record DecisionView(
    string Outcome, string ActorType, string ActorId, string? Comment, string TimestampUtc);

/// <summary>The <c>show</c> report: a committed Proposal's review state and its full operation list.</summary>
public sealed record ProposalDetailProjection(
    string ProposalId,
    string Status,
    string? Label,
    string? Comment,
    string CurrentIntentDigest,
    IReadOnlyList<ProposalOperationView> Operations,
    DecisionView? Decision = null,
    string? SupersededBy = null,
    string? ExtensionsJson = null);

/// <summary>Shapes a manifest and its current object content into a <see cref="ProposalDetailProjection"/>.</summary>
public static class ProposalDetailProjectionBuilder
{
    public static ProposalDetailProjection Build(string proposalId, ManifestDocument manifest, Proposal envelope)
    {
        var operations = envelope.Operations.Select(op => new ProposalOperationView(
            OperationId: op.OperationId.Value,
            Kind: op.Kind,
            Target: op.Target?.Value,
            EntityId: op.EntityId?.Value,
            DependsOn: op.DependsOn.Select(d => d.OperationId.Value).ToList(),
            AfterJson: op.After?.GetRawText() ?? "{}")).ToList();

        var decision = manifest.Decision is { } d
            ? new DecisionView(d.Outcome, d.ActorType, d.ActorId, d.Comment, d.TimestampUtc)
            : null;

        return new ProposalDetailProjection(
            proposalId, manifest.Status, manifest.Label, manifest.Comment, manifest.CurrentIntentDigest, operations,
            decision, manifest.SupersededBy, envelope.Extensions?.GetRawText());
    }
}
