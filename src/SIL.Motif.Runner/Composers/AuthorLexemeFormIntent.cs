using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Runner.Operations;

namespace SIL.Motif.Runner.Composers;

/// <summary>
/// The authored input to <see cref="AuthorLexemeFormComposer"/>: one editorial action against an
/// existing lexical entry, matching what FieldWorks' own entry-creation dialog presents as a single
/// step — a lexeme form of a stated morph type, optionally a root-and-pattern (non-concatenative)
/// form rather than one with fixed segmental content, and optionally a gloss on one of the entry's
/// existing senses.
/// </summary>
/// <remarks>
/// This is the composer's own authoring surface (ADR 0009 decision 1), not a Layer-0 operation
/// payload: it is never hashed or dispatched by <see cref="OperationHandlerRegistry"/> itself, only
/// lowered by <see cref="AuthorLexemeFormComposer.Build"/> into the ordinary, closed-schema
/// operations that are. <see cref="Entry"/> and, when supplied, <see cref="Sense"/> must already
/// exist — this construct authors content on an entry, it does not create one, because neither
/// entry creation nor sense creation has a Layer-0 primitive yet (LexEntry has no owning parent in
/// the manifest, and LexEntry.Senses' owning/seq create has not been generated).
/// </remarks>
/// <param name="Entry">The existing entry the lexeme form is authored onto.</param>
/// <param name="MorphType">The morph type of the new lexeme form.</param>
/// <param name="LexemeFormWritingSystem">The writing system tag the lexeme form's text is written in.</param>
/// <param name="LexemeFormText">The lexeme form's written text.</param>
/// <param name="IsAbstract">
/// Whether the new lexeme form is a root-and-pattern (non-concatenative) form rather than one with
/// fixed segmental content. Defaults to <c>false</c>, the ordinary case.
/// </param>
/// <param name="Sense">
/// An existing sense of <see cref="Entry"/> to gloss in the same authored action, or <c>null</c> to
/// author the lexeme form alone. When supplied, <see cref="GlossWritingSystem"/> and
/// <see cref="GlossText"/> must both be supplied too.
/// </param>
/// <param name="GlossWritingSystem">The writing system tag the gloss is written in, required with <see cref="Sense"/>.</param>
/// <param name="GlossText">The gloss text, required with <see cref="Sense"/>.</param>
public sealed record AuthorLexemeFormIntent(
    CanonicalId Entry,
    CanonicalId MorphType,
    string LexemeFormWritingSystem,
    string LexemeFormText,
    bool IsAbstract = false,
    CanonicalId? Sense = null,
    string? GlossWritingSystem = null,
    string? GlossText = null);

/// <summary>
/// Parses the JSON shape an agent actually authors for <see cref="AuthorLexemeFormComposer"/>:
/// <c>{ "entry": "...", "morphType": "...", "ws": "...", "text": "...", "isAbstract": false,
/// "sense": "...", "glossWs": "...", "glossText": "..." }</c>. Closed to any other property, exactly
/// like a Layer-0 operation payload — pinned by
/// `Parse_UnknownProperty_IsRejectedByTheClosedSchema` — because this is the point where an agent's
/// authored text first becomes a typed value, so a construct gets no laxer a gate than the primitives
/// it lowers into.
/// </summary>
public static class AuthorLexemeFormIntentParser
{
    private const string ConstructName = "AuthorLexemeForm";

    private static readonly string[] AllowedProperties =
    {
        "entry", "morphType", "ws", "text", "isAbstract", "sense", "glossWs", "glossText",
    };

    public static AuthorLexemeFormIntent Parse(JsonElement authored)
    {
        ClosedPayloadParsing.RequireObject(authored, ConstructName);
        ClosedPayloadParsing.RejectUnknownProperties(authored, AllowedProperties, ConstructName);

        var entry = ClosedPayloadParsing.GetRequiredCanonicalId(authored, "entry", ConstructName);
        var morphType = ClosedPayloadParsing.GetRequiredCanonicalId(authored, "morphType", ConstructName);
        var ws = ClosedPayloadParsing.GetRequiredString(authored, "ws", ConstructName);
        var text = ClosedPayloadParsing.GetRequiredString(authored, "text", ConstructName);
        var isAbstract = GetOptionalBoolean(authored, "isAbstract");

        var hasSense = authored.TryGetProperty("sense", out _);
        var hasGlossWs = authored.TryGetProperty("glossWs", out _);
        var hasGlossText = authored.TryGetProperty("glossText", out _);

        if (hasSense != hasGlossWs || hasSense != hasGlossText)
        {
            throw new ContractParseException(
                $"'{ConstructName}': 'sense', 'glossWs', and 'glossText' must be authored together or not at all.");
        }

        if (!hasSense)
            return new AuthorLexemeFormIntent(entry, morphType, ws, text, isAbstract);

        var sense = ClosedPayloadParsing.GetRequiredCanonicalId(authored, "sense", ConstructName);
        var glossWs = ClosedPayloadParsing.GetRequiredString(authored, "glossWs", ConstructName);
        var glossText = ClosedPayloadParsing.GetRequiredString(authored, "glossText", ConstructName);

        return new AuthorLexemeFormIntent(entry, morphType, ws, text, isAbstract, sense, glossWs, glossText);
    }

    private static bool GetOptionalBoolean(JsonElement authored, string propertyName)
    {
        if (!authored.TryGetProperty(propertyName, out var element))
            return false;

        if (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False)
        {
            throw new ContractParseException(
                $"'{ConstructName}' 'after.{propertyName}' must be a boolean.");
        }

        return element.GetBoolean();
    }
}
