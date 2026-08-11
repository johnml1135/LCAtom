using System;
using System.Globalization;
using System.Security.Cryptography;

namespace SIL.Motif.Contract.Ids;

/// <summary>
/// The <c>&lt;optional prefix&gt;&lt;22-character unpadded base64url suffix&gt;</c> convention used
/// for <c>proposalId</c>, every <c>operationId</c>, and proposed entity ids — the Change Set
/// contract's "IDs and GUID mapping" rule.
/// </summary>
/// <remarks>
/// Only the trailing 22 characters are structurally enforced: URL-safe base64 alphabet, no
/// padding, decoding to exactly 16 bytes. The prefix is preserved exactly with no normalization
/// and carries no structural meaning.
///
/// This type does not reject a non-canonical (nonzero low-order bit) suffix as long as it decodes
/// to 16 bytes; the contract's four structural rules do not require that. <see cref="Mint"/> and
/// <see cref="FromGuid"/> always produce the canonical zero-bit encoding.
/// </remarks>
public readonly struct CanonicalId : IEquatable<CanonicalId>, IComparable<CanonicalId>
{
    public const int SuffixLength = 22;
    public const int ByteLength = 16;

    private readonly string? _suffix;

    private CanonicalId(string prefix, string suffix)
    {
        Prefix = prefix;
        _suffix = suffix;
    }

    /// <summary>Everything before the final 22 characters, preserved exactly. Empty if none.</summary>
    public string Prefix { get; }

    /// <summary>The structurally-enforced trailing 22-character base64url suffix.</summary>
    public string Suffix => _suffix ?? throw new InvalidOperationException(UninitializedMessage);

    /// <summary>The full textual canonical id: <see cref="Prefix"/> + <see cref="Suffix"/>.</summary>
    public string Value => Prefix + Suffix;

    private bool IsInitialized => _suffix is not null;

    private const string UninitializedMessage =
        "This CanonicalId is the uninitialized default value; it does not represent a valid id.";

    public static CanonicalId Parse(string text)
    {
        if (!TryParse(text, out var id, out var error))
            throw new FormatException(error);
        return id;
    }

    public static bool TryParse(string? text, out CanonicalId id) => TryParse(text, out id, out _);

    public static bool TryParse(string? text, out CanonicalId id, out string? error)
    {
        id = default;

        if (text is null || text.Length == 0)
        {
            error = "A canonical id must not be null or empty.";
            return false;
        }

        if (text.Length < SuffixLength)
        {
            error =
                $"'{text}' is only {text.Length} characters long; a canonical id requires at " +
                $"least the {SuffixLength}-character suffix.";
            return false;
        }

        var prefix = text.Substring(0, text.Length - SuffixLength);
        var suffix = text.Substring(text.Length - SuffixLength);

        byte[] bytes;
        try
        {
            bytes = Base64Url.Decode(suffix);
        }
        catch (FormatException ex)
        {
            error = $"'{text}': suffix '{suffix}' is not valid unpadded URL-safe base64: {ex.Message}";
            return false;
        }

        if (bytes.Length != ByteLength)
        {
            error =
                $"'{text}': suffix '{suffix}' decodes to {bytes.Length} bytes; exactly " +
                $"{ByteLength} bytes (128 bits) are required.";
            return false;
        }

        error = null;
        id = new CanonicalId(prefix, suffix);
        return true;
    }

    /// <summary>Returns a defensive copy of the raw 16 identity bytes.</summary>
    public byte[] ToBytes()
    {
        if (!IsInitialized)
            throw new InvalidOperationException(UninitializedMessage);

        return Base64Url.Decode(Suffix);
    }

    /// <summary>
    /// Converts to a <see cref="Guid"/> using network (big-endian, textual-GUID) byte order:
    /// canonical byte <c>i</c> becomes hex-digit-pair <c>i</c> of the GUID's text form.
    /// Deliberately does not use <c>new Guid(byte[])</c>/<c>Guid.ToByteArray()</c>, whose
    /// historical mixed-endian layout would silently produce a different GUID.
    /// </summary>
    public Guid ToGuid()
    {
        var bytes = ToBytes();
        var hex = BytesToHex(bytes);
        return Guid.ParseExact(hex, "N");
    }

    /// <summary>
    /// Builds a canonical id whose 16 identity bytes are <paramref name="guid"/>'s network-order
    /// (textual-GUID) bytes: hex-digit-pair <c>i</c> of the GUID's text form becomes canonical
    /// byte <c>i</c>. The inverse of <see cref="ToGuid"/>.
    /// </summary>
    public static CanonicalId FromGuid(Guid guid, string prefix = "")
    {
        if (prefix is null)
            throw new ArgumentNullException(nameof(prefix));

        var hex = guid.ToString("N", CultureInfo.InvariantCulture);
        var bytes = HexToBytes(hex);
        return new CanonicalId(prefix, Base64Url.Encode(bytes));
    }

    /// <summary>
    /// Mints a unique, content-independent, time-ordered id: a 48-bit millisecond Unix timestamp
    /// (big-endian, most significant byte first) followed by 80 cryptographically random bits
    /// (ADR 0004 decision 2).
    /// </summary>
    public static CanonicalId Mint(string prefix = "")
    {
        if (prefix is null)
            throw new ArgumentNullException(nameof(prefix));

        var bytes = new byte[ByteLength];

        var millis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // 48-bit big-endian timestamp in bytes[0..5].
        bytes[0] = (byte)((millis >> 40) & 0xFF);
        bytes[1] = (byte)((millis >> 32) & 0xFF);
        bytes[2] = (byte)((millis >> 24) & 0xFF);
        bytes[3] = (byte)((millis >> 16) & 0xFF);
        bytes[4] = (byte)((millis >> 8) & 0xFF);
        bytes[5] = (byte)(millis & 0xFF);

        var random = new byte[10];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(random);
        }
        Array.Copy(random, 0, bytes, 6, random.Length);

        return new CanonicalId(prefix, Base64Url.Encode(bytes));
    }

    private static string BytesToHex(byte[] bytes)
    {
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            chars[i * 2] = NibbleToHex(b >> 4);
            chars[i * 2 + 1] = NibbleToHex(b & 0xF);
        }
        return new string(chars);
    }

    private static char NibbleToHex(int nibble) =>
        (char)(nibble < 10 ? '0' + nibble : 'a' + (nibble - 10));

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bytes;
    }

    public bool Equals(CanonicalId other) =>
        string.Equals(Prefix, other.Prefix, StringComparison.Ordinal) &&
        string.Equals(_suffix, other._suffix, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is CanonicalId other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + (Prefix?.GetHashCode() ?? 0);
            hash = hash * 31 + (_suffix?.GetHashCode() ?? 0);
            return hash;
        }
    }

    /// <summary>
    /// Byte-ordinal comparison of the UTF-8 encoding of <see cref="Value"/> (prefix included), per
    /// ADR 0007 decision 2. Never decode-as-GUID.
    /// </summary>
    public int CompareTo(CanonicalId other) => Utf8Ordinal.Compare(Value, other.Value);

    public override string ToString() => IsInitialized ? Value : "(uninitialized CanonicalId)";

    public static bool operator ==(CanonicalId left, CanonicalId right) => left.Equals(right);
    public static bool operator !=(CanonicalId left, CanonicalId right) => !left.Equals(right);
}
