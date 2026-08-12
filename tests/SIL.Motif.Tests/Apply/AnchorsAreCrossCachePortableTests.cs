using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Runner.Apply;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests.Apply;

/// <summary>
/// The load-bearing assumption behind measuring on a copy and applying to the original: <b>a footprint
/// digest computed against one <see cref="LcmCache"/> matches the digest computed against a different
/// cache holding the same data.</b>
/// </summary>
/// <remarks>
/// <para>
/// ADR 0016 has the Dry Run mutate a
/// file copy and bind an anchor from it, then has Apply re-probe the live project and refuse on
/// mismatch. That only works if the digest is a function of the <i>data</i>. If an hvo — LibLCM's
/// per-cache integer object id, assigned in load order and not stable across loads — reached the
/// digest, every anchor would look like drift, the drift check would fail closed on every apply, and
/// the failure would look like a data problem rather than a design one.
/// </para>
/// <para>
/// It holds today because <c>FootprintDigest</c> hashes canonical ids, field names, and alternative
/// values, and because the snapshotters project references through <c>CanonicalId.FromGuid</c> rather
/// than through hvos. Both are easy to regress in a way no other test would notice — a snapshotter that
/// emitted <c>Hvo</c> would still round-trip fine within a single cache. So this asserts the property
/// directly, across two caches, on a reference field as well as a text one.
/// </para>
/// </remarks>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
[Trait("Fixture", "FieldWorks")]
public sealed class AnchorsAreCrossCachePortableTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly LcmCache _cache;

    public AnchorsAreCrossCachePortableTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.Tests.Portable", Guid.NewGuid().ToString("N"));
        var fwDataPath = TestLangProjFixture.CopyToTempAndGetFwDataPath(_tempRoot);
        _cache = new FwDataProjectLoader().LoadCache(fwDataPath);
    }

    [Fact]
    public void AFootprintDigestFromAFileCopy_EqualsTheOneFromTheOriginal()
    {
        var sense = _cache.ServiceLocator.GetInstance<ILexSenseRepository>().AllInstances().First();
        var form = _cache.ServiceLocator.GetInstance<ILexEntryRepository>()
            .AllInstances().First(e => e.LexemeFormOA?.MorphTypeRA is not null).LexemeFormOA!;

        var wsTag = _cache.WritingSystemFactory.GetStrFromWs(_cache.DefaultAnalWs);

        // A reference field too: this is the case that would break if a snapshotter projected an hvo.
        var proposal = new Proposal(
            contractVersions: new Dictionary<string, string> { ["lexical"] = "1.0", ["grammar"] = "1.0" },
            proposalId: CanonicalId.Mint(),
            requires: null,
            operations: new[]
            {
                Operation(LexicalSenseOperationKinds.SetGloss, sense.Guid, new { ws = wsTag, text = "zzPortability" }),
                Operation(MoFormMorphTypeOperationKinds.ClearMorphType, form.Guid, new { }),
            });

        var fromOriginal = FootprintProbe.ComputeCurrentFootprintDigest(_cache, proposal);

        var scratchRoot = Path.Combine(_tempRoot, "copy");
        using var copy = new ScratchCacheFactory().CreateFromFileCopy(_cache.ProjectId.Path, scratchRoot);
        var fromCopy = FootprintProbe.ComputeCurrentFootprintDigest(copy, proposal);

        Assert.Equal(fromOriginal, fromCopy);

        // Not vacuously equal: proves the digest responds to data rather than ignoring its inputs.
        var otherSense = _cache.ServiceLocator.GetInstance<ILexSenseRepository>()
            .AllInstances().First(s => s.Guid != sense.Guid);
        var differentProposal = new Proposal(
            contractVersions: new Dictionary<string, string> { ["lexical"] = "1.0" },
            proposalId: CanonicalId.Mint(),
            requires: null,
            operations: new[] { Operation(LexicalSenseOperationKinds.SetGloss, otherSense.Guid, new { ws = wsTag, text = "zz" }) });

        Assert.NotEqual(fromOriginal, FootprintProbe.ComputeCurrentFootprintDigest(_cache, differentProposal));
    }

    private static OperationEnvelope Operation(string kind, Guid target, object after)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(after));
        return new OperationEnvelope(
            operationId: CanonicalId.Mint(),
            kind: kind,
            target: CanonicalId.FromGuid(target),
            after: document.RootElement.Clone());
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best effort */ }
    }
}
