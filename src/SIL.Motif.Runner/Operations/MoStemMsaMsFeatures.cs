using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Model.Effects;
using SIL.Motif.Model.Snapshot;
using SIL.Motif.Runner.Snapshotting;
using SIL.LCModel;

namespace SIL.Motif.Runner.Operations;

/// <summary>
/// Names the two <c>MoStemMsa.MsFeatures</c> operation kinds — the first hand-written grammar
/// owning/atomic slot alongside the lexical <c>LexEntry.LexemeForm</c> precedent (ADR 0022 §4,
/// "creation validity"). Unlike <c>LexemeForm</c>, an empty <c>FsFeatStruc</c> is already a valid one
/// (LibLCM imposes no minimum on <c>FeatureSpecs</c>), so this create carries no required payload;
/// populating actual feature values is a separate operation against the created object's own identity.
/// </summary>
public static class MoStemMsaMsFeaturesOperationKinds
{
    public const string CreateMsFeatures = "grammar/moStemMsa/createMsFeatures";
    public const string DeleteMsFeatures = "grammar/moStemMsa/deleteMsFeatures";

    [ModuleInitializer]
    internal static void Register()
    {
        OperationKindRegistry.Register(CreateMsFeatures);
        OperationKindRegistry.Register(DeleteMsFeatures);
        OperationHandlerRegistry.Register(CreateMsFeatures, MoStemMsaMsFeaturesCreateHandler.Instance);
        OperationHandlerRegistry.Register(DeleteMsFeatures, MoStemMsaMsFeaturesDeleteHandler.Instance);
    }
}

/// <summary>
/// The <c>after</c> payload for both kinds: <c>{}</c>. Closed: any property is rejected — there is
/// nothing to carry, since a fresh feature structure starts empty.
/// </summary>
public static class MoStemMsaMsFeaturesPayload
{
    private static readonly string[] AllowedProperties = Array.Empty<string>();

    public static void Parse(JsonElement after, string kind)
    {
        ClosedPayloadParsing.RequireObject(after, kind);
        ClosedPayloadParsing.RejectUnknownProperties(after, AllowedProperties, kind);
    }
}

/// <summary>
/// Lowers <see cref="MoStemMsaMsFeaturesOperationKinds.CreateMsFeatures"/>: creates an empty
/// <c>FsFeatStruc</c> with the authored identity and assigns it into <c>msa.MsFeaturesOA</c>.
/// </summary>
/// <remarks>
/// Exactly one owning-atomic assignment, never delete-plus-create — the same change-class rule
/// <c>LexEntryLexemeFormCreateLowering</c> follows. Must run inside an already-open unit of work
/// (ADR 0006 decision 5).
/// </remarks>
public static class MoStemMsaMsFeaturesCreateLowering
{
    public static IFsFeatStruc Apply(LcmCache cache, IMoStemMsa msa, Guid featStrucId)
    {
        var newFeatStruc = cache.ServiceLocator.GetInstance<IFsFeatStrucFactory>().Create(featStrucId);
        msa.MsFeaturesOA = newFeatStruc;
        return newFeatStruc;
    }
}

/// <summary>
/// Lowers <see cref="MoStemMsaMsFeaturesOperationKinds.DeleteMsFeatures"/> through LibLCM's ownership
/// cascade. Requires a feature structure to be present — there is nothing to name a delete over an
/// already-empty slot.
/// </summary>
public static class MoStemMsaMsFeaturesDeleteLowering
{
    public static void Apply(IMoStemMsa msa)
    {
        if (msa.MsFeaturesOA is not { } featStruc)
        {
            throw new InvalidOperationException(
                $"'{MoStemMsaMsFeaturesOperationKinds.DeleteMsFeatures}' operation: MSA '{msa.Guid}' " +
                "has no feature structure to delete.");
        }

        featStruc.Delete();
    }
}

/// <summary>Resolves, snapshots, lowers, and re-snapshots one
/// <see cref="MoStemMsaMsFeaturesOperationKinds.CreateMsFeatures"/> operation.</summary>
internal sealed class MoStemMsaMsFeaturesCreateHandler : IOperationHandler
{
    internal static readonly MoStemMsaMsFeaturesCreateHandler Instance = new();
    private MoStemMsaMsFeaturesCreateHandler() { }

