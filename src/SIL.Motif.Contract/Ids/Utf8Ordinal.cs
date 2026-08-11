using System;
using System.Text;

namespace SIL.Motif.Contract.Ids;

/// <summary>
/// Byte-ordinal comparison of the UTF-8 encoding of a string. Unordered collections in the intent
/// projection sort by this comparator (ADR 0007 decision 2), never by decode-as-GUID, and never by
/// .NET's UTF-16 <see cref="StringComparer.Ordinal"/>, which can disagree with UTF-8 byte order for
/// characters outside the Basic Multilingual Plane.
/// </summary>
public static class Utf8Ordinal
{
    public static int Compare(string a, string b)
    {
        if (a is null)
            throw new ArgumentNullException(nameof(a));
        if (b is null)
            throw new ArgumentNullException(nameof(b));

        var bytesA = Encoding.UTF8.GetBytes(a);
        var bytesB = Encoding.UTF8.GetBytes(b);

        var length = Math.Min(bytesA.Length, bytesB.Length);
        for (var i = 0; i < length; i++)
        {
            // Both operands are in 0..255, so plain int subtraction cannot misbehave on sign.
            var diff = bytesA[i] - bytesB[i];
            if (diff != 0)
                return diff;
        }

        return bytesA.Length - bytesB.Length;
    }
}
