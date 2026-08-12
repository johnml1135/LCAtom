using SIL.Motif.Contract.Ids;
using SIL.Motif.Model.Snapshot;
using Xunit;

namespace SIL.Motif.Tests.Model;

/// <summary>
/// LibLCM-free unit tests for the single-reference-as-alternatives-map convention used
/// for <c>rel/atomic</c> fields (<c>MoForm.MorphType</c>) and for the owning/atomic
/// <c>create</c>/<c>delete</c> slot itself (<c>LexEntry.LexemeForm</c>) — both are "which one entity,
/// if any, does this slot currently hold," which is exactly <see cref="BooleanFieldAlternatives"/>'s
/// "borrow the map shape via one well-known key" trick, applied to a <see cref="CanonicalId"/> instead
/// of a <c>bool</c>.
/// </summary>
public class ReferenceFieldAlternativesTests
{
    [Fact]
    public void ToAlternatives_SomeId_ProducesASingleEntryUnderTheWellKnownKey()
    {
        var id = CanonicalId.Mint();

        var alternatives = ReferenceFieldAlternatives.ToAlternatives(id);

        var entry = Assert.Single(alternatives);
        Assert.Equal(ReferenceFieldAlternatives.Key, entry.Key);
        Assert.Equal(id.Value, entry.Value);
    }

    [Fact]
    public void ToAlternatives_Null_ProducesAnEmptyMap()
    {
        Assert.Empty(ReferenceFieldAlternatives.ToAlternatives(null));
    }

    [Fact]
    public void FromAlternatives_IsTheInverseOfToAlternatives()
    {
        var id = CanonicalId.Mint();

        Assert.Equal(id, ReferenceFieldAlternatives.FromAlternatives(ReferenceFieldAlternatives.ToAlternatives(id)));
    }

    [Fact]
    public void FromAlternatives_AbsentKey_ReadsAsNull()
    {
        var empty = new Dictionary<string, string>();

        Assert.Null(ReferenceFieldAlternatives.FromAlternatives(empty));
    }
}
