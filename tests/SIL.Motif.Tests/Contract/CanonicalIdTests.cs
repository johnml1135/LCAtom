using System;
using System.Collections.Generic;
using SIL.Motif.Contract.Ids;
using Xunit;

namespace SIL.Motif.Tests.Contract;

/// <summary>
/// Canonical id parsing, validation, GUID round-trip (network-order bytes), and minting, per
/// docs/change-set-contract.md "IDs and GUID mapping" and
/// docs/adr/0004-prerequisite-graph-stable-ids-bound-apply.md.
/// </summary>
public class CanonicalIdTests
{
    // The fixed byte-vector from docs/change-set-contract.md, "IDs and GUID mapping":
    //   bytes:  00 01 02 03 04 05 06 07 08 09 0a 0b 0c 0d 0e 0f
    //   suffix: AAECAwQFBgcICQoLDA0ODw
    //   GUID:   00010203-0405-0607-0809-0a0b0c0d0e0f
    private static readonly byte[] FixedVectorBytes =
    {
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f,
    };

    private const string FixedVectorSuffix = "AAECAwQFBgcICQoLDA0ODw";
    private const string FixedVectorGuidText = "00010203-0405-0607-0809-0a0b0c0d0e0f";

    [Fact]
    public void FixedVector_SuffixMatchesDocumentedExample()
    {
        var encoded = Base64Url.Encode(FixedVectorBytes);
        Assert.Equal(FixedVectorSuffix, encoded);
        Assert.Equal(22, encoded.Length);
    }

    [Fact]
    public void FixedVector_SuffixParsesAndRoundTripsToGuid()
    {
        var id = CanonicalId.Parse(FixedVectorSuffix);
        var guid = id.ToGuid();

        Assert.Equal(Guid.Parse(FixedVectorGuidText), guid);
        Assert.Equal(FixedVectorGuidText, guid.ToString());
    }

    [Fact]
    public void FixedVector_GuidToCanonicalId_ProducesDocumentedSuffix()
    {
        var guid = Guid.Parse(FixedVectorGuidText);
        var id = CanonicalId.FromGuid(guid);

        Assert.Equal(FixedVectorSuffix, id.Suffix);
        Assert.Equal("", id.Prefix);
    }

    [Fact]
    public void FixedVector_ToBytes_MatchesDocumentedBytes()
    {
        var id = CanonicalId.Parse(FixedVectorSuffix);
        Assert.Equal(FixedVectorBytes, id.ToBytes());
    }

