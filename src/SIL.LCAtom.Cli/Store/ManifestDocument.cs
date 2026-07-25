namespace SIL.LCAtom.Cli.Store;

/// <summary>
/// The on-disk shape of <c>manifests/&lt;changeSetId&gt;.json</c>: the mutable review state for a
/// committed, immutable Change Set object. See docs/stage2-change-management.md, S4.
/// </summary>
public sealed class ManifestDocument
{
    public string ChangeSetId { get; set; } = "";

    /// <summary><c>proposed</c> (written by <c>finalize</c>) or <c>applied</c> (written by <c>apply</c>).</summary>
    public string Status { get; set; } = ManifestStatus.Proposed;

    public string? Label { get; set; }
    public string? Comment { get; set; }

    /// <summary>The full <c>sha256:</c>-prefixed intent digest computed at <c>finalize</c> time.</summary>
    public string IntentDigest { get; set; } = "";
}

public static class ManifestStatus
{
    public const string Proposed = "proposed";
    public const string Applied = "applied";
}
