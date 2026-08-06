using SIL.Motif.Generator.Model;

namespace SIL.Motif.Generator.Derivation;

/// <summary>
/// <c>Verbs</c> is a pure function of <c>(Kind, Card)</c> — seven combinations, zero exceptions,
/// across all 412 authorable rows (ADR 0022 decision 1). Nothing here reads the manifest; this *is*
/// the authority the manifest's <c>Verbs</c> column is checked against
/// (<see cref="Checks.ManifestConsistencyChecker"/>), not the other way around.
/// </summary>
public static class VerbDerivation
{
    /// <summary>The pipe-separated verb string for one field's structural shape, in the manifest's
    /// own ordering convention (docs/adr/0022-structure-is-derived-policy-is-five-rows.md).</summary>
    public static string Derive(FieldKind kind, FieldCard? card) => (kind, card) switch
    {
        (FieldKind.Basic, null) => "set|clear",
        (FieldKind.Rel, FieldCard.Atomic) => "set|clear",
        (FieldKind.Owning, FieldCard.Atomic) => "create|delete",
        (FieldKind.Owning, FieldCard.Col) => "create|delete",
        (FieldKind.Owning, FieldCard.Seq) => "create|delete|move|reparent",
        (FieldKind.Rel, FieldCard.Col) => "addRef|removeRef",
        (FieldKind.Rel, FieldCard.Seq) => "addRef|removeRef|move",
        _ => throw new GeneratorException(
            $"No verb mapping for (Kind={kind}, Card={(card is null ? "null" : card.ToString())}) — " +
            "this (Kind, Card) combination is not one of the seven ADR 0022 declares, which should be " +
            "structurally impossible for a field parsed out of MasterLCModel.xml."),
    };

    /// <summary>Splits a derived (or manifest) verb string into its individual verbs, e.g.
    /// <c>"create|delete|move|reparent"</c> into four — the unit <see cref="Derivation.KindNameDerivation"/>
    /// enumerates one kind per.</summary>
    public static IReadOnlyList<string> EnumerateVerbs(string pipeSeparatedVerbs) =>
        pipeSeparatedVerbs.Split('|');
}
