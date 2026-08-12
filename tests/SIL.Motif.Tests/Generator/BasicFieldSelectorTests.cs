using SIL.Motif.Generator;
using SIL.Motif.Generator.Emit;
using SIL.Motif.Generator.Model;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// Proves slice 1's field list is derived, not hardcoded: against the real, unmodified
/// manifest and <c>MasterLCModel.xml</c>, <see cref="BasicFieldSelector.SelectSlice1BasicFields"/>
/// yields exactly ten fields — nine from the mechanical
/// <c>LexEntry</c>/<c>MoForm</c> class filter, plus the one explicitly named exception,
/// <c>LexSense.Gloss</c>.
/// </summary>
public class BasicFieldSelectorTests
{
    [Fact]
    public void SelectSlice1BasicFields_RealModel_YieldsExactlyTheDocumentedTenFields()
    {
        var model = MotifModelLoader.Load();

        var selected = BasicFieldSelector.SelectSlice1BasicFields(model.Rows);

        var keys = selected.Select(r => (r.DeclaringClass, r.FieldName)).OrderBy(k => k.ToString()).ToList();
        var expected = new[]
        {
            ("LexEntry", "Bibliography"),
            ("LexEntry", "CitationForm"),
            ("LexEntry", "Comment"),
            ("LexEntry", "DoNotUseForParsing"),
            ("LexEntry", "LiteralMeaning"),
            ("LexEntry", "Restrictions"),
            ("LexEntry", "SummaryDefinition"),
            ("LexSense", "Gloss"),
            ("MoForm", "Form"),
            ("MoForm", "IsAbstract"),
        }.OrderBy(k => k.ToString()).ToList();

        Assert.Equal(expected, keys);
    }

    [Fact]
    public void SelectSlice1BasicFields_RealModel_ExcludesOtherLexSenseBasicSetClearFields()
    {
        // Other LexSense Scope=in/basic/set|clear rows (AnthroNote, Definition, ...) must not be swept in too.
        var model = MotifModelLoader.Load();

        var selected = BasicFieldSelector.SelectSlice1BasicFields(model.Rows);

        var lexSenseFields = selected.Where(r => r.DeclaringClass == "LexSense").Select(r => r.FieldName).ToList();
        Assert.Equal(new[] { "Gloss" }, lexSenseFields);
    }

    [Fact]
    public void SelectSlice1BasicFields_RealModel_ExcludesLexEntryLexemeForm()
    {
        // owning/atomic, verbs=create|delete: out of this set|clear-only slice despite being a LexEntry field.
        var model = MotifModelLoader.Load();

        var selected = BasicFieldSelector.SelectSlice1BasicFields(model.Rows);

        Assert.DoesNotContain(selected, r => r.DeclaringClass == "LexEntry" && r.FieldName == "LexemeForm");
    }

    [Fact]
    public void SelectSlice1BasicFields_RealModel_EveryRowIsScopeInBasicSetClear()
    {
        var model = MotifModelLoader.Load();

        var selected = BasicFieldSelector.SelectSlice1BasicFields(model.Rows);

        Assert.NotEmpty(selected);
        Assert.All(selected, row =>
        {
            Assert.Equal("in", row.Manifest.Scope);
            Assert.Equal(FieldKind.Basic, row.Kind);
            Assert.Equal("set|clear", row.Manifest.Verbs);
        });
    }
}
