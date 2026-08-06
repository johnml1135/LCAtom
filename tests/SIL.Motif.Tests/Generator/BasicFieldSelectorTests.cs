using SIL.Motif.Generator;
using SIL.Motif.Generator.Emit;
using SIL.Motif.Generator.Model;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// Proves MOT-4 slice 1's field list is derived, not hardcoded: against the real, unmodified
/// manifest and <c>MasterLCModel.xml</c>, <see cref="BasicFieldSelector.SelectSlice1BasicFields"/>
/// yields exactly the ten fields docs/plan-motif.md's MOT-4 table names — nine from the mechanical
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
        // LexSense has many other Scope=in, Kind=basic, Verbs=set|clear rows (AnthroNote,
        // Definition, ...) that must NOT be swept in just because Gloss is named explicitly — the
        // exception is one row, not a widened class filter.
        var model = MotifModelLoader.Load();

        var selected = BasicFieldSelector.SelectSlice1BasicFields(model.Rows);

        var lexSenseFields = selected.Where(r => r.DeclaringClass == "LexSense").Select(r => r.FieldName).ToList();
        Assert.Equal(new[] { "Gloss" }, lexSenseFields);
    }

    [Fact]
    public void SelectSlice1BasicFields_RealModel_ExcludesLexEntryLexemeForm()
    {
        // owning/atomic, verbs=create|delete — explicitly out of this set|clear-only slice, even
        // though docs/plan-motif.md's MOT-4 section names it as part of the wider lexEntry family.
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
