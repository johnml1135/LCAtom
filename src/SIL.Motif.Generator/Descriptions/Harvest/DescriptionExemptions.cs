using SIL.Motif.Generator.Join;

namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// The rows for which <b>no upstream source exists</b>, each with the evidence of absence written down.
/// </summary>
/// <remarks>
/// <para>
/// D8's rule is that a description is copied from a source, never written. That leaves one honest outcome
/// the <c>sourced</c>/<c>hand-corrected</c>/<c>unsourced</c> vocabulary could not express: a field somebody
/// has searched for exhaustively and found nothing for. Leaving such a row <c>unsourced</c> forever is a
/// standing to-do that nobody can close; marking it <c>sourced</c> is a lie. So it gets its own value,
/// <see cref="ReviewedValue"/>, and — this is the part that matters — a citation of the search rather than
/// of a source.
/// </para>
/// <para>
/// <b>Two kinds of exemption, because the two rows differ.</b> <c>CmPossibilityList.Abbreviation</c> is
/// <see cref="Explicit"/>: its evidence is a list of places looked, which is a human's report and cannot be
/// re-derived. <c>FsFeatureSpecification.Feature</c> is <see cref="DerivedRule"/>: its evidence is a
/// structural fact about the model that <see cref="AbstractDeclarationOnly"/> re-checks on every build, so
/// the exemption stops applying by itself the day the fact stops holding, instead of lingering as a stale
/// allowance nobody re-examines.
/// </para>
/// </remarks>
public static class DescriptionExemptions
{
    /// <summary>The <c>Reviewed</c> value an exempt row carries (manifest/README.md).</summary>
    public const string ReviewedValue = "no-source-exists";

    /// <summary>The <c>Source</c> value an exempt row carries: not a file, a search.</summary>
    public const string SourceValue = "none (searched)";

    /// <param name="Rule">
    /// The machine-checkable rule this exemption rests on, or empty when the evidence is a human's search.
    /// </param>
    public sealed record Entry(string Class, string Field, string Rule, string Evidence)
    {
        public string Key => $"{Class}.{Field}";
    }

    /// <summary>The rule name <see cref="AbstractDeclarationOnly"/> verifies.</summary>
    public const string DerivedRule = "abstract-declaration-only";

    public static readonly IReadOnlyList<Entry> Entries =
    [
        new Entry("CmPossibilityList", "Abbreviation", Rule: "",
            "Searched and not found: no <comment> on the field in MasterLCModel.xml; no ContextHelp.xml " +
            "item; no HelpTopicPaths.resx key for any list; and no page under " +
            "User_Interface/Field_Descriptions in the compiled help — all 38 Abbreviation pages there " +
            "describe a list *item's* abbreviation, and this field is the *list's own*. The absence is " +
            "itself informative: FieldWorks appears never to show this field in a dialog, so there was " +
            "never a field description to write. The sentence in this row is therefore unverified prose, " +
            "kept only because an emitted kind must carry some text; what is established here is that no " +
            "source exists to check it against, not that it is right."),

        new Entry("FsFeatureSpecification", "Feature", DerivedRule,
            "Searched and not found: no <comment> in MasterLCModel.xml on the abstract class or on any of " +
            "its six concrete subclasses, no ContextHelp.xml item, no HelpTopicPaths.resx key, and the " +
            "generated C# doc comment is the template's \"Gets or sets the Feature\" fallback. The " +
            "structural reason is checkable and is re-checked on every build: the field is declared once, " +
            "on an abstract class, and no concrete subclass redeclares it — so there is no per-class slice " +
            "for FieldWorks to have documented. If a subclass ever redeclares it, this exemption fails " +
            "rather than quietly continuing to apply. The sentence in this row is therefore unverified " +
            "prose, kept only because an emitted kind must carry some text; what is established here is " +
            "that no source exists to check it against, not that it is right."),
    ];

    public static IReadOnlyDictionary<(string Class, string Field), Entry> ByField { get; } =
        Entries.ToDictionary(e => (e.Class, e.Field));

    /// <summary>
    /// The <see cref="DerivedRule"/> rule: <paramref name="field"/> is declared on an abstract class and no
    /// concrete descendant of that class redeclares it.
    /// </summary>
    /// <returns>An empty string when the rule holds, or the reason it no longer does.</returns>
    public static string AbstractDeclarationOnly(
        IReadOnlyList<JoinedRow> allRows, string declaringClass, string field)
    {
        var classes = new Dictionary<string, (string Base, string Abstract)>(StringComparer.Ordinal);
        foreach (var row in allRows)
            classes[row.Manifest.Class] = (row.Manifest.Base, row.Manifest.Abstract);

        if (!classes.TryGetValue(declaringClass, out var declaring))
            return $"'{declaringClass}' is not in the manifest at all.";

        if (declaring.Abstract != "true")
            return $"'{declaringClass}' is no longer abstract (Abstract='{declaring.Abstract}').";

        var redeclarers = allRows
            .Where(r => r.Manifest.Field == field
                        && r.Manifest.Class != declaringClass
                        && r.Manifest.Abstract == "false"
                        && Inherits(classes, r.Manifest.Class, declaringClass))
            .Select(r => r.Manifest.Class)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        return redeclarers.Count == 0
            ? ""
            : $"concrete subclass(es) now redeclare '{field}': {string.Join(", ", redeclarers)}. The " +
              "exemption rested on there being no per-class declaration to document, so the source search " +
              "has to be redone against those classes.";
    }

    private static bool Inherits(
        IReadOnlyDictionary<string, (string Base, string Abstract)> classes, string candidate, string ancestor)
    {
        var current = candidate;
        var guard = 0;

        while (classes.TryGetValue(current, out var entry) && entry.Base.Length > 0 && guard++ < 64)
        {
            if (entry.Base == ancestor) return true;
            current = entry.Base;
        }

        return false;
    }
}
