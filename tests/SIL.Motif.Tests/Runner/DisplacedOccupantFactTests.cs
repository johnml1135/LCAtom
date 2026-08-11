using SIL.Motif.Contract.Ids;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using SIL.LCModel.Infrastructure;
using Xunit;

namespace SIL.Motif.Tests.Runner;

/// <summary>
/// Settles one factual question: <b>when an <c>owning/atomic</c> slot is overwritten, does the displaced
/// occupant survive as an orphan, or is it destroyed by the assignment?</b> It is destroyed — pinned by
/// `OverwritingAnOwningAtomicSlot_DestroysTheIncumbent_RatherThanLeavingAnOrphan` below, which asks
/// LibLCM directly with no Motif machinery in the way.
/// </summary>
/// <remarks>
/// <para>
/// The Change Set contract's original reasoning for "Owning-atomic replacement" assumed the opposite —
/// <i>"Implicit detach, not cascade delete... The displaced occupant is a disclosed orphan effect... The runner
/// refuses to apply unless the same Change Set also disposes of the displaced object... Silent orphaning here
/// is the `SetPartOfSpeech`/MSA bug class this contract exists to prevent."</i> The contract now carries a
/// correction recording that this premise is wrong for owning slots, and cites this test for it.
/// </para>
/// <para>
/// The distinction the original reasoning collapsed: de-referencing a <c>rel</c> genuinely leaves its target
/// alive, so the orphan hazard there is real. Dropping an <em>owned</em> object is not that case — LibLCM's
/// ownership model is exclusive and total, so an owned object its owner lets go of has nowhere left to live.
/// The refuse-unless-disposed rule was written for the reference hazard; over ownership it guards nothing.
/// </para>
/// <para>
/// Scope of the evidence, stated honestly: verified for <c>LexEntry.LexemeForm</c>, and expected to generalise
/// across the 69 <c>owning/atomic</c> fields because the ownership model is the same for all of them, but not
/// separately verified for each. That is why <c>create</c>-into-occupied ships with no orphan disclosure.
/// </para>
/// </remarks>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class DisplacedOccupantFactTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly LcmCache _cache;

    public DisplacedOccupantFactTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.Tests.Displaced", Guid.NewGuid().ToString("N"));
        var fwDataPath = TestLangProjFixture.CopyToTempAndGetFwDataPath(_tempRoot);
        _cache = new FwDataProjectLoader().LoadCache(fwDataPath);
    }

    [Fact]
    public void OverwritingAnOwningAtomicSlot_DestroysTheIncumbent_RatherThanLeavingAnOrphan()
    {
        var entry = _cache.ServiceLocator.GetInstance<ILexEntryRepository>()
            .AllInstances().First(e => e.LexemeFormOA is not null);

        var incumbentGuid = entry.LexemeFormOA!.Guid;
        var objects = _cache.ServiceLocator.GetInstance<ICmObjectRepository>();

        Assert.True(objects.IsValidObjectId(incumbentGuid), "precondition: the incumbent exists");

        // The plainest expression of the question: one owning-atomic assignment via LibLCM's own factory.
        NonUndoableUnitOfWorkHelper.Do(_cache.ActionHandlerAccessor, () =>
        {
            var replacement = _cache.ServiceLocator.GetInstance<IMoStemAllomorphFactory>().Create();
            entry.LexemeFormOA = replacement;
        });

        var incumbentSurvives = objects.IsValidObjectId(incumbentGuid);

        Assert.False(
            incumbentSurvives,
            "The displaced occupant survived the overwrite, so change-set-contract.md's 'implicit detach, " +
            "not cascade delete' is correct and MOT-4 slice 2's create-into-occupied is silently orphaning " +
            "it — the SetPartOfSpeech/MSA bug class the contract exists to prevent. The contract requires the " +
            "orphan be disclosed as an effect and the apply refused unless the Change Set disposes of it.");
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best effort */ }
    }
}
