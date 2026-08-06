using System;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Runner.Resolution;
using SIL.LCModel;

namespace SIL.Motif.Runner.Operations;

/// <summary>
/// Resolve-and-type-check plus the one LibLCM write every <c>rel/atomic</c> <c>set|clear</c> kind
/// performs — the reference-field analogue of <see cref="MultiAlternativesFieldLowering"/>, shared by
/// every generated <c>*Lowering</c> class for a single-valued reference field (MOT-4 slice 2;
/// <c>MoForm.MorphType</c> today). Also used by the hand-written <c>LexEntry.LexemeForm</c>
/// <c>create</c>/<c>delete</c> kind to resolve the payload's <c>morphType</c> reference, since that is
/// exactly the same "resolve one <see cref="CanonicalId"/>, check its runtime type" preamble.
/// </summary>
internal static class ReferenceFieldLowering
{
    /// <summary>
    /// Resolves <paramref name="id"/> and checks it is a <typeparamref name="TRef"/>, with the same
    /// "is not a X (it is a Y)" wording <see cref="TargetResolution"/> uses for an operation's own
    /// <c>target</c> — this resolves a *referenced* object named inside <c>after</c>, not the
    /// operation's target, so it is a distinct (if structurally similar) helper.
    /// </summary>
    public static TRef Resolve<TRef>(LcmCache cache, CanonicalId id, string kind)
        where TRef : ICmObject
    {
        var resolved = CanonicalIdResolver.Resolve(cache, id);
        if (resolved is not TRef typed)
        {
            var expectedName = typeof(TRef).Name.StartsWith("I", StringComparison.Ordinal)
                ? typeof(TRef).Name.Substring(1)
                : typeof(TRef).Name;
            throw new InvalidOperationException(
                $"'{kind}' operation: referenced object '{id.Value}' is not a {expectedName} " +
                $"(it is a {resolved.GetType().Name}).");
        }

        return typed;
    }

    /// <summary>Lowers a <c>set</c> kind: resolves <paramref name="refId"/> and passes it to
    /// <paramref name="setter"/> (the generated field's own <c>x.SomeRA = value</c> assignment).</summary>
    public static void ApplySet<TRef>(LcmCache cache, Action<TRef> setter, CanonicalId refId, string kind)
        where TRef : ICmObject
    {
        setter(Resolve<TRef>(cache, refId, kind));
    }

    /// <summary>
    /// Lowers a <c>clear</c> kind: detaches the reference by passing <c>null</c> to
    /// <paramref name="setter"/>. Takes the same <c>Action&lt;TRef&gt;</c> shape as
    /// <see cref="ApplySet{TRef}"/> (rather than <c>Action&lt;TRef?&gt;</c>) so one generated field's
    /// setter delegate — <c>value => moForm.MorphTypeRA = value</c> — serves both verbs; nullability
    /// is a compile-time annotation only, erased at the CLR level <c>Action&lt;T&gt;</c> operates at.
    /// </summary>
    public static void ApplyClear<TRef>(Action<TRef> setter)
        where TRef : ICmObject
    {
        setter(default!);
    }
}
