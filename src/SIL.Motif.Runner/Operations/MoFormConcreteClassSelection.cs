using System;
using System.Collections.Generic;
using SIL.LCModel;

namespace SIL.Motif.Runner.Operations;

/// <summary>
/// The hand-written validity logic <c>LexEntry.LexemeForm</c>'s <c>create</c> kind needs because
/// <c>MoForm</c> is <c>abstract="true"</c> — no such object can exist, so a <c>create</c> must name a
/// concrete subclass instead (ADR 0023 decision 2). The manifest cannot derive this mapping from
/// <c>MasterLCModel.xml</c> alone, since the standard morph types are seeded data, not a model
/// declaration it names. Which concrete class a lexeme form should be is exactly this kind of fact: the
/// nineteen standard FieldWorks morph types (root, stem, prefix, suffix, ...) are seeded data with
/// well-known GUIDs (<see cref="MoMorphTypeTags"/>), not a model declaration, so the mapping below is
/// maintained by hand and closed deliberately — an unrecognized morph type fails loudly rather than
/// guessing.
/// </summary>
/// <remarks>
/// Scoped to the two concrete classes this field can hold —
/// <c>MoStemAllomorph</c> "or an affix form" — and deliberately excludes <c>MoAffixProcess</c>: per
/// the layer-1 API surface's collapse criterion, that class is a *rule* (an <c>Input</c> pattern
/// plus an <c>Output</c> action list), not a form with different fields, so it is a different
/// construct entirely and out of scope for "create the entry's lexeme form."
/// </remarks>
internal enum MoFormConcreteClass
{
    StemAllomorph,
    AffixAllomorph,
}

internal static class MoFormConcreteClassSelection
{
    /// <summary>Root/stem-shaped morph types vs affix-shaped ones; the two classes create can produce.</summary>
    private static readonly IReadOnlyDictionary<Guid, MoFormConcreteClass> ByMorphTypeGuid =
        new Dictionary<Guid, MoFormConcreteClass>
        {
            [MoMorphTypeTags.kguidMorphRoot] = MoFormConcreteClass.StemAllomorph,
            [MoMorphTypeTags.kguidMorphBoundRoot] = MoFormConcreteClass.StemAllomorph,
            [MoMorphTypeTags.kguidMorphStem] = MoFormConcreteClass.StemAllomorph,
            [MoMorphTypeTags.kguidMorphBoundStem] = MoFormConcreteClass.StemAllomorph,
            [MoMorphTypeTags.kguidMorphParticle] = MoFormConcreteClass.StemAllomorph,
            [MoMorphTypeTags.kguidMorphClitic] = MoFormConcreteClass.StemAllomorph,
            [MoMorphTypeTags.kguidMorphProclitic] = MoFormConcreteClass.StemAllomorph,
            [MoMorphTypeTags.kguidMorphEnclitic] = MoFormConcreteClass.StemAllomorph,
            [MoMorphTypeTags.kguidMorphPhrase] = MoFormConcreteClass.StemAllomorph,
            [MoMorphTypeTags.kguidMorphDiscontiguousPhrase] = MoFormConcreteClass.StemAllomorph,

            [MoMorphTypeTags.kguidMorphPrefix] = MoFormConcreteClass.AffixAllomorph,
            [MoMorphTypeTags.kguidMorphSuffix] = MoFormConcreteClass.AffixAllomorph,
            [MoMorphTypeTags.kguidMorphInfix] = MoFormConcreteClass.AffixAllomorph,
            [MoMorphTypeTags.kguidMorphCircumfix] = MoFormConcreteClass.AffixAllomorph,
            [MoMorphTypeTags.kguidMorphSimulfix] = MoFormConcreteClass.AffixAllomorph,
            [MoMorphTypeTags.kguidMorphSuprafix] = MoFormConcreteClass.AffixAllomorph,
            [MoMorphTypeTags.kguidMorphInfixingInterfix] = MoFormConcreteClass.AffixAllomorph,
            [MoMorphTypeTags.kguidMorphPrefixingInterfix] = MoFormConcreteClass.AffixAllomorph,
            [MoMorphTypeTags.kguidMorphSuffixingInterfix] = MoFormConcreteClass.AffixAllomorph,
        };

    public static MoFormConcreteClass ClassFor(IMoMorphType morphType, string kind)
    {
        if (ByMorphTypeGuid.TryGetValue(morphType.Guid, out var concreteClass))
            return concreteClass;

        throw new InvalidOperationException(
            $"'{kind}' operation: morph type '{morphType.Guid}' is not one of the standard FieldWorks " +
            "morph types Motif knows how to realize as a concrete allomorph (MoStemAllomorph or " +
            "MoAffixAllomorph). This mapping lives outside MasterLCModel.xml and is hand-maintained: add " +
            $"the morph type to {nameof(MoFormConcreteClassSelection)} before it can be used to create a " +
            "lexeme form.");
    }

    /// <summary>Constructs a new, empty <paramref name="concreteClass"/> instance with the given
    /// identity — not yet attached to any owner.</summary>
    public static IMoForm Create(LcmCache cache, MoFormConcreteClass concreteClass, Guid id) => concreteClass switch
    {
        MoFormConcreteClass.StemAllomorph =>
            cache.ServiceLocator.GetInstance<IMoStemAllomorphFactory>().Create(id),
        MoFormConcreteClass.AffixAllomorph =>
            cache.ServiceLocator.GetInstance<IMoAffixAllomorphFactory>().Create(id),
        _ => throw new ArgumentOutOfRangeException(nameof(concreteClass), concreteClass, "Unknown concrete MoForm class."),
    };
}
