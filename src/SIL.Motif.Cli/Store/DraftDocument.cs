using System.Collections.Generic;

namespace SIL.Motif.Cli.Store;

/// <summary>
/// The on-disk shape of <c>drafts/&lt;draftName&gt;.json</c>: a mutable local draft the CLI builds
/// incrementally across invocations (<c>new -&gt; add-set-gloss -&gt; label -&gt; comment -&gt;
/// finalize</c>). Never synced; deleted by <c>finalize</c>. See docs/stage2-change-management.md, S3.
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
}

/// <summary>One authored operation inside a draft. Stage E supports exactly <c>lexical/lexSense/setGloss</c>.</summary>
public sealed class DraftOperation
{
    public string OperationId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Target { get; set; }

    /// <summary>The <c>after</c> payload. For <c>setGloss</c> this is <c>{"ws": "...", "text": "..."}</c>.</summary>
    public Dictionary<string, string> After { get; set; } = new();
}
