using SIL.Motif.Generator;
using SIL.Motif.Generator.Join;
using SIL.Motif.Generator.Manifest;
using SIL.Motif.Generator.Model;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// MOT-2's core: the (Class, Field) join balances today (898 = 898, zero orphans either way,
/// docs/plan-motif.md MOT-2) and fails closed, naming the key, the moment either side gains an
/// unmatched key.
/// </summary>
public class ModelManifestJoinerTests
{
    private static ModelField Field(string cls, string field) =>
        new(cls, field, FieldKind.Basic, "Unicode", null);

    private static ManifestRow Row(string cls, string field) => new(
        Class: cls, Base: "CmObject", Abstract: "false", Scope: "in", ScopeReason: "reason",
        Field: field, Kind: "basic", Sig: "Unicode", Card: "", HcReferenced: "no",
        Construct: "x", Group: "system", Classification: "semantic-operation",
        ComparisonClass: "unordered", Verbs: "set|clear", HcReachable: "no",
        AssessPoisonsCache: "no", EnumValues: "", Rationale: "test fixture");

    [Fact]
    public void Join_RealModelAndManifest_Balances898To898WithNoOrphans()
    {
        var model = SIL.Motif.Generator.Model.MasterLcModelParser.Parse(
            SIL.Motif.Generator.ModelSource.ModelPathResolver.Resolve().Path);
        var manifestRows = ManifestTsvParser.Parse(RepoPaths.DefaultManifestPath());

        var joined = ModelManifestJoiner.Join(model.Fields, manifestRows);

        Assert.Equal(898, joined.Count);
    }

    [Fact]
    public void Join_MatchingKeys_Succeeds()
    {
        var fields = new[] { Field("LexEntry", "Comment") };
        var rows = new[] { Row("LexEntry", "Comment") };

        var joined = ModelManifestJoiner.Join(fields, rows);

        Assert.Single(joined);
        Assert.Equal("LexEntry", joined[0].DeclaringClass);
        Assert.Equal("Comment", joined[0].FieldName);
    }

    [Fact]
    public void Join_ExtraKeyInModelOnly_FailsNamingTheKey()
    {
        var fields = new[] { Field("LexEntry", "Comment"), Field("LexEntry", "OnlyInModel") };
        var rows = new[] { Row("LexEntry", "Comment") };

        var ex = Assert.Throws<GeneratorException>(() => ModelManifestJoiner.Join(fields, rows));

        Assert.Contains("LexEntry.OnlyInModel", ex.Message);
        Assert.Contains("model but not the manifest", ex.Message);
    }

    [Fact]
    public void Join_ExtraKeyInManifestOnly_FailsNamingTheKey()
    {
        var fields = new[] { Field("LexEntry", "Comment") };
        var rows = new[] { Row("LexEntry", "Comment"), Row("LexEntry", "OnlyInManifest") };

        var ex = Assert.Throws<GeneratorException>(() => ModelManifestJoiner.Join(fields, rows));

        Assert.Contains("LexEntry.OnlyInManifest", ex.Message);
        Assert.Contains("manifest but not the model", ex.Message);
    }

    [Fact]
    public void Join_DuplicateKeyInModel_Fails()
    {
        var fields = new[] { Field("LexEntry", "Comment"), Field("LexEntry", "Comment") };
        var rows = new[] { Row("LexEntry", "Comment") };

        var ex = Assert.Throws<GeneratorException>(() => ModelManifestJoiner.Join(fields, rows));
        Assert.Contains("LexEntry.Comment", ex.Message);
    }

    [Fact]
    public void Join_DuplicateKeyInManifest_Fails()
    {
        var fields = new[] { Field("LexEntry", "Comment") };
        var rows = new[] { Row("LexEntry", "Comment"), Row("LexEntry", "Comment") };

        var ex = Assert.Throws<GeneratorException>(() => ModelManifestJoiner.Join(fields, rows));
        Assert.Contains("LexEntry.Comment", ex.Message);
    }
}
