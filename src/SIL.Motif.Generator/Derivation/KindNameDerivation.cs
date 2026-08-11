namespace SIL.Motif.Generator.Derivation;

/// <summary>
/// Assembles the full kind string, <c>{group}/{construct}/{verb}{FieldName}</c> (ADR 0023 decision 1).
/// One field with N verbs yields N kind names —
/// e.g. <c>LexEntry.AlternateForms</c> (owning/seq) yields four:
/// <c>lexical/lexEntry/createAlternateForms</c>, <c>...deleteAlternateForms</c>,
/// <c>...moveAlternateForms</c>, <c>...reparentAlternateForms</c>.
/// </summary>
public static class KindNameDerivation
{
    public static string DeriveOne(string group, string construct, string verb, string fieldName) =>
        $"{group}/{construct}/{verb}{fieldName}";

    /// <summary>Every kind name a field's full <see cref="VerbDerivation"/> verb set yields.</summary>
    public static IReadOnlyList<string> DeriveAll(string group, string construct, string verbsPipeSeparated, string fieldName) =>
        VerbDerivation.EnumerateVerbs(verbsPipeSeparated)
            .Select(verb => DeriveOne(group, construct, verb, fieldName))
            .ToList();
}
