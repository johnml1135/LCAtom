using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Parsing;

namespace SIL.Motif.Runner.Composers;

/// <summary>
/// The authored input to <see cref="AuthorFeatureStructureComposer"/>: give an existing stem's
/// morphosyntactic analysis an (initially empty) feature structure to record morphosyntactic features
/// against — Motif's first grammar construct, alongside the lexical
/// <see cref="AuthorLexemeFormComposer"/>.
/// </summary>
/// <param name="Msa">
/// The existing <c>MoStemMsa</c> to attach a feature structure to. Must not already have one — this
/// construct authors the structure itself, never overwrites an occupied slot.
/// </param>
public sealed record AuthorFeatureStructureIntent(CanonicalId Msa);

/// <summary>
/// Parses the JSON shape an agent authors for <see cref="AuthorFeatureStructureComposer"/>:
/// <c>{ "msa": "..." }</c>. Closed to any other property, exactly like a Layer-0 operation payload.
/// </summary>
public static class AuthorFeatureStructureIntentParser
{
    private const string ConstructName = "AuthorFeatureStructure";
    private static readonly string[] AllowedProperties = { "msa" };

    public static AuthorFeatureStructureIntent Parse(JsonElement authored)
    {
        if (authored.ValueKind != JsonValueKind.Object)
            throw new ContractParseException($"'{ConstructName}': the authored construct must be a JSON object.");

        foreach (var property in authored.EnumerateObject())
        {
            if (property.Name != "msa")
                throw new ContractParseException($"'{ConstructName}': unknown property '{property.Name}'.");
        }

        if (!authored.TryGetProperty("msa", out var msaElement) || msaElement.ValueKind != JsonValueKind.String)
            throw new ContractParseException($"'{ConstructName}': 'msa' is required and must be a string.");

        var text = msaElement.GetString()!;
        if (!CanonicalId.TryParse(text, out var msa, out var error))
            throw new ContractParseException($"'{ConstructName}': 'msa' ('{text}') is not a valid canonical id: {error}");

        return new AuthorFeatureStructureIntent(msa);
    }
}
