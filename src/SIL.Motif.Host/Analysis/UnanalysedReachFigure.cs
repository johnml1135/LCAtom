namespace SIL.Motif.Host.Analysis;

/// <summary>
/// Of the correctly-spelled word forms carrying no manual analysis, how many the grammar parses —
/// ADR 0038 decision 7's one counted figure about the unanalysed. <c>null</c> on
/// <see cref="AnalysisAggregateResponse.UnanalysedReach"/> whenever no Assessment is on record, because
/// nothing about the parser side is knowable at all in that case.
/// </summary>
/// <param name="UnanalysedCount">
/// How many correctly-spelled word forms carry zero manually-approved analyses — the population this
/// figure describes. "Correctly spelled" excludes only word forms whose <c>WfiWordform.SpellingStatus</c>
/// is <c>Incorrect</c>; the default, undecided, is included, because excluding it would silently treat
/// "nobody has judged this yet" as "known to be wrong". A word form nobody has analysed carries no
/// expectation of its own (ADR 0038 decision 7) — this count is the only thing reported about it.
/// </param>
/// <param name="ParsedCount">
/// Of <see cref="UnanalysedCount"/>, how many the grammar produced at least one analysis for, per the
/// Assessment on record. A word form the Assessment did not cover counts as not parsed here, the same as
/// a genuine grammar failure, so this figure never overstates reach.
/// </param>
public sealed record UnanalysedReachFigure(int UnanalysedCount, int ParsedCount)
{
    /// <summary>
    /// The sentence a report prints. <b>The only way to render this figure as prose</b>, so the caveat
    /// ADR 0038 decision 7 requires cannot be dropped by a caller who only wants the numbers in words.
    /// </summary>
    /// <remarks>
    /// Nobody has checked these words, so a rising count is equally consistent with the grammar improving
    /// and with it getting looser — looseness being the exact failure mode this figure exists to catch
    /// early warning of. It supports a claim about how much of the language is reached, and never a claim
    /// that what is reached is correct, and that sentence travels with every number this method prints
    /// rather than living beside it in a document a caller could drop.
    /// </remarks>
    public string Describe() =>
        $"{ParsedCount:N0} of {UnanalysedCount:N0} correctly-spelled word form(s) with no manual analysis " +
        "parsed. This is reach, not correctness: nobody has checked these words, so a rising number is " +
        "equally consistent with the grammar improving and with it getting looser. It supports a claim " +
        "about how much of the language the grammar touches, and never a claim about whether the touch " +
        "was right.";
}
