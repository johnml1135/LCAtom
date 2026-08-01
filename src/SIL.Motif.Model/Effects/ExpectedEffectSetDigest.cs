using System.Collections.Generic;
using SIL.Motif.Contract.Canonicalization;

namespace SIL.Motif.Model.Effects;

/// <summary>
/// Computes the effect digest: RFC 8785 canonical JSON bytes over the effect set, then SHA-256,
/// reusing the Contract kernel's canonicalizer and hash rendering exactly as
/// <see cref="IntentDigest"/> does for a Proposal's intent. See docs/change-set-contract.md,
/// "Expected effects", rule 4, and "Canonical JSON and hashes".
/// </summary>
public static class ExpectedEffectSetDigest
{
    public static string Compute(IEnumerable<ExpectedEffect> effects)
    {
        var json = ExpectedEffectSetJsonWriter.WriteJson(effects);
        var bytes = CanonicalJson.CanonicalizeToUtf8(json);
        return IntentDigest.Sha256Of(bytes);
    }
}
