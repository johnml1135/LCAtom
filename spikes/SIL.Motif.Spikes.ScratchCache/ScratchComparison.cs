using SIL.LCModel;
using SIL.LCModel.Core.WritingSystems;
using SIL.LCModel.Infrastructure;
using SIL.WritingSystems;

namespace SIL.Motif.Spikes.ScratchCache;

/// <summary>
/// Compares a scratch <see cref="LcmCache"/> against the live cache it was derived from, on the axes that
/// the <c>A1</c> research identified as the real risks. Timing is the easy question; equivalence is the one
/// that could invalidate <c>docs/adr/0016-scratch-cache-copy-not-undo.md</c>'s design rather than its
/// parameters.
/// </summary>
public static class ScratchComparison
{
    /// <param name="sampleSize">
    /// How many senses to compare textually. Sampling is honest here: exhaustive comparison would fluff
    /// every object in both caches, which changes the very cost the spike is measuring.
    /// </param>
    public static ComparisonReport Compare(LcmCache live, LcmCache scratch, string strategy, int sampleSize = 50)
    {
        if (live is null) throw new ArgumentNullException(nameof(live));
        if (scratch is null) throw new ArgumentNullException(nameof(scratch));

        var findings = new List<string>();

        var liveObjects = live.ServiceLocator.GetInstance<ICmObjectRepository>().Count;
        var scratchObjects = scratch.ServiceLocator.GetInstance<ICmObjectRepository>().Count;
        if (liveObjects != scratchObjects)
        {
            // Not automatically a defect: CreateCacheCopy skips cache.Initialize(), so it never runs
            // DataStoreInitializationServices.PrepareCache, which a normal file load does. A file-loaded
            // scratch can therefore legitimately hold objects the in-memory copy does not.
            findings.Add($"object count differs: live {liveObjects:N0}, scratch {scratchObjects:N0} (delta {scratchObjects - liveObjects:+#;-#;0})");
        }

        var liveEntries = live.ServiceLocator.GetInstance<ILexEntryRepository>().Count;
        var scratchEntries = scratch.ServiceLocator.GetInstance<ILexEntryRepository>().Count;
        if (liveEntries != scratchEntries)
            findings.Add($"lexical entry count differs: live {liveEntries:N0}, scratch {scratchEntries:N0}");

        var writingSystems = CompareWritingSystems(live, scratch, findings);
        var customFields = CompareCustomFields(live, scratch, findings);
        var (sampled, senseMismatches) = CompareSenseText(live, scratch, sampleSize, findings);

        return new ComparisonReport(
            Strategy: strategy,
            LiveObjectCount: liveObjects,
            ScratchObjectCount: scratchObjects,
            LiveEntryCount: liveEntries,
            ScratchEntryCount: scratchEntries,
            WritingSystems: writingSystems,
            CustomFields: customFields,
            SensesSampled: sampled,
            SenseTextMismatches: senseMismatches,
            Findings: findings);
    }

    /// <summary>
    /// Hazard (a) from the research: a <c>kMemoryOnly</c> target never attaches a writing-system store, so
    /// <c>GetOrSet</c> always falls through to <c>Create(tag)</c> and synthesizes a definition from the bare
    /// language tag — losing custom collation, valid characters, fonts and keyboards.
    /// </summary>
    private static WritingSystemComparison CompareWritingSystems(LcmCache live, LcmCache scratch, List<string> findings)
    {
        var liveWs = live.ServiceLocator.WritingSystems.AllWritingSystems.ToList();
        var scratchWs = scratch.ServiceLocator.WritingSystems.AllWritingSystems
            .ToDictionary(ws => ws.LanguageTag, StringComparer.Ordinal);

        var missing = new List<string>();
        var degraded = new List<string>();
        var valueEqual = 0;

        foreach (var expected in liveWs)
        {
            if (!scratchWs.TryGetValue(expected.LanguageTag, out var actual))
            {
                missing.Add(expected.LanguageTag);
                continue;
            }

            if (expected.ValueEquals(actual))
            {
                valueEqual++;
                continue;
            }

            degraded.Add($"{expected.LanguageTag}: {DescribeWsDelta(expected, actual)}");
        }

        if (missing.Count > 0)
            findings.Add($"writing systems missing from scratch: {string.Join(", ", missing)}");
        if (degraded.Count > 0)
            findings.Add($"writing systems present but not value-equal ({degraded.Count} of {liveWs.Count}): {string.Join(" | ", degraded.Take(6))}");

        return new WritingSystemComparison(liveWs.Count, scratchWs.Count, valueEqual, missing, degraded);
    }

    private static string DescribeWsDelta(CoreWritingSystemDefinition expected, CoreWritingSystemDefinition actual)
    {
        var parts = new List<string>();

        if (expected.DefaultCollationType != actual.DefaultCollationType)
            parts.Add($"collation type '{expected.DefaultCollationType}' -> '{actual.DefaultCollationType}'");

        var expectedRules = (expected.DefaultCollation as RulesCollationDefinition)?.CollationRules;
        var actualRules = (actual.DefaultCollation as RulesCollationDefinition)?.CollationRules;
        if (!string.Equals(expectedRules, actualRules, StringComparison.Ordinal))
            parts.Add($"collation rules {Len(expectedRules)} -> {Len(actualRules)} chars");

        if (expected.CharacterSets.Count != actual.CharacterSets.Count)
            parts.Add($"character sets {expected.CharacterSets.Count} -> {actual.CharacterSets.Count}");

        var expectedFont = expected.DefaultFont?.Name;
        var actualFont = actual.DefaultFont?.Name;
        if (!string.Equals(expectedFont, actualFont, StringComparison.Ordinal))
            parts.Add($"font '{expectedFont ?? "(none)"}' -> '{actualFont ?? "(none)"}'");

        if (!string.Equals(expected.Keyboard, actual.Keyboard, StringComparison.Ordinal))
            parts.Add("keyboard differs");

        if (!string.Equals(expected.SpellCheckingId, actual.SpellCheckingId, StringComparison.Ordinal))
            parts.Add("spell-check id differs");

        return parts.Count == 0
            ? "not value-equal, but none of the sampled properties differ (see WritingSystemDefinition.ValueEquals)"
            : string.Join("; ", parts);

        static string Len(string? s) => s is null ? "(none)" : s.Length.ToString();
    }

