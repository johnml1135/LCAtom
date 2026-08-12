using System;
using System.Collections.Generic;
using SIL.Motif.Contract.Ids;
using Xunit;

namespace SIL.Motif.Tests.Contract;

/// <summary>
/// Canonical id parsing, validation, GUID round-trip (network-order bytes), and minting, per
/// the Change Set contract's "IDs and GUID mapping" (a 22-character unpadded base64url suffix
/// decoding to the 16 canonical bytes, mapped left-to-right onto the textual GUID's bytes — never
/// .NET's mixed-endian <c>Guid.ToByteArray()</c>) and ADR 0004 decision 2 (a <c>changeSetId</c> is a
/// content-independent, uniquely minted 128-bit id, frozen at creation).
/// </summary>
public class CanonicalIdTests
{
    // The fixed byte-vector documented in docs/change-set-contract.md, "IDs and GUID mapping".
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
        // '=' padding is never part of the URL-safe base64 alphabet, so a padded string must fail.
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
        // Longer than 22 chars isn't "wrong length" -- the extra leading chars are an arbitrary prefix.
        Assert.False(CanonicalId.TryParse(text, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Parse_RejectsSuffixThatDecodesToWrongByteCount()
    {
        // 22 unpadded base64url chars always decode to exactly 16 bytes; this instead uses a bad char.
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
        // Leading 6 bytes are a millisecond timestamp: later-minted ids must never sort earlier by raw bytes.
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
