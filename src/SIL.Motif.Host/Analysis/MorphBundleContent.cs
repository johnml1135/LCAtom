using System.Security.Cryptography;
using System.Text;

namespace SIL.Motif.Host.Analysis;

/// <summary>
/// One morph bundle's content identity — the three references FieldWorks' own comparison,
/// <c>ParseAnalysis.MatchesIWfiAnalysis</c> (<c>ParseResult.cs:102-133</c>), treats as constitutive: the
/// allomorph chosen, the morphosyntactic analysis chosen, and the optional irregular-inflection type.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not the bundle's own GUID, and not <c>SenseRA</c> or word-level <c>CategoryRA</c>
/// either.</b> ADR 0027 establishes —
/// against FieldWorks source, not by inference — that the parser never sets Sense or Category
/// (<c>ParseFiler.ProcessAnalysis</c> has no assignment to either), so gating agreement on them would
/// fail every analysis a human enriched with a sense choice, for a reason that has nothing to do with
/// the grammar. Sense and Category are real judgements worth reporting, but they do not belong in the
/// key that decides whether two analyses are "the same test".
/// </para>
/// <para>
/// Each field holds the referenced object's own GUID as text (or <c>null</c> when the bundle has no
/// such reference — a bare guessed root, for instance, has no <c>InflTypeRA</c>). Two bundles with the
/// same three references are the same content regardless of which <c>WfiMorphBundle</c> or
/// <c>WfiAnalysis</c> object carried them — see <see cref="AnalysisContent"/>.
/// </para>
/// <para>
/// <b>The guessed-string leg of <c>MatchesIWfiAnalysis</c> is deliberately absent, and that is not an
/// omission to be tidied up later.</b> ADR 0027 describes the gate as the three references "with the
/// guessed-string handling", so the difference is worth being precise about. FieldWorks' fourth condition is
/// <c>current.GuessedString == null || EquivalentFormString(mb.Form, current.GuessedString)</c>, where
/// <c>current</c> is a <i>parser</i> morph — it exists to compare a fresh parse result against a stored
/// analysis, and there is no <c>GuessedString</c> when comparing two stored analyses to each other, which is
/// the only thing this digest does. The automatic side is identified by PanGloss's own
/// <c>IdentityDigest</c> instead (see <see cref="AutomaticAnalysis"/>), so this type never straddles the two.
/// </para>
/// <para>
/// What that leaves, stated plainly so it is a known consequence rather than a surprise: two stored analyses
/// whose bundles agree on all three references but carry <i>different literal form strings</i> — possible
/// only where <c>MorphRA</c> is null — hash identically here. <b>FieldWorks considers them the same analysis
/// too</b>, so agreeing with it is the correct behaviour under the same reasoning as
/// <c>WritingSystemWordTokeniser</c>: matching the tool that built the data beats being independently finer,
/// because everything measured is measured against what that tool produced.
/// </para>
/// </remarks>
public sealed record MorphBundleContent(string? MorphGuid, string? MsaGuid, string? InflTypeGuid);

/// <summary>
/// Computes a content digest for an ordered list of <see cref="MorphBundleContent"/> — Motif's mirror of
/// <c>ParseAnalysis.MatchesIWfiAnalysis</c>'s shape, used as the identity for a <c>WfiAnalysis</c> in
/// place of its GUID (ADR 0038 decision 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the <c>WfiAnalysis</c> GUID.</b> Verified against FieldWorks source: editing a breakdown to
/// something matching no existing analysis creates a <i>new</i> <c>WfiAnalysis</c> and deletes the old
/// approved one outright, and whether the GUID happens to survive a same-occurrence edit depends on how
/// many other occurrences shared it — an accident of unrelated state, not identity. Content is durable;
/// the GUID is not.
/// </para>
/// <para>
/// <b>Order matters, count matters.</b> <c>WfiAnalysis.MorphBundlesOS</c> is an owning <i>sequence</i> —
/// morpheme order is part of the analysis, not incidental — so bundles are hashed in the order given,
/// never sorted. The bundle count is folded into the digest explicitly (as its own line) rather than left
/// implicit in the list length, so an empty analysis and a one-bundle analysis whose single bundle
/// resolves to no references cannot collide.
/// </para>
/// </remarks>
public static class AnalysisContent
{
    public static string ComputeDigest(IReadOnlyList<MorphBundleContent> bundles)
    {
        if (bundles is null) throw new ArgumentNullException(nameof(bundles));

        var builder = new StringBuilder();
        builder.Append("count:").Append(bundles.Count).Append('\n');
        foreach (var bundle in bundles)
        {
            builder.Append(bundle.MorphGuid ?? "-").Append('|')
                .Append(bundle.MsaGuid ?? "-").Append('|')
                .Append(bundle.InflTypeGuid ?? "-").Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
