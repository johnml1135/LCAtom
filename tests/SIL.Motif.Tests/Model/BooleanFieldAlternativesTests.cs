using SIL.Motif.Model.Snapshot;
using Xunit;

namespace SIL.Motif.Tests.Model;

/// <summary>
/// LibLCM-free unit tests for the Boolean-as-alternatives-map convention MOT-4's Boolean fields
/// (<c>LexEntry.DoNotUseForParsing</c>, <c>MoForm.IsAbstract</c>) use so a scalar value can reuse
/// <see cref="SIL.Motif.Model.Effects.ExpectedEffect.Before"/>/<c>After</c>'s already-shipped
/// <c>IReadOnlyDictionary&lt;string, string&gt;</c> shape without widening it.
/// </summary>
public class BooleanFieldAlternativesTests
{
    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void ToAlternatives_ProducesASingleEntryUnderTheWellKnownKey(bool value, string expectedText)
    {
        var alternatives = BooleanFieldAlternatives.ToAlternatives(value);

        var entry = Assert.Single(alternatives);
        Assert.Equal(BooleanFieldAlternatives.Key, entry.Key);
        Assert.Equal(expectedText, entry.Value);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void FromAlternatives_IsTheInverseOfToAlternatives(string text, bool expected)
    {
        var alternatives = new Dictionary<string, string> { [BooleanFieldAlternatives.Key] = text };

        Assert.Equal(expected, BooleanFieldAlternatives.FromAlternatives(alternatives));
    }

    [Fact]
    public void FromAlternatives_AbsentKey_ReadsAsFalse()
    {
        var empty = new Dictionary<string, string>();

        Assert.False(BooleanFieldAlternatives.FromAlternatives(empty));
    }

    [Fact]
    public void ToAlternatives_RoundTripsThroughFromAlternatives()
    {
        Assert.True(BooleanFieldAlternatives.FromAlternatives(BooleanFieldAlternatives.ToAlternatives(true)));
        Assert.False(BooleanFieldAlternatives.FromAlternatives(BooleanFieldAlternatives.ToAlternatives(false)));
    }
}