    /// <summary>
    /// Hazard (b): <c>AddCustomFields</c> discards the source's flid and re-derives it while enumerating a
    /// <c>HashSet</c> whose order is not contractual, so the same field can land on a different flid.
    /// </summary>
    private static CustomFieldComparison CompareCustomFields(LcmCache live, LcmCache scratch, List<string> findings)
    {
        var liveFields = CustomFieldMap(live);
        var scratchFields = CustomFieldMap(scratch);

        var missing = liveFields.Keys.Where(k => !scratchFields.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var drifted = liveFields
            .Where(kv => scratchFields.TryGetValue(kv.Key, out var flid) && flid != kv.Value)
            .Select(kv => $"{kv.Key}: flid {kv.Value} -> {scratchFields[kv.Key]}")
            .ToList();

        if (missing.Count > 0)
            findings.Add($"custom fields missing from scratch: {string.Join(", ", missing)}");
        if (drifted.Count > 0)
            findings.Add($"CUSTOM FIELD FLID DRIFT ({drifted.Count}): {string.Join(" | ", drifted)}");

        return new CustomFieldComparison(liveFields.Count, scratchFields.Count, missing, drifted);
    }

    private static Dictionary<string, int> CustomFieldMap(LcmCache cache)
    {
        var mdc = (IFwMetaDataCacheManaged)cache.MetaDataCacheAccessor;
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var flid in mdc.GetFieldIds())
        {
            if (!mdc.IsCustom(flid)) continue;
            // Resolve by (ownerClass, name) — the only durable key. AGENTS.md rule 11: a flid is
            // cache-local and never portable identity. This map exists precisely to prove that.
            map[$"{mdc.GetOwnClsName(flid)}.{mdc.GetFieldName(flid)}"] = flid;
        }

        return map;
    }

    /// <summary>Data fidelity: does the scratch actually carry the same text as the live cache?</summary>
    private static (int Sampled, List<string> Mismatches) CompareSenseText(LcmCache live, LcmCache scratch, int sampleSize, List<string> findings)
    {
        var mismatches = new List<string>();
        var liveSenses = live.ServiceLocator.GetInstance<ILexSenseRepository>()
            .AllInstances()
            .OrderBy(s => s.Guid)
            .Take(sampleSize)
            .ToList();

        var scratchRepo = scratch.ServiceLocator.GetInstance<ILexSenseRepository>();
        foreach (var sense in liveSenses)
        {
            if (!scratchRepo.TryGetObject(sense.Guid, out var counterpart))
            {
                mismatches.Add($"sense {sense.Guid:D} absent from scratch");
                continue;
            }

            var expected = sense.Gloss.AnalysisDefaultWritingSystem?.Text;
            var actual = counterpart.Gloss.AnalysisDefaultWritingSystem?.Text;
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                mismatches.Add($"sense {sense.Guid:D} gloss '{expected ?? "(null)"}' -> '{actual ?? "(null)"}'");
        }

        if (mismatches.Count > 0)
            findings.Add($"sense text mismatches ({mismatches.Count} of {liveSenses.Count} sampled): {string.Join(" | ", mismatches.Take(5))}");

        return (liveSenses.Count, mismatches);
    }

    public sealed record ComparisonReport(
        string Strategy,
        int LiveObjectCount,
        int ScratchObjectCount,
        int LiveEntryCount,
        int ScratchEntryCount,
        WritingSystemComparison WritingSystems,
        CustomFieldComparison CustomFields,
        int SensesSampled,
        List<string> SenseTextMismatches,
        List<string> Findings)
    {
        /// <summary>
        /// Safe for operations that read and write text without depending on writing-system behaviour —
        /// today's <c>lexical/sense/setGloss</c> is the whole of that category.
        /// </summary>
        /// <remarks>
        /// Deliberately does <b>not</b> consider degraded writing systems, because a `MultiUnicode` string
        /// round-trips correctly through a scratch whose writing systems lost their collation and
        /// valid-character settings. Object-count deltas are also excluded — see the note where that finding
        /// is raised.
        /// </remarks>
        public bool IsUsableForTextFidelity =>
            WritingSystems.Missing.Count == 0
            && CustomFields.Missing.Count == 0
            && CustomFields.FlidDrift.Count == 0
            && SenseTextMismatches.Count == 0;

        /// <summary>
        /// Safe for operations whose result depends on the project's own writing-system definitions —
        /// collation and sorting, valid-character validation, homograph ordering, anything font- or
        /// keyboard-sensitive. Stricter than <see cref="IsUsableForTextFidelity"/> on purpose: a scratch can
        /// carry every byte of text correctly and still sort it differently.
        /// </summary>
        public bool IsUsableForWritingSystemSensitiveWork =>
            IsUsableForTextFidelity && WritingSystems.Degraded.Count == 0;
    }

    public sealed record WritingSystemComparison(
        int LiveCount, int ScratchCount, int ValueEqualCount, List<string> Missing, List<string> Degraded);

    public sealed record CustomFieldComparison(
        int LiveCount, int ScratchCount, List<string> Missing, List<string> FlidDrift);
}
