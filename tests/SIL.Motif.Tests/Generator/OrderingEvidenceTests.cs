using SIL.Motif.Generator;
using SIL.Motif.Generator.Checks;
using SIL.Motif.Generator.Descriptions.Harvest;
using SIL.Motif.Generator.Ordering;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// The evidence harvest is a review aid, so what matters is that it quotes the right sentences, records why
/// it chose them, and never quietly stops covering the rows it claims to cover.
/// </summary>
public class OrderingEvidenceTests
{
    [Fact]
    public void OnlyTheSentencesThatSpeakToOrder_AreQuoted()
    {
        const string comment =
            "refers to an ordered seq of MoInflAffixSlot, i.e. those slots which correspond (roughly) to " +
            "prefixes. The order is from the innermost affix out. This attribute is not used for clitics.";

        var (statement, terms) = OrderingEvidenceHarvester.SelectOrderingSentences(comment);

        Assert.Contains("The order is from the innermost affix out.", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("not used for clitics", statement, StringComparison.Ordinal);
        Assert.Contains("order", terms);
        Assert.Contains("innermost", terms);
    }

    /// <summary>
    /// "i.e." is everywhere in this file's prose, and splitting on its full stops would quote half a clause
    /// and drop the half that carries the meaning.
    /// </summary>
    [Fact]
    public void AnAbbreviationInsideASentence_DoesNotSplitIt()
    {
        const string comment = "refers to a seq of slots, i.e. the ordered ones, e.g. prefixes.";

        var (statement, _) = OrderingEvidenceHarvester.SelectOrderingSentences(comment);

        Assert.Equal(comment, statement);
    }

    [Fact]
    public void AFieldWhoseCommentSaysNothingAboutOrder_YieldsNoStatement()
    {
        var (statement, terms) = OrderingEvidenceHarvester.SelectOrderingSentences(
            "refers to a collection of MoForm objects owned by this entry.");

        Assert.Equal("", statement);
        Assert.Empty(terms);
    }

    [Fact]
    public void AFieldWithNoCommentAtAll_YieldsNoStatement()
    {
        var (statement, _) = OrderingEvidenceHarvester.SelectOrderingSentences("");
        Assert.Equal("", statement);
    }

    /// <summary>
    /// The vocabulary is a filter over what a human reads, not a verdict. This sentence reads as a denial
    /// that order matters — and the audit of 2026-08-03 established that order <em>does</em> matter here,
    /// because first appearance assigns the alpha variables. Quoting it is right; scoring it is not.
    /// </summary>
    [Fact]
    public void ASentenceThatSeemsToDenyOrdering_IsStillQuotedForAHumanToRead()
    {
        const string comment =
            "Note that although this attr is defined as a collection seq (not an ordered seq), the order is " +
            "assumed to be stable.";

        var (statement, _) = OrderingEvidenceHarvester.SelectOrderingSentences(comment);

        Assert.Equal(comment, statement);
    }

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"motif-ordering-{Guid.NewGuid():N}.tsv");
        var rows = new[]
        {
            new OrderingEvidence(
                "MoInflAffixTemplate", "PrefixSlots", "seq", "positional",
                "The order is from the innermost affix out.", "order innermost",
                OrderingEvidenceHarvester.SourceName, "line 3402, <rel id=\"PrefixSlots\">",
                SourceDigest.OfText("The order is from the innermost affix out.")),
        };

        try
        {
            OrderingEvidenceTsv.Write(path, rows);
            Assert.Equal(rows, OrderingEvidenceTsv.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The shipped file against the real manifest and model: it covers exactly the rows that claim order
    /// carries meaning, and every quote it holds is cited and digested.
    /// </summary>
    [Fact]
    public void TheShippedEvidenceFile_CoversExactlyTheRowsThatClaimOrderMatters()
    {
        var rows = MotifModelLoader.Load().Rows;
        var evidence = OrderingEvidenceTsv.Read(RepoPaths.DefaultOrderingEvidencePath());

        OrderingEvidenceCheck.Check(rows, evidence);

        Assert.Equal(OrderingEvidenceHarvester.RowsNeedingEvidence(rows).Count, evidence.Count);
    }

    /// <summary>
    /// The headline the audit's recommended review is scoped by. Asserted so the split cannot drift
    /// unnoticed: if a model bump adds ordering prose to a row, or a new <c>seq</c> field ships with none,
    /// the number here changes and someone has to look.
    /// </summary>
    [Fact]
    public void ThirtyTwoOfTheSixtyFourClaims_RestOnCardSeqAlone()
    {
        var evidence = OrderingEvidenceTsv.Read(RepoPaths.DefaultOrderingEvidencePath());

        Assert.Equal(64, evidence.Count);
        Assert.Equal(32, evidence.Count(e => e.HasStatement));
        Assert.Equal(32, evidence.Count(e => !e.HasStatement));
    }
}
