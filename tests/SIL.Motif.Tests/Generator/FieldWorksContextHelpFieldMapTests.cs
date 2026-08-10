using SIL.Motif.Generator.Descriptions.Harvest;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// Sanity checks on the curated map itself, independent of whether a FieldWorks checkout is present: no two
/// entries can claim the same (Class, Field) — that would make <see cref="KindDescriptionRefresher"/>'s
/// citation depend on dictionary insertion order — and every entry names a real, non-empty confidence and
/// verification note, because an uncited "trust me" curated entry is exactly what this map exists to avoid.
/// </summary>
public class FieldWorksContextHelpFieldMapTests
{
    [Fact]
    public void NoTwoEntries_ClaimTheSameField()
    {
        var keys = FieldWorksContextHelpFieldMap.Entries.Select(e => (e.Class, e.Field)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void NoTwoEntries_ShareAContextHelpId()
    {
        // Each ContextHelp.xml id should be consumed by at most one (Class, Field) — reusing one id for two
        // fields would mean the harvester attaches the same sentence to two different meanings.
        var ids = FieldWorksContextHelpFieldMap.Entries.Select(e => e.ContextHelpId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void EveryEntry_RecordsAConfidenceAndAVerificationNote()
    {
        Assert.All(FieldWorksContextHelpFieldMap.Entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Confidence));
            Assert.False(string.IsNullOrWhiteSpace(e.VerifiedAgainst));
        });
    }
}
