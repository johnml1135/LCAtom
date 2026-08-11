namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// Fields whose description is <b>derived from a sibling field's cited source</b> by a checked-in
/// substitution, because the model documents the family once and the siblings differ only in what they
/// match.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a rule and not a sentence.</b> The obvious way to close these rows is to copy the
/// sibling's sentence, edit the one clause that differs, and paste the result into the manifest. That
/// produces prose which is correct today and frozen forever: when the upstream sentence is reworded, the
/// copy stays behind, still cited, still reading fluently, and nothing can tell. Storing the
/// <em>substitution</em> instead means the sentence is re-derived from the current upstream text on every
/// refresh — the human contribution is one <c>(find, replace)</c> pair a reviewer can check in a glance,
/// and the rest is generated.
/// </para>
/// <para>
/// <b>The rule fails rather than degrades.</b> <see cref="Apply"/> requires <see cref="Rule.Find"/> to
/// occur <em>exactly once</em> in the sibling's current text. If the upstream sentence is reworded so the
/// clause is gone — or, worse, appears twice — the adaptation cannot be applied and the build stops. That
/// is the check the research pass asked for and could not express as prose: an adapted row cannot outlive
/// the sentence it was adapted from.
/// </para>
/// <para>
/// <b>The licence for adapting at all is in the model.</b> <c>PhSimpleContext</c>, the abstract parent,
/// carries a class-level comment saying outright: <i>"All subclasses define a featureStructure attr, but
/// they do so differently for each class."</i> The model is stating that the subclasses share one concept
/// and differ in one dimension, which is exactly what a substitution encodes. Without a statement like
/// that, a sibling's sentence would be a guess about a different field, not a source for this one.
/// </para>
/// </remarks>
public static class DescriptionAdaptations
{
    /// <summary>The <c>Reviewed</c> value an adapted row carries (manifest/README.md).</summary>
    public const string ReviewedValue = "adapted";

    /// <param name="SourceClass">The sibling whose cited source text is adapted.</param>
    /// <param name="Find">
    /// The clause in the sibling's text that is specific to the sibling. Must appear exactly once.
    /// </param>
    /// <param name="Replace">The same clause, for this field.</param>
    /// <param name="Licence">
    /// The evidence that adapting a sibling's sentence is legitimate here rather than a guess — quoted, with
    /// its citation, so a reviewer can weigh it without opening the model.
    /// </param>
    public sealed record Rule(
        string Class,
        string Field,
        string SourceClass,
        string SourceField,
        string Find,
        string Replace,
        string Licence)
    {
        public string Key => $"{Class}.{Field}";
        public string SourceKey => $"{SourceClass}.{SourceField}";
    }

    private const string PhSimpleContextLicence =
        "the abstract parent PhSimpleContext's own class comment (MasterLCModel.xml line 4258): \"The " +
        "subclasses define simple contexts in terms of natural classes..., phonemes, and boundary " +
        "markers... All subclasses define a featureStructure attr, but they do so differently for each " +
        "class.\" The model states that these three fields are one concept differing in one dimension, " +
        "which is what the substitution encodes";

    public static readonly IReadOnlyList<Rule> Rules =
    [
        new Rule(
            "PhSimpleContextBdry", "FeatureStructure",
            "PhSimpleContextSeg", "FeatureStructure",
            Find: "a particular phoneme (as opposed to a class of phonemes)",
            Replace: "a particular boundary marker (as opposed to a phoneme or a class of phonemes)",
            Licence: PhSimpleContextLicence),

        new Rule(
            "PhSimpleContextNC", "FeatureStructure",
            "PhSimpleContextSeg", "FeatureStructure",
            Find: "a particular phoneme (as opposed to a class of phonemes)",
            Replace: "a natural class of phonemes (as opposed to one particular phoneme)",
            Licence: PhSimpleContextLicence),
    ];

    public static IReadOnlyDictionary<(string Class, string Field), Rule> ByField { get; } =
        Rules.ToDictionary(r => (r.Class, r.Field));

    /// <summary>
    /// Applies <paramref name="rule"/> to the sibling's current source text.
    /// </summary>
    /// <exception cref="GeneratorException">
    /// If the clause is absent, or occurs more than once. Both mean the upstream sentence is no longer the
    /// one this adaptation was written against, and the honest response is to stop and have a human read
    /// the new wording — not to emit whatever the substitution happens to produce.
    /// </exception>
    public static string Apply(Rule rule, string sourceText)
    {
        var first = sourceText.IndexOf(rule.Find, StringComparison.Ordinal);
        if (first < 0)
        {
            throw new GeneratorException(
                $"{rule.Key}: its description is adapted from {rule.SourceKey}, by replacing " +
                $"\"{rule.Find}\" — which no longer appears in that field's source text:{Environment.NewLine}" +
                $"  {sourceText}{Environment.NewLine}" +
                "The upstream sentence has been reworded. Read the new wording and update the rule in " +
                "DescriptionAdaptations, rather than relaxing this check.");
        }

        var second = sourceText.IndexOf(rule.Find, first + rule.Find.Length, StringComparison.Ordinal);
        if (second >= 0)
        {
            throw new GeneratorException(
                $"{rule.Key}: the clause \"{rule.Find}\" now appears more than once in {rule.SourceKey}'s " +
                "source text, so which occurrence to substitute is no longer decided by the rule. Narrow " +
                "the clause until it is unique again.");
        }

        return sourceText.Remove(first, rule.Find.Length).Insert(first, rule.Replace);
    }

    /// <summary>
    /// The citation an adapted row carries: the sibling it came from, the substitution applied, and the
    /// licence for adapting at all. Long, deliberately — this is the row a reviewer should be most
    /// suspicious of, so it carries the most evidence.
    /// </summary>
    public static string Citation(Rule rule, string sourceCitation) =>
        $"{sourceCitation} — the SIBLING field {rule.SourceKey}, adapted by replacing \"{rule.Find}\" " +
        $"with \"{rule.Replace}\". Licensed by {rule.Licence}. The substitution is re-applied to that " +
        "field's current source text on every refresh, and fails if the clause is no longer there.";
}
