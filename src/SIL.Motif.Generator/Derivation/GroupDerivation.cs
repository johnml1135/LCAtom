namespace SIL.Motif.Generator.Derivation;

/// <summary>
/// The kind's <em>first</em> segment: a closed table over the declaring class's name, derived —
/// never read off the manifest's own <c>Group</c> column, which ADR 0024 renamed <em>domain</em>
/// and repurposed as hand-authored editorial routing ("who should review a change"). The two
/// deliberately disagree on 53 rows by design (ADR 0024 decision 1) — <b>do not</b> compare this
/// derivation's output against the manifest column; <see cref="Checks.ManifestConsistencyChecker"/>
/// checks <c>Verbs</c> and <c>ComparisonClass</c> against the manifest for exactly this reason, and
/// pointedly does not check this one.
/// </summary>
/// <remarks>
/// <para>
/// ADR 0024 decision 2 requires only that the table be closed, total over the classes actually
/// present, and that an unrecognized prefix fail the build rather than guess. It does not require
/// reproducing the hand-authored <c>domain</c> column — "accuracy against the old column is no
/// longer a criterion" (ADR 0024, "Decision" preamble) — so this table is free to be the simplest
/// thing that is total: <see cref="ClassOverrides"/> resolves the handful of classes a shared prefix
/// would get wrong, checked before <see cref="PrefixTable"/>, which is ordered longest-prefix-first
/// so a more specific entry (e.g. <c>"ConstChart"</c>) is never shadowed by a shorter one that
/// happens to be declared after it.
/// </para>
/// <para>
/// Five groups are used: LibLCM's own module boundaries (<c>grammar</c> for morphology/phonology/
/// feature-structure classes, <c>lexical</c> for the dictionary, <c>lists</c> for the
/// <c>CmPossibility</c> family) plus <c>system</c> for administrative/import/Scripture machinery and
/// <c>analysis</c> for the word-analysis family ADR 0025 brought into scope — mirroring the five
/// values manifest/README.md documents for its own (differently-sourced) <c>Group</c>/domain column,
/// without being derived from it.
/// </para>
/// </remarks>
public static class GroupDerivation
{
    /// <summary>Overrides checked before PrefixTable: a shared prefix would give the wrong answer here.</summary>
    private static readonly IReadOnlyDictionary<string, string> ClassOverrides = new Dictionary<string, string>
    {
        // ADR 0025 word-analysis family: CmAgent/StTxtPara would otherwise fall to Cm*/St* -> system.
        ["CmAgent"] = "analysis",
        ["StTxtPara"] = "analysis",
        ["Segment"] = "analysis",

        // List scaffolding itself (ADR 0023's example: CmPossibility.Name -> lists/...), not ordinary Cm*.
        ["CmPossibility"] = "lists",
        ["CmPossibilityList"] = "lists",

        // No shared prefix with any sibling class, so each gets its own entry rather than a one-member "prefix".
        ["CrossReference"] = "system",
        ["LangProject"] = "system",
        ["Note"] = "system",
        ["VirtualOrdering"] = "system",
        ["PartOfSpeech"] = "grammar",
        ["Reminder"] = "system",
        ["PunctuationForm"] = "system",
    };

    /// <summary>Prefix -> group; table order is cosmetic, Derive always picks the longest match.</summary>
    private static readonly IReadOnlyList<(string Prefix, string Group)> PrefixTable = new[]
    {
        ("ConstituentChart", "analysis"),  // discourse chart parts (ConstituentChartCellPart)
        ("ConstChart", "analysis"),        // discourse chart parts (ConstChartRow, ConstChartTag, ...)
        ("ReversalIndex", "lexical"),      // ReversalIndex, ReversalIndexEntry
        ("Chk", "system"),                 // Scripture-checking tool classes (ChkRef, ChkTerm, ...)
        ("Cm", "system"),                  // Cellar-model administrative/generic classes (minus ClassOverrides above)
        ("Ds", "analysis"),                // discourse data (DsChart, DsConstChart, DsDiscourseData)
        ("Fs", "grammar"),                 // feature structures
        ("Lex", "lexical"),                // the dictionary
        ("Mo", "grammar"),                 // morphology (ADR 0024 notes 15 of these are hand-called
                                            // lexical instead; the derived rule does not reproduce that split)
        ("Ph", "grammar"),                 // phonology
        ("Pub", "system"),                 // publication settings
        ("Rn", "system"),                  // research notebook
        ("Scr", "system"),                 // Scripture/import machinery (also matches "Scripture" itself)
        ("St", "system"),                  // structured text (minus StTxtPara override above)
        ("Text", "analysis"),              // Text, TextTag
        ("User", "system"),                // UserAppFeatAct, UserConfigAcct
        ("Wfi", "analysis"),               // word analysis (WfiAnalysis, WfiWordform, ...)
        ("Word", "analysis"),              // WordFormLookup, WordformLookupList
    };

    public static string Derive(string declaringClass)
    {
        if (ClassOverrides.TryGetValue(declaringClass, out var overrideGroup))
            return overrideGroup;

        // Longest match computed, not table order: a new entry can't be silently shadowed by a shorter one.
        string? bestPrefix = null;
        string? bestGroup = null;
        foreach (var (prefix, group) in PrefixTable)
        {
            if (!declaringClass.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (bestPrefix is not null && prefix.Length <= bestPrefix.Length) continue;
            bestPrefix = prefix;
            bestGroup = group;
        }

        if (bestGroup is not null)
            return bestGroup;

        throw new GeneratorException(
            $"'{declaringClass}' matches no entry in the closed group-prefix table (ADR 0024 decision 2). " +
            "Add it to GroupDerivation before this class can appear in a generated kind.");
    }
}
