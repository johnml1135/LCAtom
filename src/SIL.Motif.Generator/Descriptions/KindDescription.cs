namespace SIL.Motif.Generator.Descriptions;

/// <summary>
/// One row of <c>manifest/kind-descriptions.tsv</c>: the hand-written sentence explaining *when an agent
/// should reach for* the operation on a given field.
/// </summary>
/// <param name="Class">Declaring LibLCM class.</param>
/// <param name="Field">Field name.</param>
/// <param name="Label">
/// The short human name, normally copied from <c>manifest/fieldworks-labels.tsv</c>. Present here only so the
/// "a description must not merely restate its label" bar can be checked without loading the harvest.
/// </param>
/// <param name="Description">The sentence. Never hashed, so it is free to improve forever.</param>
/// <param name="Reviewed">
/// <c>draft</c> until a linguist has signed it off. Tracked rather than enforced: a draft description is
/// enough to emit a kind, because a wrong sentence is a documentation bug and not a data-corruption risk.
/// The column exists so nobody mistakes drafted prose for reviewed prose.
/// </param>
public sealed record KindDescription(
    string Class,
    string Field,
    string Label,
    string Description,
    string Reviewed)
{
    /// <summary>The manifest join key, matching <see cref="Join.FieldKey"/>'s shape.</summary>
    public string Key => $"{Class}.{Field}";
}
