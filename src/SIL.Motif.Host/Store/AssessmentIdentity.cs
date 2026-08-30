using System.Security.Cryptography;
using System.Text;
using SIL.Motif.Host.Analysis;

namespace SIL.Motif.Host.Store;

/// <summary>Derives a stable id for a <see cref="StoredAssessment"/> from what it says, not from a caller's name.</summary>
/// <remarks>
/// Combines the selection it was measured over (<see cref="Corpus.Selection.Sha256"/>), the grammar that
/// produced it, and the parser's own digest of the outcome — the same newline-joined, <c>sha256:</c>-prefixed
/// convention <see cref="Corpus.Selection"/> uses, for the same reason: two extractions of the same
/// content must hash identically. Re-saving the same parser run against the same selection and grammar therefore
/// lands on the same id rather than accumulating a duplicate row.
/// </remarks>
public static class AssessmentIdentity
{
    public static string ComputeId(StoredAssessment assessment)
    {
        var joined = string.Join(
            "\n",
            assessment.Selection.Sha256,
            assessment.Report.GrammarSourceSha256,
            assessment.Report.OutcomeDigest);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return "sha256:" + System.Convert.ToHexString(hash).ToLowerInvariant();
    }
}
