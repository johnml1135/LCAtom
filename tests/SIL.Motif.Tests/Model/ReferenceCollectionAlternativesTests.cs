using SIL.Motif.Contract.Ids;
using SIL.Motif.Model.Snapshot;
using Xunit;

namespace SIL.Motif.Tests.Model;

/// <summary>
/// LibLCM-free unit tests for the member-set-as-alternatives-map convention used for
/// <c>rel/col</c> and <c>rel/seq</c> <c>addRef</c>/<c>removeRef</c> fields (<c>LexEntry.DialectLabels</c>,
/// <c>.DoNotPublishIn</c>, <c>.DoNotShowMainEntryIn</c>): the full membership, not the one member an
/// operation added or removed, exactly mirroring how a MultiUnicode field's before/after is always the
/// whole alternatives map rather than only the one writing system that changed
/// (<see cref="SIL.Motif.Model.Effects.ExpectedEffect"/>'s remarks).
/// </summary>
public class ReferenceCollectionAlternativesTests
{
    [Fact]
    public void ToAlternatives_EachMemberIsItsOwnKeyAndValue()
    {
        var a = CanonicalId.Mint();
        var b = CanonicalId.Mint();

        var alternatives = ReferenceCollectionAlternatives.ToAlternatives(new[] { a, b });

        Assert.Equal(2, alternatives.Count);
        Assert.Equal(a.Value, alternatives[a.Value]);
        Assert.Equal(b.Value, alternatives[b.Value]);
    }

    [Fact]
    public void ToAlternatives_Empty_ProducesAnEmptyMap()
    {
        Assert.Empty(ReferenceCollectionAlternatives.ToAlternatives(Array.Empty<CanonicalId>()));
    }

    [Fact]
    public void FromAlternatives_IsTheInverseOfToAlternatives()
    {
        var a = CanonicalId.Mint();
        var b = CanonicalId.Mint();
        var alternatives = ReferenceCollectionAlternatives.ToAlternatives(new[] { a, b });

        var roundTripped = ReferenceCollectionAlternatives.FromAlternatives(alternatives);

        Assert.Equal(new[] { a, b }.OrderBy(x => x.Value), roundTripped.OrderBy(x => x.Value));
    }

    [Fact]
    public void FromAlternatives_Empty_ReadsAsNoMembers()
    {
        Assert.Empty(ReferenceCollectionAlternatives.FromAlternatives(new Dictionary<string, string>()));
    }
}
