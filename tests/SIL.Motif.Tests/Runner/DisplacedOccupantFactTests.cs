using SIL.Motif.Contract.Ids;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using SIL.LCModel.Infrastructure;
using Xunit;

namespace SIL.Motif.Tests.Runner;

/// <summary>
/// Settles one factual question the contract and the implementation disagree about: **when an
/// <c>owning/atomic</c> slot is overwritten, does the displaced occupant survive as an orphan, or is it
/// destroyed by the assignment?**
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/change-set-contract.md</c>'s "Owning-atomic replacement" is emphatic that it survives —
/// *"Implicit detach, not cascade delete... The displaced occupant is a disclosed orphan effect... The runner
/// refuses to apply unless the same Change Set also disposes of the displaced object... Silent orphaning here
/// is the `SetPartOfSpeech`/MSA bug class this contract exists to prevent."*
/// </para>
/// <para>
/// `MOT-4` slice 2 shipped <c>create</c>-into-occupied with no such disclosure or refusal, on the basis of an
/// empirical claim that the incumbent is in fact destroyed. That claim was not asserted anywhere:
/// <c>LexEntryLexemeFormOperationsTests.Create_IntoAnOccupiedSlot_...</c> captures the old GUID and checks it
/// appears in the Dry Run's <c>Before</c>, but never checks whether the object still exists afterwards — while
/// the <c>Delete_...</c> test immediately below it *does* assert <c>IsValidObjectId</c>.
/// </para>
/// <para>
/// So this test asks LibLCM directly, with no Motif machinery in the way. Whichever way it answers, one of the
/// two is wrong and the answer decides which: if the incumbent survives, slice 2 has shipped the bug class the
/// contract names; if it does not, the contract's premise is wrong for <c>owning/atomic</c> and the
/// refuse-unless-disposed rule is guarding nothing.
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

        // The plainest possible expression of the question: one owning-atomic assignment, via LibLCM's own
        // factory, with no Motif lowering involved.
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
