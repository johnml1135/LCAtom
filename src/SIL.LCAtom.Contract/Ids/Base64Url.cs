using System;

namespace SIL.LCAtom.Contract.Ids;

/// <summary>
/// Unpadded, URL-safe base64 (RFC 4648 §5) encode/decode used for the canonical-id suffix
/// convention. See docs/change-set-contract.md, "IDs and GUID mapping".
/// </summary>
public static class Base64Url
{
    /// <summary>
    /// Encodes <paramref name="bytes"/> as unpadded URL-safe base64. The encoder always produces
    /// the canonical (zero low-order-bit) form, because <see cref="Convert.ToBase64String(byte[])"/>
    /// zero-pads before trimming.
    /// </summary>
    public static string Encode(byte[] bytes)
    {
        if (bytes is null)
            throw new ArgumentNullException(nameof(bytes));

        var standard = Convert.ToBase64String(bytes);
        var urlSafe = standard.Replace('+', '-').Replace('/', '_');
        return urlSafe.TrimEnd('=');
    }

    /// <summary>
    /// Decodes an unpadded URL-safe base64 string. Throws <see cref="FormatException"/> if any
    /// character falls outside the URL-safe alphabet, or if the length is not a valid base64
    /// grouping. Does not otherwise second-guess the caller's expected decoded length.
    /// </summary>
    public static byte[] Decode(string suffix)
    {
        if (suffix is null)
            throw new ArgumentNullException(nameof(suffix));

        foreach (var ch in suffix)
        {
            var isAlphabet =
                (ch >= 'A' && ch <= 'Z') ||
                (ch >= 'a' && ch <= 'z') ||
                (ch >= '0' && ch <= '9') ||
                ch == '-' || ch == '_';
            if (!isAlphabet)
                throw new FormatException($"'{ch}' is not part of the URL-safe unpadded base64 alphabet.");
        }

        var standard = suffix.Replace('-', '+').Replace('_', '/');
        var padded = (standard.Length % 4) switch
        {
            0 => standard,
            2 => standard + "==",
            3 => standard + "=",
            _ => throw new FormatException($"'{suffix}' is not a valid base64url length."),
        };

        return Convert.FromBase64String(padded);
    }
}