    public ExpectedEffect ApplyAndCaptureEffect(LcmCache cache, OperationEnvelope operation, List<CanonicalId> touchedTargets)
    {
        if (operation.EntityId is not { } entityId)
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId.Value}' of kind '{MoStemMsaMsFeaturesOperationKinds.CreateMsFeatures}' requires 'entityId'.");
        }

        if (operation.After is not { } after)
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId.Value}' of kind '{MoStemMsaMsFeaturesOperationKinds.CreateMsFeatures}' requires 'after'.");
        }

        MoStemMsaMsFeaturesPayload.Parse(after, MoStemMsaMsFeaturesOperationKinds.CreateMsFeatures);

        var (id, msa) = TargetResolution.Resolve<IMoStemMsa>(cache, operation, MoStemMsaMsFeaturesOperationKinds.CreateMsFeatures);
        touchedTargets.Add(id);

        var before = ReferenceFieldSnapshotting.ReadAlternatives(msa.MsFeaturesOA);
        MoStemMsaMsFeaturesCreateLowering.Apply(cache, msa, entityId.ToGuid());
        var afterValue = ReferenceFieldAlternatives.ToAlternatives(entityId);

        return new ExpectedEffect(id, SnapshotFields.MoStemMsaMsFeatures, before, afterValue);
    }

    public ExpectedEffect ReadCurrentFootprint(LcmCache cache, OperationEnvelope operation)
    {
        var (id, msa) = TargetResolution.Resolve<IMoStemMsa>(cache, operation, MoStemMsaMsFeaturesOperationKinds.CreateMsFeatures);
        var current = ReferenceFieldSnapshotting.ReadAlternatives(msa.MsFeaturesOA);
        return new ExpectedEffect(id, SnapshotFields.MoStemMsaMsFeatures, current, current);
    }
}

/// <summary>The <see cref="MoStemMsaMsFeaturesOperationKinds.DeleteMsFeatures"/> counterpart to
/// <see cref="MoStemMsaMsFeaturesCreateHandler"/>.</summary>
internal sealed class MoStemMsaMsFeaturesDeleteHandler : IOperationHandler
{
    internal static readonly MoStemMsaMsFeaturesDeleteHandler Instance = new();
    private MoStemMsaMsFeaturesDeleteHandler() { }

    public ExpectedEffect ApplyAndCaptureEffect(LcmCache cache, OperationEnvelope operation, List<CanonicalId> touchedTargets)
    {
        if (operation.After is not { } after)
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId.Value}' of kind '{MoStemMsaMsFeaturesOperationKinds.DeleteMsFeatures}' requires 'after'.");
        }

        MoStemMsaMsFeaturesPayload.Parse(after, MoStemMsaMsFeaturesOperationKinds.DeleteMsFeatures);

        var (id, msa) = TargetResolution.Resolve<IMoStemMsa>(cache, operation, MoStemMsaMsFeaturesOperationKinds.DeleteMsFeatures);
        touchedTargets.Add(id);

        var before = ReferenceFieldSnapshotting.ReadAlternatives(msa.MsFeaturesOA);
        MoStemMsaMsFeaturesDeleteLowering.Apply(msa);
        var afterValue = ReferenceFieldSnapshotting.ReadAlternatives(msa.MsFeaturesOA);

        return new ExpectedEffect(id, SnapshotFields.MoStemMsaMsFeatures, before, afterValue);
    }

    public ExpectedEffect ReadCurrentFootprint(LcmCache cache, OperationEnvelope operation)
    {
        var (id, msa) = TargetResolution.Resolve<IMoStemMsa>(cache, operation, MoStemMsaMsFeaturesOperationKinds.DeleteMsFeatures);
        var current = ReferenceFieldSnapshotting.ReadAlternatives(msa.MsFeaturesOA);
        return new ExpectedEffect(id, SnapshotFields.MoStemMsaMsFeatures, current, current);
    }
}
