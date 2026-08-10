namespace SIL.Motif.Host.Analysis;

/// <summary>One word form named in a <see cref="ManualAnalysisDiff"/> category.</summary>
public sealed record ManualAnalysisChange(string WordformGuid, string Form);

/// <summary>
/// What changed between two <see cref="AnalysisAggregateResponse"/> readings, computed from the
/// <b>manual</b> analyses only. There is no separate change-tracking type in this codebase — ADR 0038
/// decision 5 makes this diff the whole answer to "what changed": differences in the automatic analyses
/// are grammar coverage moving, differences in counts are text churn, and what remains — differences in
/// the manual analyses — is a test being established, updated, or removed.
/// </summary>
/// <param name="Established">
/// Word forms that had no approved analysis before and have at least one now — a test coming into
/// existence, including a word form that did not exist in the earlier response at all.
/// </param>
/// <param name="Updated">
/// Word forms that had at least one approved analysis both before and after, where the set of
/// <see cref="ApprovedAnalysis.ContentDigest"/> values differs — a test's assertion changing, never a
/// count-only change (a word form whose analysis set is byte-for-byte the same is not reported here even
/// if its occurrence counts moved, because occurrence counts are text churn, not a test change).
/// </param>
/// <param name="Removed">
/// Word forms that had at least one approved analysis before and have none now. <b>Never netted against
/// <see cref="Established"/> or read as an improvement</b> (ADR 0038 decision 5 and hard constraint):
/// deleting the last approved analysis on a word form improves every raw count while reducing what is
/// checked, and this is often the routinely correct thing to do — reported plainly, exactly like
/// <see cref="Established"/> and <see cref="Updated"/>, never flagged or celebrated.
/// </param>
/// <param name="Vanished">
/// Word forms present in the earlier response and absent from the later one — the word form object itself
/// disappeared from the project, distinct from <see cref="Removed"/> (whose word forms still exist with
/// zero approved analyses). ADR 0038 decision 3: <c>CmObject.MergeObject</c> deletes the source object
/// with no forwarding record, so a vanished word form is reported as vanished and Motif does not chase it
/// or guess what it might have become.
/// </param>
public sealed record ManualAnalysisDiff(
    IReadOnlyList<ManualAnalysisChange> Established,
    IReadOnlyList<ManualAnalysisChange> Updated,
    IReadOnlyList<ManualAnalysisChange> Removed,
    IReadOnlyList<ManualAnalysisChange> Vanished);

/// <summary>Computes a <see cref="ManualAnalysisDiff"/> from two <see cref="AnalysisAggregateResponse"/> readings.</summary>
public static class AnalysisAggregateDiff
{
    /// <param name="before">The earlier reading.</param>
    /// <param name="after">The later reading.</param>
    public static ManualAnalysisDiff Compute(AnalysisAggregateResponse before, AnalysisAggregateResponse after)
    {
        if (before is null) throw new ArgumentNullException(nameof(before));
        if (after is null) throw new ArgumentNullException(nameof(after));

        var beforeByGuid = before.WordForms.ToDictionary(w => w.WordformGuid, StringComparer.Ordinal);
        var afterByGuid = after.WordForms.ToDictionary(w => w.WordformGuid, StringComparer.Ordinal);

        var established = new List<ManualAnalysisChange>();
        var updated = new List<ManualAnalysisChange>();
        var removed = new List<ManualAnalysisChange>();
        var vanished = new List<ManualAnalysisChange>();

        foreach (var (guid, beforeWordform) in beforeByGuid)
        {
            if (!afterByGuid.TryGetValue(guid, out var afterWordform))
            {
                // Present before, gone now: the word form itself disappeared (ADR 0038 decision 3).
                // Reported as vanished, not as "removed" — removed means the word form still exists
                // with zero approved analyses, which is a different and traceable fact.
                vanished.Add(new ManualAnalysisChange(guid, beforeWordform.Form));
                continue;
            }

            Classify(guid, beforeWordform, afterWordform, established, updated, removed);
        }

        foreach (var (guid, afterWordform) in afterByGuid)
        {
            if (beforeByGuid.ContainsKey(guid)) continue; // already classified above

            // A word form absent from the earlier response is new to it. Only report it if it carries an
            // expectation now — a new word form with no manual analysis carries no expectation at all
            // (ADR 0038 decision 7), so it is not a "change" in the sense this diff reports.
            if (afterWordform.ManualAnalyses.Count > 0)
                established.Add(new ManualAnalysisChange(guid, afterWordform.Form));
        }

        return new ManualAnalysisDiff(established, updated, removed, vanished);
    }

    private static void Classify(
        string guid,
        WordFormAnalysisAggregate before,
        WordFormAnalysisAggregate after,
        List<ManualAnalysisChange> established,
        List<ManualAnalysisChange> updated,
        List<ManualAnalysisChange> removed)
    {
        var beforeDigests = before.ManualAnalyses.Select(a => a.ContentDigest).ToHashSet(StringComparer.Ordinal);
        var afterDigests = after.ManualAnalyses.Select(a => a.ContentDigest).ToHashSet(StringComparer.Ordinal);

        if (beforeDigests.Count == 0 && afterDigests.Count > 0)
        {
            established.Add(new ManualAnalysisChange(guid, after.Form));
        }
        else if (beforeDigests.Count > 0 && afterDigests.Count == 0)
        {
            // The routinely-correct case ADR 0038 decision 5 names by name: never rendered as an
            // improvement anywhere, even though every raw count (established, passing, whatever else a
            // caller might compute) would look better for it.
            removed.Add(new ManualAnalysisChange(guid, after.Form));
        }
        else if (beforeDigests.Count > 0 && !beforeDigests.SetEquals(afterDigests))
        {
            updated.Add(new ManualAnalysisChange(guid, after.Form));
        }
        // Both empty, or exactly equal sets: nothing to report. A word form with no manual analysis at
        // either end carries no expectation either time (decision 7), and an unchanged set is not a change.
    }
}