    [Theory]
    [InlineData("")]
    [InlineData("agent_")]
    [InlineData("lex_entry_")]
    public void GuidRoundTrip_WithArbitraryPrefix_PreservesPrefixAndBytes(string prefix)
    {
        var original = Guid.NewGuid();
        var id = CanonicalId.FromGuid(original, prefix);

        Assert.Equal(prefix, id.Prefix);
        Assert.Equal(prefix + id.Suffix, id.Value);

        var roundTripped = id.ToGuid();
        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void GuidRoundTrip_RandomizedManyGuids_AlwaysRoundTrips()
    {
        var random = new Random(20260724);
        for (var i = 0; i < 500; i++)
        {
            var bytes = new byte[16];
            random.NextBytes(bytes);
            var guid = new Guid(bytes); // arbitrary bit pattern; internal .NET layout is irrelevant here

            var id = CanonicalId.FromGuid(guid, "x_");
            Assert.Equal(guid, id.ToGuid());
        }
    }

    [Fact]
    public void Parse_WithPrefix_SeparatesPrefixFromSuffix()
    {
        var id = CanonicalId.Parse("agent_" + FixedVectorSuffix);
        Assert.Equal("agent_", id.Prefix);
        Assert.Equal(FixedVectorSuffix, id.Suffix);
        Assert.Equal("agent_" + FixedVectorSuffix, id.Value);
    }

    [Fact]
    public void Parse_RejectsPaddedSuffix()
    {
        // A syntactically-padded 22-char base64-looking string containing '=' is not URL-safe
        // base64 at all (padding is never part of the alphabet), so it must fail to parse.
        var padded = FixedVectorSuffix.Substring(0, 20) + "==";
        Assert.False(CanonicalId.TryParse(padded, out _, out var error));
        Assert.Contains("base64", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("AAECAwQFBgcICQoLDA0OD")] // 21 chars: shorter than the required 22-character suffix
    [InlineData("short")]
    [InlineData("")]
    public void Parse_RejectsWrongLengthId(string text)
    {
        // Note: a string *longer* than 22 characters is not "wrong length" -- the extra leading
        // characters are simply an arbitrary, informational prefix (see
        // GuidRoundTrip_WithArbitraryPrefix_PreservesPrefixAndBytes and
        // Parse_WithPrefix_SeparatesPrefixFromSuffix). Only "shorter than the 22-character
        // suffix" is structurally invalid.
        Assert.False(CanonicalId.TryParse(text, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Parse_RejectsSuffixThatDecodesToWrongByteCount()
    {
        // 20 base64url characters (no padding) decode to 15 bytes, not 16, even though the
        // *string* is not 22 characters -- covered above. This case is 22 characters that are
        // valid base64url but whose bit-length, once the implicit padding is restored, still
        // decodes to something other than 16 bytes is structurally impossible for a 22-character
        // unpadded base64 string (22 chars is always exactly 16 bytes' worth once '==' is
        // restored); the four-rule contract is therefore fully covered by the length check above.
        // This test instead nails down that a non-base64url character is rejected outright.
        var invalidChar = FixedVectorSuffix.Substring(0, 21) + "+"; // '+' is standard-base64, not URL-safe
        Assert.False(CanonicalId.TryParse(invalidChar, out _, out var error));
        Assert.Contains("URL-safe", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Equals_And_HashCode_AreValueBased()
    {
        var a = CanonicalId.Parse("p_" + FixedVectorSuffix);
        var b = CanonicalId.Parse("p_" + FixedVectorSuffix);
        var c = CanonicalId.Parse("q_" + FixedVectorSuffix);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Mint_ProducesStructurallyValidId()
    {
        var id = CanonicalId.Mint("agent_");
        Assert.Equal("agent_", id.Prefix);
        Assert.Equal(22, id.Suffix.Length);
        Assert.True(CanonicalId.TryParse(id.Value, out _));
    }

    [Fact]
    public void Mint_IsUniqueAcrossManyCalls()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 2000; i++)
        {
            var id = CanonicalId.Mint();
            Assert.True(seen.Add(id.Value), $"Minted a duplicate id: {id.Value}");
        }
    }

    [Fact]
    public void Mint_IsTimeOrdered_ByFirst48Bits()
    {
        // The leading 6 bytes are a millisecond timestamp, so ids minted later must never sort
        // earlier when compared by their raw bytes (random low bits cannot decrease a strictly
        // later millisecond value's ordering across the timestamp portion).
        var first = CanonicalId.Mint();
        System.Threading.Thread.Sleep(5);
        var second = CanonicalId.Mint();

        var firstTimestampBytes = first.ToBytes()[..6];
        var secondTimestampBytes = second.ToBytes()[..6];

        Assert.True(
            CompareBytes(firstTimestampBytes, secondTimestampBytes) <= 0,
            "A later-minted id's timestamp prefix must not sort before an earlier one's.");
    }

    [Fact]
    public void Mint_TimestampPrefix_MatchesUnixMillisecondsAtMintTime()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var id = CanonicalId.Mint();
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var bytes = id.ToBytes();
        long millis = 0;
        for (var i = 0; i < 6; i++)
            millis = (millis << 8) | bytes[i];

        Assert.InRange(millis, before, after);
    }

    private static int CompareBytes(byte[] a, byte[] b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            var diff = a[i] - b[i];
            if (diff != 0)
                return diff;
        }
        return 0;
    }
}
