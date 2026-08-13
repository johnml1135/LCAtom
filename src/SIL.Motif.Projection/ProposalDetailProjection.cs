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

/// <summary>The <c>show</c> report: a committed Proposal's review state and its full operation list.</summary>
public sealed record ProposalDetailProjection(
    string ProposalId,
    string Status,
    string? Label,
    string? Comment,
    string CurrentIntentDigest,
    IReadOnlyList<ProposalOperationView> Operations);

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

        return new ProposalDetailProjection(
            proposalId, manifest.Status, manifest.Label, manifest.Comment, manifest.CurrentIntentDigest, operations);
    }
}
