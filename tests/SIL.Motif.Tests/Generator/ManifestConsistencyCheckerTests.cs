using SIL.Motif.Generator;
using SIL.Motif.Generator.Checks;
using SIL.Motif.Generator.Join;
using SIL.Motif.Generator.Manifest;
using SIL.Motif.Generator.Model;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// ADR 0022 decision 3: the build fails, naming the row, when a manifest row's <c>Verbs</c> or
/// <c>ComparisonClass</c> disagrees with the derivation and is not one of the cited exceptions in
/// <see cref="Derivation.ComparisonClassDerivation.Exceptions"/>. Also ADR 0024 decision 2's totality
/// check, exercised end to end here via <see cref="ManifestConsistencyChecker.CheckGroupIsTotal"/>
/// against real joined rows.
/// </summary>
public class ManifestConsistencyCheckerTests
{
    private static ManifestRow InScopeRow(string cls, string field, string verbs, string comparisonClass) => new(
        Class: cls, Base: "CmObject", Abstract: "false", Scope: "in", ScopeReason: "reason",
        Field: field, Kind: "basic", Sig: "Unicode", Card: "", HcReferenced: "no",
        Construct: "x", Group: "system", Classification: "semantic-operation",
        ComparisonClass: comparisonClass, Verbs: verbs, HcReachable: "no",
        AssessPoisonsCache: "no", EnumValues: "", Rationale: "test fixture");

    private static JoinedRow Row(string cls, string field, FieldKind kind, FieldCard? card, ManifestRow manifest) =>
        new(cls, field, kind, "Unicode", card, manifest);

    [Fact]
    public void CheckVerbsAndComparisonClass_AgreeingRow_DoesNotThrow()
    {
        var manifest = InScopeRow("LexEntry", "Comment", "set|clear", "unordered");
        var rows = new[] { Row("LexEntry", "Comment", FieldKind.Basic, null, manifest) };

        ManifestConsistencyChecker.CheckVerbsAndComparisonClass(rows);
    }

    [Fact]
    public void CheckVerbsAndComparisonClass_DisagreeingVerbs_ThrowsNamingTheRow()
    {
        var manifest = InScopeRow("LexEntry", "Comment", "create|delete", "unordered"); // wrong for a basic field
        var rows = new[] { Row("LexEntry", "Comment", FieldKind.Basic, null, manifest) };

        var ex = Assert.Throws<GeneratorException>(() => ManifestConsistencyChecker.CheckVerbsAndComparisonClass(rows));
        Assert.Contains("LexEntry.Comment", ex.Message);
        Assert.Contains("Verbs mismatch", ex.Message);
    }

    [Fact]
    public void CheckVerbsAndComparisonClass_DisagreeingComparisonClass_ThrowsNamingTheRow()
    {
        var manifest = InScopeRow("LexEntry", "SomeSeqField", "create|delete|move|reparent", "unordered"); // seq should be positional
        var rows = new[] { Row("LexEntry", "SomeSeqField", FieldKind.Owning, FieldCard.Seq, manifest) };

        var ex = Assert.Throws<GeneratorException>(() => ManifestConsistencyChecker.CheckVerbsAndComparisonClass(rows));
        Assert.Contains("LexEntry.SomeSeqField", ex.Message);
        Assert.Contains("ComparisonClass mismatch", ex.Message);
    }

    [Fact]
    public void CheckVerbsAndComparisonClass_TheFiveRealExceptions_DoNotThrow()
    {
        var manifest = InScopeRow("LexEntry", "AlternateForms", "create|delete|move|reparent", "feeding");
        var rows = new[] { Row("LexEntry", "AlternateForms", FieldKind.Owning, FieldCard.Seq, manifest) };

        ManifestConsistencyChecker.CheckVerbsAndComparisonClass(rows);
    }

