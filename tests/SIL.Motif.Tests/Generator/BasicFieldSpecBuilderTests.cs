using SIL.Motif.Generator;
using SIL.Motif.Generator.Emit;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// Proves the exact 20 kind strings slice 1 emits, derived through the same
/// <c>GroupDerivation</c>/<c>ConstructDerivation</c>/<c>KindNameDerivation</c> the checks elsewhere use —
/// including the one case that would be wrong if group/construct were read off the manifest's
/// <c>domain</c>/<c>Construct</c> columns instead of derived: <c>MoForm</c> is manifest-domain
/// <c>lexical</c> (ADR 0024's 15 <c>Mo*</c> exceptions) but derived-group <c>grammar</c>, so
/// <c>MoForm.Form</c>/<c>MoForm.IsAbstract</c> must land under <c>grammar/moForm/...</c>, not
/// <c>lexical/...</c>.
/// </summary>
public class BasicFieldSpecBuilderTests
{
    [Fact]
    public void BuildAll_RealModel_ProducesExactlyTheTwentyDocumentedKindStrings()
    {
        var model = MotifModelLoader.Load();
        var selected = BasicFieldSelector.SelectSlice1BasicFields(model.Rows);

        var specs = BasicFieldSpecBuilder.BuildAll(selected);

        var kinds = specs.SelectMany(s => new[] { s.SetKind, s.ClearKind }).OrderBy(k => k, StringComparer.Ordinal).ToList();

        var expected = new[]
        {
            "lexical/lexEntry/setBibliography", "lexical/lexEntry/clearBibliography",
            "lexical/lexEntry/setCitationForm", "lexical/lexEntry/clearCitationForm",
            "lexical/lexEntry/setComment", "lexical/lexEntry/clearComment",
            "lexical/lexEntry/setDoNotUseForParsing", "lexical/lexEntry/clearDoNotUseForParsing",
            "lexical/lexEntry/setLiteralMeaning", "lexical/lexEntry/clearLiteralMeaning",
            "lexical/lexEntry/setRestrictions", "lexical/lexEntry/clearRestrictions",
            "lexical/lexEntry/setSummaryDefinition", "lexical/lexEntry/clearSummaryDefinition",
            "lexical/lexSense/setGloss", "lexical/lexSense/clearGloss",
            "grammar/moForm/setForm", "grammar/moForm/clearForm",
            "grammar/moForm/setIsAbstract", "grammar/moForm/clearIsAbstract",
        }.OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(expected, kinds);
    }

    [Fact]
    public void Build_MoForm_UsesTheDerivedGrammarGroup_NotTheManifestsLexicalDomain()
    {
        var model = MotifModelLoader.Load();
        var moFormRow = model.Rows.Single(r => r.DeclaringClass == "MoForm" && r.FieldName == "Form");

        // Manifest.Group holds the manifest's domain column (ADR 0024): this row's value is "lexical".
        Assert.Equal("lexical", moFormRow.Manifest.Group);

        var spec = BasicFieldSpecBuilder.Build(moFormRow);

        Assert.Equal("grammar", spec.Group);
        Assert.StartsWith("grammar/moForm/", spec.SetKind);
    }

    [Fact]
    public void Build_TargetInterfaceAndSnapshotConstant_AreMechanicalFromDeclaringClassAndField()
    {
        var model = MotifModelLoader.Load();
        var row = model.Rows.Single(r => r.DeclaringClass == "LexEntry" && r.FieldName == "CitationForm");

        var spec = BasicFieldSpecBuilder.Build(row);

        Assert.Equal("ILexEntry", spec.TargetInterface);
        Assert.Equal("LexEntryCitationForm", spec.SnapshotFieldConstant);
    }
}
