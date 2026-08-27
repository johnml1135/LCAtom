using SIL.Motif.Contract.Responses;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SIL.Motif.Projection;
using SIL.Motif.Projection.Rendering;
using Xunit;

namespace SIL.Motif.Tests.Projection;

/// <summary>
/// Pins the JSON key names of every report projection. These are the names an agent reads, and they are
/// produced by reflection over the record's properties — so renaming a C# property renames the key an
/// agent depends on, silently and with nothing else failing.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately a list of literal strings rather than anything derived. A test that recomputed
/// the names from the same records it is checking would agree with any rename and prove nothing; the
/// point is that changing the contract has to be a decision somebody writes down here.
/// </para>
/// <para>
/// Adding a key is a compatible change and only needs this list extended. Renaming or removing one is
/// not, and a failure here is the intended place to notice that.
/// </para>
/// </remarks>
public sealed class ProjectionJsonContractTests
{
    [Fact]
    public void ProjectSummary_KeysAreTheContract() =>
        AssertKeys(new ProjectSummaryProjection("p", 3), "projectName", "lexicalEntryCount");

    [Fact]
    public void ProposalListItem_KeysAreTheContract() =>
        AssertKeys(
            new ProposalListProjection(new[] { new ProposalListItem("id", "proposed", "a label") }),
            "proposals", "proposalId", "status", "label");

    [Fact]
    public void EffectView_KeysAreTheContract() =>
        AssertKeys(
            new EffectView("id", "Gloss", new[] { new EffectChange("en", "was", "now") }),
            "canonicalId", "field", "changes", "ws", "before", "after");

    [Fact]
    public void AppliedLogEntrySummary_KeysAreTheContract() =>
        AssertKeys(
            new AppliedLogEntrySummary("id", "20260101T000000Z", "user", "sha256:x"),
            "proposalId", "timestampUtc", "user", "intentDigest");

    // Every key emitted at any depth must be named here, and every name here must be emitted.
    private static void AssertKeys<T>(T projection, params string[] expected)
    {
        var json = ProjectionJson.Serialize(projection);
        using var document = JsonDocument.Parse(json);

        var actual = new SortedSet<string>(StringComparer.Ordinal);
        Collect(document.RootElement, actual);

        Assert.Equal(
            expected.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            actual.ToList());
    }

    private static void Collect(JsonElement element, SortedSet<string> keys)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    keys.Add(property.Name);
                    Collect(property.Value, keys);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Collect(item, keys);
                break;
        }
    }
}
