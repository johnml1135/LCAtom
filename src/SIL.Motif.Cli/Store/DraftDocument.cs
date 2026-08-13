using System.Collections.Generic;
using System.Text.Json;

namespace SIL.Motif.Cli.Store;

/// <summary>
/// The on-disk shape of <c>drafts/&lt;draftName&gt;.json</c>: a mutable local draft the CLI builds
/// incrementally across invocations (<c>new -&gt; add-set-gloss -&gt; label -&gt; comment -&gt;
/// finalize</c>). Never synced; deleted by <c>finalize</c>.
/// </summary>
public sealed class DraftDocument
{
    /// <summary>Minted at <c>new</c> time and frozen thereafter (ADR 0004).</summary>
    public string ProposalId { get; set; } = "";

    public Dictionary<string, string> ContractVersions { get; set; } = new();

    public List<string> Requires { get; set; } = new();

    /// <summary>Draft-only review metadata; moves to the manifest at <c>finalize</c>, not the object.</summary>
    public string? Label { get; set; }

    /// <summary>Draft-only review metadata; moves to the manifest at <c>finalize</c>, not the object.</summary>
    public string? Comment { get; set; }

    public List<DraftOperation> Operations { get; set; } = new();

    /// <summary>
    /// One entry per Layer-1 composer call that contributed operations to this draft — the composer's
    /// name and its exact authored input, folded into the committed Proposal's <c>extensions</c> at
    /// <c>finalize</c> (ADR 0009 decision 1). Non-hashed: it round-trips as provenance for a reviewer,
    /// never as an input the intent digest depends on.
    /// </summary>
    public List<JsonElement> ComposerProvenance { get; set; } = new();
}

/// <summary>One authored operation inside a draft.</summary>
public sealed class DraftOperation
{
    public string OperationId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Target { get; set; }

    /// <summary>
    /// The canonical id of a newly proposed entity, for creating operations (e.g.
    /// <c>createLexemeForm</c>). <c>null</c> for operations that do not mint a new entity.
    /// Round-tripped through <c>reopen</c>/<c>finalize</c> so a later operation that <c>target</c>s
    /// this id keeps resolving to "created earlier in this Proposal" — and so the removal
    /// analysis (<see cref="SIL.Motif.Cli.OperationDependencyGraph"/>) can find it.
    /// </summary>
    public string? EntityId { get; set; }

    /// <summary>
    /// Explicit intra-Proposal ordering dependencies: operation ids (within this same draft) this
    /// operation requires to have already run. See <see cref="SIL.Motif.Contract.Model.OperationDependency"/>
    /// and the removal/split consequence enumeration this feeds.
    /// </summary>
    public List<string> DependsOn { get; set; } = new();

    /// <summary>
    /// The <c>after</c> payload, one <see cref="JsonElement"/> per property so a boolean or numeric
    /// value round-trips as itself rather than as its stringified text — a composer-produced operation
    /// (e.g. <c>setIsAbstract</c>'s <c>{"value":true}</c>) carries a payload this shape, not only the
    /// string-valued ones the hand-authored CLI verbs build.
    /// </summary>
    public Dictionary<string, JsonElement> After { get; set; } = new();
}
