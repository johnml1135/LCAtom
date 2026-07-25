using SIL.LCAtom.Contract.Canonicalization;
using Xunit;

namespace SIL.LCAtom.Tests.Contract;

/// <summary>
/// Fixed RFC 8785 canonicalization vectors exercising the JCS reference implementation wrapper
/// (<see cref="CanonicalJson"/>): object member sorting, escaping, and ES6 number formatting. See
/// docs/change-set-contract.md "Canonical JSON and hashes" and
/// docs/adr/0007-cross-language-digest-determinism.md decision 3.
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
        // "1" < "10" < "2" under RFC 8785's UTF-16 code-unit member-name ordering, unlike a
        // numeric sort where 2 < 10.
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
