using System;
using System.Collections.Generic;
using System.Linq;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Runner.Composers;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests.Composers;

/// <summary>
/// Proves <see cref="AuthorFeatureStructureComposer.Build"/>'s lowering shape against a real project —
/// the grammar counterpart to <see cref="AuthorLexemeFormComposerTests"/>. The end-to-end dry-run/apply
/// round trip lives in <see cref="AuthorFeatureStructureEndToEndTests"/>; this class is about the shape
/// of what <see cref="AuthorFeatureStructureComposer.Build"/> returns, not about running it.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class AuthorFeatureStructureComposerTests : IDisposable
{
    private readonly LcmCache _cache;
    private readonly SeededProject _seed;

    public AuthorFeatureStructureComposerTests(PristineProjectFixture pristine)
    {
        _cache = pristine.NewScratch();
        _seed = pristine.Seed;
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
    }

    /// <summary>The seeded first sense's own MSA, not exposed directly by <see cref="SeededProject"/>.</summary>
    private CanonicalId FirstMsa() => CanonicalId.FromGuid(
        _cache.ServiceLocator.GetInstance<ILexSenseRepository>().GetObject(_seed.FirstSenseId)
            .MorphoSyntaxAnalysisRA.Guid);

    [Fact]
    public void Build_ProducesOneCreateOperation_TargetingTheMsa()
    {
        var msa = FirstMsa();
        var intent = new AuthorFeatureStructureIntent(msa);

        var operations = AuthorFeatureStructureComposer.Build(_cache, intent);

        var op = Assert.Single(operations);
        Assert.Equal(MoStemMsaMsFeaturesOperationKinds.CreateMsFeatures, op.Kind);
        Assert.Equal(msa, op.Target);
        Assert.NotNull(op.EntityId);
        Assert.Empty(op.DependsOn);
        Assert.Equal("{}", op.After!.Value.GetRawText());
    }

    [Fact]
    public void Build_MsaAlreadyHasAFeatureStructure_ThrowsFailingClosed_RatherThanSilentlyReplacingIt()
    {
        var msa = FirstMsa();
        // First author succeeds and actually attaches a feature structure to the live cache.
        var first = AuthorFeatureStructureComposer.Build(_cache, new AuthorFeatureStructureIntent(msa));
        SIL.LCModel.Infrastructure.NonUndoableUnitOfWorkHelper.Do(_cache.ActionHandlerAccessor, () =>
            MoStemMsaMsFeaturesCreateLowering.Apply(
                _cache,
                (IMoStemMsa)_cache.ServiceLocator.GetInstance<ICmObjectRepository>().GetObject(msa.ToGuid()),
                first[0].EntityId!.Value.ToGuid()));

        var ex = Assert.Throws<InvalidOperationException>(
            () => AuthorFeatureStructureComposer.Build(_cache, new AuthorFeatureStructureIntent(msa)));
        Assert.Contains("already has a feature structure", ex.Message);
    }

    [Fact]
    public void Build_MsaDoesNotExist_ThrowsFailingClosed()
    {
        var intent = new AuthorFeatureStructureIntent(CanonicalId.FromGuid(Guid.NewGuid()));

        Assert.ThrowsAny<Exception>(() => AuthorFeatureStructureComposer.Build(_cache, intent));
    }

    [Fact]
    public void Build_MsaResolvesToTheWrongType_ThrowsFailingClosed()
    {
        // The seeded entry's id, presented where an MSA is required.
        var intent = new AuthorFeatureStructureIntent(CanonicalId.FromGuid(_seed.FirstEntryId));

        var ex = Assert.Throws<InvalidOperationException>(() => AuthorFeatureStructureComposer.Build(_cache, intent));
        Assert.Contains("not a MoStemMsa", ex.Message);
    }

    [Fact]
    public void Build_SameIntent_TwiceWithADeterministicIdSource_ProducesIdenticalOperations()
    {
        var intent = new AuthorFeatureStructureIntent(FirstMsa());
        var fixedIds = Enumerable.Range(0, 4).Select(i => CanonicalId.FromGuid(new Guid(i, 0, 0, new byte[8]))).ToArray();

        var first = AuthorFeatureStructureComposer.Build(_cache, intent, DeterministicMinter(fixedIds));
        var second = AuthorFeatureStructureComposer.Build(_cache, intent, DeterministicMinter(fixedIds));

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Kind, second[i].Kind);
            Assert.Equal(first[i].OperationId, second[i].OperationId);
            Assert.Equal(first[i].EntityId, second[i].EntityId);
            Assert.Equal(first[i].Target, second[i].Target);
        }
    }

    private static Func<CanonicalId> DeterministicMinter(IReadOnlyList<CanonicalId> ids)
    {
        var queue = new Queue<CanonicalId>(ids);
        return () => queue.Dequeue();
    }
}
