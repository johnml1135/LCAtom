using SIL.Motif.Contract.Canonicalization;
using Xunit;

namespace SIL.Motif.Tests.Contract;

/// <summary>
/// Fixed RFC 8785 canonicalization vectors exercising the JCS reference implementation wrapper
/// (<see cref="CanonicalJson"/>): object member names sorted by UTF-16 code-unit order rather than
/// a native <c>sorted()</c> (ADR 0007 decision 3), string escaping, and ES6 number formatting via
/// ECMAScript <c>Number::toString</c>, then hashed as canonicalization bytes with SHA-256 per the
/// Change Set contract's "Canonical JSON and hashes" rule.
/// </summary>
public class CanonicalJsonTests
{
    [Fact]
    public void ObjectMembers_AreSortedByUtf16CodeUnitOrder()
    {
        Assert.Equal("""{"a":2,"b":1}""", CanonicalJson.Canonicalize("""{"b":1,"a":2}"""));
    }

    [Fact]
    public void ObjectMembers_NumericLookingKeys_SortLexicographicallyNotNumerically()
    {
        // RFC 8785 UTF-16 code-unit order: "1" < "10" < "2", not the numeric order 2 < 10.
        Assert.Equal(
            """{"1":"one","10":"ten","2":"two"}""",
            CanonicalJson.Canonicalize("""{"2":"two","1":"one","10":"ten"}"""));
    }

    [Fact]
    public void NestedObjects_AreSortedAtEveryLevel()
    {
        Assert.Equal(
            """{"a":{"x":1,"y":2},"b":1}""",
            CanonicalJson.Canonicalize("""{"b":1,"a":{"y":2,"x":1}}"""));
    }

    [Fact]
    public void Arrays_PreserveElementOrder()
    {
        Assert.Equal("""[3,1,2]""", CanonicalJson.Canonicalize("""[3,1,2]"""));
    }

    [Fact]
    public void NonAsciiCharacters_AreEmittedLiterallyNotEscaped()
    {
        Assert.Equal("""{"a":"€$"}""", CanonicalJson.Canonicalize("""{"a":"€$"}"""));
    }

    [Fact]
    public void IntegralFloatLiteral_LosesTrailingZeroFraction()
    {
        Assert.Equal("""{"x":1}""", CanonicalJson.Canonicalize("""{"x":1.0}"""));
    }

    [Fact]
    public void Whitespace_IsRemovedRegardlessOfAuthoredPrettyPrinting()
    {
        var pretty = """
            {
              "a" :  1 ,
              "b":   2
            }
            """;
        Assert.Equal("""{"a":1,"b":2}""", CanonicalJson.Canonicalize(pretty));
    }

    [Fact]
    public void CanonicalizeToUtf8_MatchesUtf8EncodingOfStringForm()
    {
        var bytes = CanonicalJson.CanonicalizeToUtf8("""{"b":1,"a":2}""");
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("""{"a":2,"b":1}"""), bytes);
    }
}
