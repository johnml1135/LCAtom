namespace SIL.Motif.Model.Snapshot;

/// <summary>
/// Canonical field-name constants used as keys into <see cref="ObjectSnapshot.MultiUnicodeFields"/>
/// and as the <c>field</c> value of an expected effect. These are exactly the "field" strings shown
/// in docs/change-set-contract.md, "Expected effects" (e.g. <c>"lexical/sense/gloss"</c>), so a
/// snapshot field and an effect's field always agree textually.
/// </summary>
/// <remarks>
/// Stage C populates exactly one: the <see cref="LexSenseGloss"/> field behind the in-scope
/// <c>lexical/sense/setGloss</c> operation. New fields are added here as later stages add
/// operation kinds; the snapshot/effect shapes below do not need to change to accommodate them.
/// </remarks>
public static class SnapshotFields
{
    /// <summary>A <c>LexSense</c>'s <c>Gloss</c> MultiUnicode field.</summary>
    public const string LexSenseGloss = "lexical/sense/gloss";

    /// <summary>
    /// The Canonical Semantic Snapshot / expected-effect projection shape version, recorded on
    /// <see cref="SIL.Motif.Model.DryRun.BoundDryRunAnchor.ProjectionVersion"/>. Bump this
    /// only when the snapshot/effect shape changes in a way that could alter a digest for otherwise
    /// unchanged content.
    /// </summary>
    public const string ProjectionVersion = "1";
}
