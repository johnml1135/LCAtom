using System.Security.Cryptography;
using System.Text;
using SIL.Motif.Contract.Model;

namespace SIL.Motif.Contract.Canonicalization;

/// <summary>
/// Computes the Proposal intent digest: RFC 8785 canonical JSON bytes over the intent
/// projection, then SHA-256, rendered as <c>sha256:</c> + 64 lowercase hex characters. See
/// docs/change-set-contract.md, "Canonical JSON and hashes".
/// </summary>
public static class IntentDigest
{
    /// <summary>The RFC 8785 canonical UTF-8 bytes of the Proposal's intent projection.</summary>
    public static byte[] CanonicalBytes(Proposal proposal)
    {
        var projectionJson = IntentProjectionWriter.WriteProjectionJson(proposal);
        return CanonicalJson.CanonicalizeToUtf8(projectionJson);
    }

    /// <summary>The intent digest, as <c>sha256:</c> followed by 64 lowercase hex characters.</summary>
    public static string Compute(Proposal proposal) => Sha256Of(CanonicalBytes(proposal));

    /// <summary>Hashes already-canonicalized UTF-8 bytes and renders as <c>sha256:&lt;hex&gt;</c>.</summary>
    public static string Sha256Of(byte[] canonicalUtf8Bytes)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(canonicalUtf8Bytes);
        return "sha256:" + ToLowerHex(hash);
    }

    private static string ToLowerHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            builder.Append(b.ToString("x2"));
        return builder.ToString();
    }
}
