using System.Collections.Generic;
using System.Text;

namespace SIL.Motif.Projection.Rendering;

/// <summary>
/// Turns each read-surface projection into the same text a reviewer reads at a terminal — a pure
/// function of the projection, never of anything the projection itself does not carry (ADR 0021
/// decision 2). <see cref="ProjectionJson.Serialize{T}"/> is the other renderer over the same
/// object, so every figure below also appears in that JSON by construction.
/// </summary>
public static class CommandTextRenderer
{
    public static string Render(ProjectSummaryProjection projection)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Project: {projection.ProjectName}");
        sb.AppendLine($"Lexical entries: {projection.LexicalEntryCount}");
        return sb.ToString();
    }

    public static string Render(ProposalListProjection projection)
    {
        var sb = new StringBuilder();
        if (projection.Proposals.Count == 0)
        {
            sb.AppendLine("No proposals in store.");
            return sb.ToString();
        }

        foreach (var item in projection.Proposals)
            sb.AppendLine($"{item.ProposalId}  {item.Status,-8}  {item.Label}");
        return sb.ToString();
    }

    public static string Render(ProposalDetailProjection projection)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Proposal {projection.ProposalId}");
        sb.AppendLine($"  status:              {projection.Status}");
        sb.AppendLine($"  label:               {projection.Label}");
        sb.AppendLine($"  comment:             {projection.Comment}");
        sb.AppendLine($"  currentIntentDigest: {projection.CurrentIntentDigest}");
        sb.AppendLine($"  operations ({projection.Operations.Count}):");
        foreach (var op in projection.Operations)
        {
            sb.AppendLine($"    {op.OperationId}  ({op.Kind})");
            if (op.Target is not null)
                sb.AppendLine($"      target:    {op.Target}");
            if (op.EntityId is not null)
                sb.AppendLine($"      entityId:  {op.EntityId}");
            if (op.DependsOn.Count > 0)
                sb.AppendLine($"      dependsOn: {string.Join(", ", op.DependsOn)}");
            sb.AppendLine($"      after:     {op.AfterJson}");
        }
        return sb.ToString();
    }

    public static string Render(AppliedLogProjection projection)
    {
        var sb = new StringBuilder();
        var noun = projection.EntryCount == 1 ? "entry" : "entries";
        sb.AppendLine($"Applied-change log for '{projection.ProjectPath}' ({projection.EntryCount} Motif {noun}):");

        foreach (var entry in projection.Entries)
        {
            sb.AppendLine(
                $"  {entry.ProposalId}  ts={entry.TimestampUtc}  user='{entry.User}'  " +
                $"intentDigest={entry.IntentDigest}  description=\"{entry.Description}\"");
        }

        foreach (var diagnostic in projection.Diagnostics)
            sb.AppendLine(diagnostic);

        return sb.ToString();
    }

    public static string Render(DryRunProjection projection)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"DryRun of Proposal {projection.ProposalId}");
        sb.AppendLine($"  intentDigest: {projection.IntentDigest}");
        sb.AppendLine($"  baseline:     {projection.BaselineNote}");
        sb.AppendLine($"  effects ({projection.Effects.Count}):");
        AppendEffects(sb, projection.Effects);
        sb.AppendLine($"  effectDigest: {projection.EffectDigest}");
        sb.AppendLine($"  footprintDigest: {projection.FootprintDigest}");
        sb.AppendLine("  (bound-DryRun anchor recorded on the manifest; 'apply' will require it)");
        return sb.ToString();
    }

    public static string Render(ApplyProjection projection)
    {
        var sb = new StringBuilder();
        if (projection.AlreadyApplied)
        {
            sb.AppendLine($"Proposal {projection.ProposalId} was already applied (idempotent; no mutation performed).");
            sb.AppendLine($"  {projection.ResultNote}");
        }
        else
        {
            sb.AppendLine($"Applied Proposal {projection.ProposalId}.");
            sb.AppendLine($"  {projection.ResultNote}");
            sb.AppendLine($"  effects ({projection.Effects.Count}):");
            AppendEffects(sb, projection.Effects);
            sb.AppendLine($"  effectDigest: {projection.EffectDigest}");
        }

        var logEntry = projection.AppliedLogEntry;
        sb.AppendLine(
            $"  applied-log entry: proposalId={logEntry.ProposalId} timestamp={logEntry.TimestampUtc} " +
            $"user='{logEntry.User}' intentDigest={logEntry.IntentDigest}");
        return sb.ToString();
    }

    private static void AppendEffects(StringBuilder sb, IReadOnlyList<EffectView> effects)
    {
        foreach (var effect in effects)
        {
            sb.AppendLine($"    {effect.CanonicalId}  field={effect.Field}");

            if (effect.Changes.Count == 0)
            {
                sb.AppendLine("      (no observable before/after change)");
                continue;
            }

            foreach (var change in effect.Changes)
                sb.AppendLine($"      [{change.Ws}] \"{change.Before ?? "(absent)"}\" -> \"{change.After ?? "(absent)"}\"");
        }
    }
}
