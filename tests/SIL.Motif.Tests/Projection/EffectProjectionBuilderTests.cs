using System;
using System.Collections.Generic;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Model.Effects;
using SIL.Motif.Projection;
using Xunit;

namespace SIL.Motif.Tests.Projection;

public sealed class EffectProjectionBuilderTests
{
    [Fact]
    public void Build_KeepsOnlyChangedWritingSystems()
    {
        var id = CanonicalId.FromGuid(Guid.NewGuid());
        var effect = new ExpectedEffect(
            id,
            "lexical/sense/gloss",
            Before: new Dictionary<string, string> { ["en"] = "old", ["fr"] = "same" },
            After: new Dictionary<string, string> { ["en"] = "new", ["fr"] = "same" });

        var view = Assert.Single(EffectProjectionBuilder.Build(new[] { effect }));

        Assert.Equal(id.Value, view.CanonicalId);
        Assert.Equal("lexical/sense/gloss", view.Field);
        var change = Assert.Single(view.Changes);
        Assert.Equal("en", change.Ws);
        Assert.Equal("old", change.Before);
        Assert.Equal("new", change.After);
    }

    [Fact]
    public void Build_RepresentsAnAbsentAlternativeAsNull()
    {
        var id = CanonicalId.FromGuid(Guid.NewGuid());
        var effect = new ExpectedEffect(
            id,
            "lexical/sense/gloss",
            Before: new Dictionary<string, string>(),
            After: new Dictionary<string, string> { ["en"] = "new" });

        var view = Assert.Single(EffectProjectionBuilder.Build(new[] { effect }));
        var change = Assert.Single(view.Changes);
        Assert.Null(change.Before);
        Assert.Equal("new", change.After);
    }

    [Fact]
    public void Build_NoChangedWritingSystemsProducesAnEmptyChangeList()
    {
        var id = CanonicalId.FromGuid(Guid.NewGuid());
        var effect = new ExpectedEffect(
            id,
            "lexical/sense/gloss",
            Before: new Dictionary<string, string> { ["en"] = "same" },
            After: new Dictionary<string, string> { ["en"] = "same" });

        var view = Assert.Single(EffectProjectionBuilder.Build(new[] { effect }));
        Assert.Empty(view.Changes);
    }
}
