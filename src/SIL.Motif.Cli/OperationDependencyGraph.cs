using System;
using System.Collections.Generic;
using System.Linq;
using SIL.Motif.Cli.Store;

namespace SIL.Motif.Cli;

/// <summary>
/// One declared intra-Proposal dependency edge: <see cref="DependentOperationId"/> requires
/// <see cref="RequiredOperationId"/> to remain present, either because it explicitly
/// <c>dependsOn</c> it, or because it <c>target</c>s an entity that only exists because
/// <see cref="RequiredOperationId"/> creates it (its <c>entityId</c>). See
/// docs/adr/0021-cli-is-the-full-surface-layer-1-churns.md decision 6.
/// </summary>
public sealed record DeclaredDependencyEdge(
    string DependentOperationId,
    string DependentKind,
    string RequiredOperationId,
    string RequiredKind,
    string Reason);

/// <summary>
/// Computes the declared (structural, digest-visible) dependency graph among a set of
/// <see cref="DraftOperation"/>s, and classifies which removals decision 6 allows to be enumerated
/// at all.
/// </summary>
/// <remarks>
/// This is deliberately limited to what is <b>declared</b> in the operations themselves —
/// <c>dependsOn</c> and entity-creation/<c>target</c> matching — because that is all a Proposal
/// document can state without opening a live project. It is exactly enough for decision 6, points
/// 1–2 ("a removal with no dependents just happens" / "a removal that severs a requires edge or
/// orphans a dependent warns and names every consequence"). It is deliberately NOT enough to
/// enumerate what a cascading <c>delete</c> operation itself would reach in a live LibLCM model —
/// see <see cref="IsCascadingDelete"/> and decision 6, point 4.
/// </remarks>
public static class OperationDependencyGraph
{
    /// <summary>Every declared edge among <paramref name="operations"/>.</summary>
    public static IReadOnlyList<DeclaredDependencyEdge> AllEdges(IReadOnlyList<DraftOperation> operations)
    {
        var byId = new Dictionary<string, DraftOperation>(StringComparer.Ordinal);
        foreach (var op in operations)
            byId[op.OperationId] = op;

        var byEntityId = new Dictionary<string, DraftOperation>(StringComparer.Ordinal);
        foreach (var op in operations)
        {
            if (op.EntityId is { } entityId)
                byEntityId[entityId] = op;
        }

        var edges = new List<DeclaredDependencyEdge>();
        foreach (var op in operations)
        {
            foreach (var dependsOnId in op.DependsOn)
            {
                if (byId.TryGetValue(dependsOnId, out var required))
                {
                    edges.Add(new DeclaredDependencyEdge(
                        op.OperationId, op.Kind, required.OperationId, required.Kind,
                        $"'{op.OperationId}' ({op.Kind}) explicitly depends on " +
                        $"'{required.OperationId}' ({required.Kind})."));
                }
            }

            if (op.Target is { } target &&
                byEntityId.TryGetValue(target, out var creator) &&
                !ReferenceEquals(creator, op))
            {
                edges.Add(new DeclaredDependencyEdge(
                    op.OperationId, op.Kind, creator.OperationId, creator.Kind,
                    $"'{op.OperationId}' ({op.Kind}) targets entity '{target}', created by " +
                    $"'{creator.OperationId}' ({creator.Kind})."));
            }
        }

        return edges;
    }

    /// <summary>
    /// The transitive closure of operations (among <paramref name="operations"/>) that depend,
    /// directly or indirectly, on any operation id in <paramref name="seed"/> — excluding the seed
    /// itself. This is the full consequence set decision 6 requires naming: removing a dependent can
    /// itself orphan a further one, so a first-level-only enumeration would understate the blast
    /// radius.
    /// </summary>
    public static IReadOnlyList<DeclaredDependencyEdge> TransitiveDependents(
        IReadOnlyList<DraftOperation> operations, IReadOnlySet<string> seed)
    {
        var edges = AllEdges(operations);
        var byRequired = edges
            .GroupBy(e => e.RequiredOperationId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var closureIds = new HashSet<string>(seed, StringComparer.Ordinal);
        var result = new List<DeclaredDependencyEdge>();
        var frontier = new Queue<string>(seed);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            if (!byRequired.TryGetValue(current, out var dependentEdges))
                continue;

            foreach (var edge in dependentEdges)
            {
                if (closureIds.Add(edge.DependentOperationId))
                {
                    result.Add(edge);
                    frontier.Enqueue(edge.DependentOperationId);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// True for a cascading-delete operation kind: one whose apply-time lowering calls LibLCM's
    /// owning-cascade <c>ICmObject.Delete()</c> (e.g. <c>lexical/lexEntry/deleteLexemeForm</c>).
    /// LibLCM's cascade reaches every object the deleted one owns, none of which this Proposal names
    /// anywhere — that reach is discovered only by walking the live model, never declared in the
    /// operation's envelope. Removing such an operation from a Proposal therefore has a consequence
    /// set this analysis (or any purely-declared analysis) cannot honestly state, so it must be
    /// refused outright rather than enumerated-then-forced (decision 6, point 4).
    /// </summary>
    /// <remarks>
    /// Identified by convention: every generated kind's leaf segment (the part after the last
    /// <c>/</c>) names its verb, and <c>delete</c> is reserved for owning-cascade removal — as
    /// opposed to <c>clear</c> (clearing a scalar/reference field, no cascade) or <c>removeRef</c>
    /// (de-referencing, which LibLCM leaves an orphan rather than cascading). See
    /// docs/adr/0021-cli-is-the-full-surface-layer-1-churns.md decision 6.
    /// </remarks>
    public static bool IsCascadingDelete(string kind)
    {
        var leaf = kind[(kind.LastIndexOf('/') + 1)..];
        return leaf.StartsWith("delete", StringComparison.Ordinal);
    }
}
