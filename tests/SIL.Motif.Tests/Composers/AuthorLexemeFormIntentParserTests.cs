using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Runner.Composers;
using Xunit;

namespace SIL.Motif.Tests.Composers;

/// <summary>
/// Closed-schema rejection and round-trip tests for <see cref="AuthorLexemeFormIntentParser"/> — the
/// point where an agent's authored JSON for the <c>AuthorLexemeForm</c> construct first becomes a
/// typed <see cref="AuthorLexemeFormIntent"/>. No <c>LcmCache</c> involved: parsing never touches a
/// project.
/// </summary>
public sealed class AuthorLexemeFormIntentParserTests
{
    [Fact]
    public void Parse_ValidMinimalJson_ProducesTheIntent()
    {
        var entry = CanonicalId.Mint();
        var morphType = CanonicalId.Mint();
        var element = Parsed(new { entry = entry.Value, morphType = morphType.Value, ws = "fr", text = "chien" });

        var intent = AuthorLexemeFormIntentParser.Parse(element);

        Assert.Equal(entry, intent.Entry);
        Assert.Equal(morphType, intent.MorphType);
        Assert.Equal("fr", intent.LexemeFormWritingSystem);
        Assert.Equal("chien", intent.LexemeFormText);
        Assert.False(intent.IsAbstract);
        Assert.Null(intent.Sense);
        Assert.Null(intent.GlossWritingSystem);
        Assert.Null(intent.GlossText);
    }

    [Fact]
    public void Parse_ValidJsonWithSenseAndAbstractFlag_ProducesTheFullIntent()
    {
        var sense = CanonicalId.Mint();
        var element = Parsed(new
        {
            entry = CanonicalId.Mint().Value,
            morphType = CanonicalId.Mint().Value,
            ws = "fr",
            text = "chien",
            isAbstract = true,
            sense = sense.Value,
            glossWs = "en",
            glossText = "dog",
        });

        var intent = AuthorLexemeFormIntentParser.Parse(element);

        Assert.True(intent.IsAbstract);
        Assert.Equal(sense, intent.Sense);
        Assert.Equal("en", intent.GlossWritingSystem);
        Assert.Equal("dog", intent.GlossText);
    }

    [Fact]
    public void Parse_UnknownProperty_IsRejectedByTheClosedSchema()
    {
        var element = Parsed(new
        {
            entry = CanonicalId.Mint().Value,
            morphType = CanonicalId.Mint().Value,
            ws = "fr",
            text = "chien",
            extra = "not allowed",
        });

        Assert.Throws<ContractParseException>(() => AuthorLexemeFormIntentParser.Parse(element));
    }

    [Fact]
    public void Parse_MissingRequiredProperty_IsRejectedByTheClosedSchema()
    {
        var element = Parsed(new { morphType = CanonicalId.Mint().Value, ws = "fr", text = "chien" });

        Assert.Throws<ContractParseException>(() => AuthorLexemeFormIntentParser.Parse(element));
    }

    [Fact]
    public void Parse_SenseWithoutGlossFields_IsRejected()
    {
        var element = Parsed(new
        {
            entry = CanonicalId.Mint().Value,
            morphType = CanonicalId.Mint().Value,
            ws = "fr",
            text = "chien",
            sense = CanonicalId.Mint().Value,
        });

        Assert.Throws<ContractParseException>(() => AuthorLexemeFormIntentParser.Parse(element));
    }

    [Fact]
    public void Parse_GlossFieldsWithoutSense_IsRejected()
    {
        var element = Parsed(new
        {
            entry = CanonicalId.Mint().Value,
            morphType = CanonicalId.Mint().Value,
            ws = "fr",
            text = "chien",
            glossWs = "en",
            glossText = "dog",
        });

        Assert.Throws<ContractParseException>(() => AuthorLexemeFormIntentParser.Parse(element));
    }

    private static JsonElement Parsed(object value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }
}
