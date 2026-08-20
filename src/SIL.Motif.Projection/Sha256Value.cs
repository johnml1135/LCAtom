using System;

namespace SIL.Motif.Projection;

/// <summary>Validates the canonical text form shared by Motif's SHA-256 identities.</summary>
public static class Sha256Value
{
    private const string Prefix = "sha256:";
    private const int HexLength = 64;

    public static bool IsCanonical(string? value)
    {
        if (value is null || value.Length != Prefix.Length + HexLength
            || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = Prefix.Length; index < value.Length; index++)
        {
            var character = value[index];
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                return false;
        }

        return true;
    }

    public static void RequireCanonical(string? value, string parameterName)
    {
        if (!IsCanonical(value))
        {
            throw new ArgumentException(
                "A canonical SHA-256 value is required: 'sha256:' followed by exactly 64 lowercase " +
                "hexadecimal characters.",
                parameterName);
        }
    }
}
