using Xunit;

namespace SIL.Motif.Tests.Projection;

/// <summary>
/// A test for the test. <see cref="FigureAudit"/> is the acceptance check for every report surface, and
/// an earlier version of it passed three cases it was supposed to catch — so its own failure modes are
/// pinned here rather than trusted.
/// </summary>
/// <remarks>
/// Each case below is one an earlier version accepted: a numeric figure that was merely a substring of a
/// JSON value, a flag the JSON contradicted, and a value the JSON did not carry at all. The first two
/// are why membership is exact and against a set that includes booleans.
/// </remarks>
public sealed class FigureAuditTests
{
    [Fact]
    public void ANumberThatIsOnlyASubstringOfAJsonValue_IsCaught()
    {
        const string text = "  entries: \"12\"";
        const string json = """{"entryCount":"512"}""";

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => FigureAudit.AssertEveryTextFigureAppearsInJson(text, json));
    }

    [Fact]
    public void AFlagTheJsonContradicts_IsCaught()
    {
        const string text = "  alreadyApplied: \"true\"";
        const string json = """{"alreadyApplied":false}""";

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => FigureAudit.AssertEveryTextFigureAppearsInJson(text, json));
    }

    [Fact]
    public void AValueTheJsonDoesNotCarryAtAll_IsCaught()
    {
        const string text = "  status: \"applied\"";
        const string json = """{"status":"proposed"}""";

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => FigureAudit.AssertEveryTextFigureAppearsInJson(text, json));
    }

    [Fact]
    public void AFlagTheJsonAgreesWith_Passes()
    {
        const string text = "  alreadyApplied: \"false\"";
        const string json = """{"alreadyApplied":false}""";

        FigureAudit.AssertEveryTextFigureAppearsInJson(text, json);
    }

    [Fact]
    public void AnUnquotedLabeledWordTheJsonDoesNotCarry_IsCaught()
    {
        const string text = "  status:              applied";
        const string json = """{"status":"proposed"}""";

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => FigureAudit.AssertEveryTextFigureAppearsInJson(text, json));
    }

    [Fact]
    public void AnUnquotedLabeledWordTheJsonAgreesWith_Passes()
    {
        const string text = "  status:              proposed";
        const string json = """{"status":"proposed"}""";

        FigureAudit.AssertEveryTextFigureAppearsInJson(text, json);
    }

    [Fact]
    public void AnUnquotedMultiWordPhrase_StaysOutsideTheSweep()
    {
        // "wrong value" is not a JSON leaf, but a space-containing value can't be told apart from prose.
        const string text = "  baseline:     wrong value\n  status:       proposed";
        const string json = """{"baseline":"footprint-scoped baseline","status":"proposed"}""";

        FigureAudit.AssertEveryTextFigureAppearsInJson(text, json);
    }

    [Fact]
    public void ADigestWithItsAlgorithmPrefix_IsOneFigure_NotAStrayTokenAfterTheColon()
    {
        var digest = "sha256:" + new string('a', 64);
        var text = "  intentDigest: " + digest;
        var json = $$"""{"intentDigest":"{{digest}}"}""";

        FigureAudit.AssertEveryTextFigureAppearsInJson(text, json);
    }

    [Fact]
    public void ACanonicalIdBeginningWithBase64UrlPunctuation_IsOneCompleteFigure()
    {
        const string id = "-5fkGwyjT7qgUh2AbBR2hQ";
        var text = "  proposalId: " + id;
        var json = $$"""{"proposalId":"{{id}}"}""";

        FigureAudit.AssertEveryTextFigureAppearsInJson(text, json);
    }
}
