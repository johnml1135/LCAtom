using SIL.Motif.Contract.Model;
using SIL.Motif.Contract.Responses;
using SIL.Motif.Projection.Store;
using System.Collections.Generic;
using System.Linq;

namespace SIL.Motif.Projection;

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
            AfterJson: op.After?.GetRawText())).ToList();

        var decision = manifest.Decision is { } d
            ? new DecisionView(d.Outcome, d.ActorType, d.ActorId, d.Comment, d.TimestampUtc)
            : null;

        return new ProposalDetailProjection(
            proposalId, manifest.Status, manifest.Label, manifest.Comment, manifest.CurrentIntentDigest, operations,
            decision, manifest.SupersededBy, envelope.Extensions?.GetRawText());
    }
}
