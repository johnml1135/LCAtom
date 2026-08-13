using SIL.Motif.Host.WritingSystems;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests.WritingSystems;

/// <summary>
/// The vernacular writing systems are read <b>in order</b>, and the order is recorded, because the order
/// decides the grammar.
/// </summary>
/// <remarks>
/// <para>
/// This is not a formatting concern. `DefaultVernacularWritingSystem` is
/// <c>m_currentVernacularWritingSystems.FirstOrDefault()</c> (liblcm <c>OverridesLangProj.cs:844-849</c>), and
/// <c>HCLoader</c> reads every phoneme's grapheme and every lexical form through it — dropping any entry whose
/// form is empty in that writing system. So promoting a different writing system to first changes the phoneme
/// inventory and can silently shrink the grammar.
/// </para>
/// <para>
/// The failure this guards against is specific: a report that says <i>"grammar unchanged"</i> after exactly
/// that edit. If <see cref="WritingSystemInventoryReader"/> ever sorts, dedupes or otherwise normalises the
/// vernacular list, the digest stops distinguishing two arrangements that produce different grammars, and the
/// report starts lying in the one direction that matters.
/// </para>
/// </remarks>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class WritingSystemInventoryTests : IDisposable
{
    private readonly LcmCache _cache;

    public WritingSystemInventoryTests(PristineProjectFixture pristine)
    {
        _cache = pristine.NewScratch();
    }

    [Fact]
    public void TheFirstVernacularWritingSystem_IsTheOneMarkedDefault_AndMatchesTheCache()
    {
        var inventory = WritingSystemInventoryReader.Read(_cache);

        Assert.NotEmpty(inventory.Vernacular);
        Assert.NotNull(inventory.DefaultVernacular);

        // Exactly one is flagged, and it is the first — not merely "one of them is".
        Assert.Single(inventory.Vernacular, ws => ws.IsDefaultVernacular);
        Assert.True(inventory.Vernacular[0].IsDefaultVernacular);

        // Agrees with what the parser actually uses, asked of LibLCM directly, not re-derived from this list.
        var cacheDefault = _cache.ServiceLocator.WritingSystems.DefaultVernacularWritingSystem;
        Assert.Equal(cacheDefault.Id, inventory.DefaultVernacular!.Id);
    }

    [Fact]
    public void TheDigest_ChangesWhenTheOrderChanges_AndIsStableWhenNothingDoes()
    {
        var first = WritingSystemInventoryReader.Read(_cache);
        var second = WritingSystemInventoryReader.Read(_cache);

        Assert.Equal(first.Digest, second.Digest);
        Assert.StartsWith("sha256:", first.Digest);

        // A real precondition, not a guard that silently no-ops the reorder assertion below.
        Assert.True(first.Vernacular.Count > 1, "fixture must seed at least two vernacular writing systems");
        // Reorders the ids here, not the project, to test the digest itself.
        var reversed = first.Vernacular.Reverse().ToList();
        var reorderedDigest = DigestOf(reversed.Select(ws => ws.Id));
        Assert.NotEqual(DigestOf(first.Vernacular.Select(ws => ws.Id)), reorderedDigest);
    }

    [Fact]
    public void AnalysisWritingSystemsAreReported_ButDoNotEnterTheDigest()
    {
        var inventory = WritingSystemInventoryReader.Read(_cache);

        // They are worth showing a reader: glosses live in them.
        Assert.NotEmpty(inventory.Analysis);

        // HCLoader reads these for naming only; a digest that moved when a gloss language changed would be spurious.
        Assert.Equal(DigestOf(inventory.Vernacular.Select(ws => ws.Id)), inventory.Digest);
    }

    [Fact]
    public void TheInventoryOffersNoWayToChangeAnything_AndSaysWhereToGoInstead()
    {
        // Enforced by reflection, not convention: a mutating method here means Motif quietly owns FieldWorks config.
        var mutators = typeof(WritingSystemInventory).GetMethods()
            .Where(m => m.Name.StartsWith("Set", StringComparison.Ordinal)
                        || m.Name.StartsWith("Add", StringComparison.Ordinal)
                        || m.Name.StartsWith("Reorder", StringComparison.Ordinal)
                        || m.Name.StartsWith("Remove", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            mutators.Count == 0,
            "WritingSystemInventory has gained a mutating method: " +
            string.Join(", ", mutators.Select(m => m.Name)) +
            ". Writing systems are edited in FieldWorks (ADR 0034's state/change boundary).");

        Assert.Contains("FieldWorks", WritingSystemInventory.EditingIsAFieldWorksJob);
    }

    private static string DigestOf(IEnumerable<string> ids)
    {
        var joined = string.Join("\n", ids);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(joined));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
    }
}
