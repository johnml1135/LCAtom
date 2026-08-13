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
/// Proves <see cref="AuthorLexemeFormComposer.Build"/>'s lowering shape against a real project: which
/// operations it emits, in what order, which carry a <c>dependsOn</c> edge and which deliberately do
/// not, that it resolves and validates every reference before authoring anything, and that it is
/// deterministic. The end-to-end dry-run/apply/save round trip lives in
/// <see cref="AuthorLexemeFormEndToEndTests"/>; this class is about the shape of what
/// <see cref="AuthorLexemeFormComposer.Build"/> returns, not about running it.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class AuthorLexemeFormComposerTests : IDisposable
{
    private readonly LcmCache _cache;
    private readonly SeededProject _seed;

    public AuthorLexemeFormComposerTests(PristineProjectFixture pristine)
    {
        _cache = pristine.NewScratch();
        _seed = pristine.Seed;
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
    }

    [Fact]
    public void Build_LexemeFormOnly_ProducesOneCreateOperation()
    {
        var intent = new AuthorLexemeFormIntent(
            CanonicalId.FromGuid(_seed.SecondEntryId), StemMorphType(), "fr", "zzMotifNewForm");

        var operations = AuthorLexemeFormComposer.Build(_cache, intent);

        var op = Assert.Single(operations);
        Assert.Equal(LexEntryLexemeFormOperationKinds.CreateLexemeForm, op.Kind);
        Assert.Equal(intent.Entry, op.Target);
        Assert.NotNull(op.EntityId);
        Assert.Empty(op.DependsOn);
    }

    [Fact]
    public void Build_WithIsAbstract_ProducesCreateThenSetIsAbstract_DependingOnTheCreate()
    {
        var intent = new AuthorLexemeFormIntent(
            CanonicalId.FromGuid(_seed.SecondEntryId), StemMorphType(), "fr", "zzMotifTemplate", IsAbstract: true);

        var operations = AuthorLexemeFormComposer.Build(_cache, intent);

        Assert.Equal(2, operations.Count);
        var create = operations[0];
        var setAbstract = operations[1];

        Assert.Equal(LexEntryLexemeFormOperationKinds.CreateLexemeForm, create.Kind);
        Assert.Equal(MoFormIsAbstractOperationKinds.SetIsAbstract, setAbstract.Kind);
        Assert.Equal(create.EntityId, setAbstract.Target); // targets the form the create proposes
        var dependency = Assert.Single(setAbstract.DependsOn);
        Assert.Equal(create.OperationId, dependency.OperationId);
    }

    [Fact]
    public void Build_WithSenseGloss_ProducesCreateAndSetGloss_WithNoDependencyBetweenThem()
    {
        var senseId = CanonicalId.FromGuid(_seed.SecondSenseId);
        var intent = new AuthorLexemeFormIntent(
            CanonicalId.FromGuid(_seed.SecondEntryId), StemMorphType(), "fr", "zzMotifNewForm",
            Sense: senseId, GlossWritingSystem: "en", GlossText: "new gloss");

        var operations = AuthorLexemeFormComposer.Build(_cache, intent);

        Assert.Equal(2, operations.Count);
        var create = operations[0];
        var setGloss = operations[1];

        Assert.Equal(LexEntryLexemeFormOperationKinds.CreateLexemeForm, create.Kind);
        Assert.Equal(LexicalSenseOperationKinds.SetGloss, setGloss.Kind);
        Assert.Equal(senseId, setGloss.Target);
        // The sense already exists, so nothing in this Proposal need run before setGloss.
        Assert.Empty(setGloss.DependsOn);
    }

    [Fact]
    public void Build_WithIsAbstractAndSenseGloss_ProducesAllThreeInDeclaredOrder()
    {
        var senseId = CanonicalId.FromGuid(_seed.SecondSenseId);
        var intent = new AuthorLexemeFormIntent(
            CanonicalId.FromGuid(_seed.SecondEntryId), StemMorphType(), "fr", "zzMotifNewForm",
            IsAbstract: true, Sense: senseId, GlossWritingSystem: "en", GlossText: "new gloss");

        var operations = AuthorLexemeFormComposer.Build(_cache, intent);

        Assert.Equal(3, operations.Count);
        Assert.Equal(
            new[]
            {
                LexEntryLexemeFormOperationKinds.CreateLexemeForm,
                MoFormIsAbstractOperationKinds.SetIsAbstract,
                LexicalSenseOperationKinds.SetGloss,
            },
            operations.Select(op => op.Kind));
    }

    [Fact]
    public void Build_EntryDoesNotExist_ThrowsFailingClosed()
    {
        var intent = new AuthorLexemeFormIntent(
            CanonicalId.FromGuid(Guid.NewGuid()), StemMorphType(), "fr", "x");

        Assert.ThrowsAny<Exception>(() => AuthorLexemeFormComposer.Build(_cache, intent));
    }

    [Fact]
    public void Build_EntryResolvesToTheWrongType_ThrowsFailingClosed()
    {
        // The seeded sense's id, presented where an entry is required.
        var intent = new AuthorLexemeFormIntent(
            CanonicalId.FromGuid(_seed.FirstSenseId), StemMorphType(), "fr", "x");

        var ex = Assert.Throws<InvalidOperationException>(() => AuthorLexemeFormComposer.Build(_cache, intent));
        Assert.Contains("not a LexEntry", ex.Message);
    }

    [Fact]
    public void Build_SenseNotBelongingToEntry_ThrowsFailingClosed()
    {
        // FirstSenseId belongs to the first entry, not the second.
        var intent = new AuthorLexemeFormIntent(
            CanonicalId.FromGuid(_seed.SecondEntryId), StemMorphType(), "fr", "x",
            Sense: CanonicalId.FromGuid(_seed.FirstSenseId), GlossWritingSystem: "en", GlossText: "y");

        var ex = Assert.Throws<InvalidOperationException>(() => AuthorLexemeFormComposer.Build(_cache, intent));
        Assert.Contains("does not belong to entry", ex.Message);
    }

    [Fact]
    public void Build_SenseWithoutGlossFields_ThrowsFailingClosed()
    {
        // Constructed directly, bypassing AuthorLexemeFormIntentParser's own version of this check.
        var intent = new AuthorLexemeFormIntent(
            CanonicalId.FromGuid(_seed.SecondEntryId), StemMorphType(), "fr", "x",
            Sense: CanonicalId.FromGuid(_seed.SecondSenseId));

        Assert.Throws<InvalidOperationException>(() => AuthorLexemeFormComposer.Build(_cache, intent));
    }

    [Fact]
    public void Build_SameIntent_TwiceWithADeterministicIdSource_ProducesIdenticalOperations()
    {
        var senseId = CanonicalId.FromGuid(_seed.SecondSenseId);
        var intent = new AuthorLexemeFormIntent(
            CanonicalId.FromGuid(_seed.SecondEntryId), StemMorphType(), "fr", "zzMotifNewForm",
            IsAbstract: true, Sense: senseId, GlossWritingSystem: "en", GlossText: "new gloss");

        var fixedIds = Enumerable.Range(0, 8).Select(i => CanonicalId.FromGuid(new Guid(i, 0, 0, new byte[8]))).ToArray();

        var first = AuthorLexemeFormComposer.Build(_cache, intent, DeterministicMinter(fixedIds));
        var second = AuthorLexemeFormComposer.Build(_cache, intent, DeterministicMinter(fixedIds));

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Kind, second[i].Kind);
            Assert.Equal(first[i].OperationId, second[i].OperationId);
            Assert.Equal(first[i].EntityId, second[i].EntityId);
            Assert.Equal(first[i].Target, second[i].Target);
            Assert.Equal(first[i].After?.GetRawText(), second[i].After?.GetRawText());
            Assert.Equal(
                first[i].DependsOn.Select(d => d.OperationId),
                second[i].DependsOn.Select(d => d.OperationId));
        }
    }

    private static Func<CanonicalId> DeterministicMinter(IReadOnlyList<CanonicalId> ids)
    {
        var queue = new Queue<CanonicalId>(ids);
        return () => queue.Dequeue();
    }

    private static CanonicalId StemMorphType() => CanonicalId.FromGuid(MoMorphTypeTags.kguidMorphStem);
}