    [Fact]
    public void CheckVerbsAndComparisonClass_AnInjectedException_StillFails()
    {
        // A field claiming "feeding" that is not one of the real cited exceptions
        // (ComparisonClassDerivation.Exceptions) must still be caught — the table is a closed list
        // of specific rows, not a pattern a new row can opt into.
        var manifest = InScopeRow("LexEntry", "SomeOtherSeqField", "create|delete|move|reparent", "feeding");
        var rows = new[] { Row("LexEntry", "SomeOtherSeqField", FieldKind.Owning, FieldCard.Seq, manifest) };

        var ex = Assert.Throws<GeneratorException>(() => ManifestConsistencyChecker.CheckVerbsAndComparisonClass(rows));
        Assert.Contains("LexEntry.SomeOtherSeqField", ex.Message);
        Assert.Contains("ComparisonClass mismatch", ex.Message);
    }

    [Fact]
    public void CheckVerbsAndComparisonClass_VerbsNa_SkipsVerbCheckButStillChecksComparisonClass()
    {
        // Verbs = n/a is skipped; ComparisonClass is still populated and checked (65 real in-scope
        // rows are exactly this shape — see manifest/liblcm-inventory.tsv).
        var manifest = InScopeRow("LexEntry", "LiftResidue", "n/a", "unordered");
        var rows = new[] { Row("LexEntry", "LiftResidue", FieldKind.Basic, null, manifest) };

        ManifestConsistencyChecker.CheckVerbsAndComparisonClass(rows); // does not throw: ComparisonClass agrees

        var wrongComparisonClass = InScopeRow("LexEntry", "LiftResidue", "n/a", "positional");
        var badRows = new[] { Row("LexEntry", "LiftResidue", FieldKind.Basic, null, wrongComparisonClass) };
        var ex = Assert.Throws<GeneratorException>(() => ManifestConsistencyChecker.CheckVerbsAndComparisonClass(badRows));
        Assert.Contains("ComparisonClass mismatch", ex.Message);
    }

    [Fact]
    public void CheckVerbsAndComparisonClass_OutOfScopeRow_IsSkippedEntirely()
    {
        var manifest = InScopeRow("LexEntry", "Whatever", "create|delete", "unordered") with { Scope = "out" };
        var rows = new[] { Row("LexEntry", "Whatever", FieldKind.Basic, null, manifest) }; // basic + create|delete would fail if checked

        ManifestConsistencyChecker.CheckVerbsAndComparisonClass(rows);
    }

    [Fact]
    public void CheckGroupIsTotal_RealJoinedRows_DoesNotThrow()
    {
        var model = SIL.Motif.Generator.Model.MasterLcModelParser.Parse(SIL.Motif.Generator.ModelSource.ModelPathResolver.Resolve().Path);
        var manifestRows = ManifestTsvParser.Parse(RepoPaths.DefaultManifestPath());
        var joined = ModelManifestJoiner.Join(model.Fields, manifestRows);

        ManifestConsistencyChecker.CheckGroupIsTotal(joined);
    }

    [Fact]
    public void CheckGroupIsTotal_UnknownPrefix_ThrowsNamingTheClass()
    {
        var manifest = InScopeRow("ZzzUnrecognizedSyntheticClass", "Field", "set|clear", "unordered");
        var rows = new[] { Row("ZzzUnrecognizedSyntheticClass", "Field", FieldKind.Basic, null, manifest) };

        var ex = Assert.Throws<GeneratorException>(() => ManifestConsistencyChecker.CheckGroupIsTotal(rows));
        Assert.Contains("ZzzUnrecognizedSyntheticClass", ex.Message);
    }

    [Fact]
    public void CheckVerbsAndComparisonClass_RealInScopeRows_AllAgreeWithDerivation()
    {
        // The plan's own claim: today the manifest and the derivation balance exactly, with zero
        // unexplained departures among the 494 in-scope rows (docs/plan-motif.md MOT-2). This is a
        // regression guard on that claim, not just a synthetic-fixture test.
        var model = SIL.Motif.Generator.Model.MasterLcModelParser.Parse(SIL.Motif.Generator.ModelSource.ModelPathResolver.Resolve().Path);
        var manifestRows = ManifestTsvParser.Parse(RepoPaths.DefaultManifestPath());
        var joined = ModelManifestJoiner.Join(model.Fields, manifestRows);

        ManifestConsistencyChecker.CheckVerbsAndComparisonClass(joined);
    }
}
