namespace SIL.Motif.Host.Parser;

/// <summary>
/// Which of PanGloss's analysis modes to run. **Motif may not use all of them**, and the reason is
/// correctness rather than preference.
/// </summary>
/// <remarks>
/// <para>
/// PanGloss has three modes. <b>HC only</b> interprets the rules directly: slow and correct. <b>FST only</b>
/// is fast and can say whether a word is valid, but it <b>over-generates</b> — it may report that something
/// which is not a word is a word. <b>FST pruned with HC</b> has the FST propose candidates and HermitCrab
/// confirm them, which prunes the over-generation; it is designed to agree with HC only, and measured to:
/// comparing outcome digests over the same corpus reported all cases unchanged between the two modes.
/// </para>
/// <para>
/// <b>Motif asks whether the grammar rules are being applied properly, so an over-generating answer cannot
/// settle its question.</b> Checking the FST engine against itself for regressions is legitimate work inside
/// PanGloss as that engine evolves; it is not a substitute here. So FST-only is deliberately absent from this
/// enum rather than present and discouraged — there is no Motif use for it, and an unused-but-available mode
/// is an invitation.
/// </para>
/// </remarks>
public enum ParserEngine
{
    /// <summary>
    /// The FST proposes and HermitCrab confirms. **Motif's default and normal choice.** Costs a one-off FST
    /// build per grammar version (measured 0.13 s–12 s), then analyses 12–19× faster than HC alone with no
    /// per-word timeouts observed.
    /// </summary>
    FstPrunedByHermitCrab,

    /// <summary>
    /// HermitCrab alone. **The fallback** for a grammar that will not compile into an FST at all (observed
    /// on a real project — the grammar overflowed the FST engine's enumeration budget), and anything else
    /// odd enough that the FST path cannot handle it.
    /// </summary>
    HermitCrabOnly,
}

/// <summary>Maps <see cref="ParserEngine"/> onto the flags PanGloss's CLI actually takes.</summary>
/// <remarks>
/// The two commands spell the same mode differently — <c>batch --engine=foma</c> and
/// <c>assess --pipeline foma-confirm</c> select the identical propose-then-confirm composite, which is not
/// obvious from the names and was mis-read once already. Both spellings live here so nowhere else has to know.
/// </remarks>
public static class ParserEngineFlags
{
    /// <summary>The <c>--engine</c> value for <c>batch</c> and <c>parse</c>.</summary>
    public static string BatchEngine(this ParserEngine engine) => engine switch
    {
        ParserEngine.FstPrunedByHermitCrab => "foma",
        ParserEngine.HermitCrabOnly => "default",
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown parser engine."),
    };

    /// <summary>The <c>--pipeline</c> value for <c>assess</c>.</summary>
    public static string AssessPipeline(this ParserEngine engine) => engine switch
    {
        ParserEngine.FstPrunedByHermitCrab => "foma-confirm",
        ParserEngine.HermitCrabOnly => "hermitcrab",
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown parser engine."),
    };
}

/// <summary>
/// Maps an Assessment scope's engine name — PanGloss's own vocabulary, <c>"fast"</c> and <c>"accurate"</c> —
/// to a <see cref="ParserEngine"/>. Shared by <see cref="SIL.Motif.Host.Assess.PanGlossAssessor"/>, which
/// produces Assessments under this vocabulary, and any later reader of a stored Assessment's scope that
/// needs the same name resolved back, so the two never drift apart.
/// </summary>
public static class PanGlossEngineNames
{
    private static readonly IReadOnlyDictionary<string, ParserEngine> ByName =
        new Dictionary<string, ParserEngine>(StringComparer.OrdinalIgnoreCase)
        {
            ["fast"] = ParserEngine.FstPrunedByHermitCrab,
            ["accurate"] = ParserEngine.HermitCrabOnly,
        };

    /// <summary>Resolves a scope's <c>Engine</c> name; <c>false</c> when it names no engine PanGloss has.</summary>
    public static bool TryParse(string name, out ParserEngine engine) => ByName.TryGetValue(name, out engine);
}
