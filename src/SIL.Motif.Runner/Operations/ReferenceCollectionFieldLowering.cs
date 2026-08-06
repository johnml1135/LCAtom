using System.Collections.Generic;
using SIL.Motif.Contract.Ids;
using SIL.LCModel;

namespace SIL.Motif.Runner.Operations;

/// <summary>
/// The one LibLCM write every <c>rel/col</c>/<c>rel/seq</c> <c>addRef</c>/<c>removeRef</c> kind
/// performs — shared by every generated <c>*Lowering</c> class for a reference collection field
/// (MOT-4 slice 2; <c>LexEntry.DialectLabels</c>, <c>.DoNotPublishIn</c>, <c>.DoNotShowMainEntryIn</c>
/// today). LibLCM's own <c>ILcmReferenceCollection&lt;T&gt;</c> and <c>ILcmReferenceSequence&lt;T&gt;</c>
/// both implement the ordinary <see cref="ICollection{T}"/> (<c>Add</c>/<c>Remove</c>/<c>Contains</c>),
/// verified by reflection against the pinned <c>SIL.LCModel</c> package, so one generic helper covers
/// both cardinalities — only the accessor expression a generated field's lowering class passes in
/// differs (<c>entry.DialectLabelsRS</c> vs <c>entry.DoNotPublishInRC</c>).
/// </summary>
/// <remarks>
/// Both operations are idempotent by design: <c>addRef</c> of an already-present member and
/// <c>removeRef</c> of an absent one are no-ops rather than errors, matching "ensure this member is/
/// isn't present" authored intent rather than raw collection-mutation semantics.
/// </remarks>
internal static class ReferenceCollectionFieldLowering
{
    public static void ApplyAddRef<TRef>(LcmCache cache, ICollection<TRef> collection, CanonicalId memberId, string kind)
        where TRef : ICmObject
    {
        var member = ReferenceFieldLowering.Resolve<TRef>(cache, memberId, kind);
        if (!collection.Contains(member))
            collection.Add(member);
    }

    public static void ApplyRemoveRef<TRef>(LcmCache cache, ICollection<TRef> collection, CanonicalId memberId, string kind)
        where TRef : ICmObject
    {
        var member = ReferenceFieldLowering.Resolve<TRef>(cache, memberId, kind);
        collection.Remove(member);
    }
}
