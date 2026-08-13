using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Runner.Composers;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests.Composers;

/// <summary>
/// Pins the load-bearing rule the whole composer design rests on: preconditions live in the
/// Proposal, never in an operation, so what an operation carries is unconditional. This is not a
/// convention <see cref="AuthorLexemeFormComposer"/> merely follows — it is structurally guaranteed,
/// because every operation kind it lowers into has a closed payload schema whose fixed allow-list
/// admits no baseline-condition property, pinned here by
/// `LoweredCreateLexemeForm_CannotCarryAConditionalProperty_TheClosedPayloadRejectsIt`,
/// `LoweredSetIsAbstract_CannotCarryAConditionalProperty_TheClosedPayloadRejectsIt`, and
/// `LoweredSetGloss_CannotCarryAConditionalProperty_TheClosedPayloadRejectsIt` below. Each takes a
/// real operation <see cref="AuthorLexemeFormComposer.Build"/> produced, adds one property shaped
/// like a baseline condition (<c>"onlyIf"</c>, <c>"expectedBefore"</c>, <c>"unless"</c>), and
/// re-parses it through the exact <c>*Payload.Parse</c> method
/// <see cref="SIL.Motif.Runner.DryRun.ProposalDryRunner"/> and
/// <see cref="SIL.Motif.Runner.Apply.ProposalApplier"/> call at dry-run and apply time. If a future
/// edit ever admitted a conditional field into one of these payloads, this test would start failing
/// the moment that field's name was also added to the payload's own allow-list.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class PreconditionsNeverRideOnALoweredOperationTests : IDisposable
{
    private static readonly string[] ConditionShapedPropertyNames =
    {
        "onlyIf", "unless", "expectedBefore", "ifBaselineEquals", "condition",
    };

    private readonly LcmCache _cache;
    private readonly SeededProject _seed;

    public PreconditionsNeverRideOnALoweredOperationTests(PristineProjectFixture pristine)
    {
        _cache = pristine.NewScratch();
        _seed = pristine.Seed;
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
    }

    [Fact]
    public void LoweredCreateLexemeForm_CannotCarryAConditionalProperty_TheClosedPayloadRejectsIt()
    {
        var create = BuildAllThreeOperations()[0];
        Assert.Equal(LexEntryLexemeFormOperationKinds.CreateLexemeForm, create.Kind);

        AssertEveryConditionShapedAdditionIsRejected(
            create.After!.Value, after => LexEntryLexemeFormCreatePayload.Parse(after));
    }

    [Fact]
    public void LoweredSetIsAbstract_CannotCarryAConditionalProperty_TheClosedPayloadRejectsIt()
    {
        var setIsAbstract = BuildAllThreeOperations()[1];
        Assert.Equal(MoFormIsAbstractOperationKinds.SetIsAbstract, setIsAbstract.Kind);

        AssertEveryConditionShapedAdditionIsRejected(
            setIsAbstract.After!.Value, after => MoFormIsAbstractSetPayload.Parse(after));
    }

    [Fact]
    public void LoweredSetGloss_CannotCarryAConditionalProperty_TheClosedPayloadRejectsIt()
    {
        var setGloss = BuildAllThreeOperations()[2];
        Assert.Equal(LexicalSenseOperationKinds.SetGloss, setGloss.Kind);

        AssertEveryConditionShapedAdditionIsRejected(
            setGloss.After!.Value, after => SetGlossPayload.Parse(after));
    }

    private IReadOnlyList<OperationEnvelope> BuildAllThreeOperations()
    {
        var intent = new AuthorLexemeFormIntent(
            CanonicalId.FromGuid(_seed.SecondEntryId),
            CanonicalId.FromGuid(MoMorphTypeTags.kguidMorphStem),
            "fr", "zzMotifNewForm",
            IsAbstract: true,
            Sense: CanonicalId.FromGuid(_seed.SecondSenseId), GlossWritingSystem: "en", GlossText: "new gloss");

        return AuthorLexemeFormComposer.Build(_cache, intent);
    }

    private static void AssertEveryConditionShapedAdditionIsRejected(
        JsonElement validAfter, Action<JsonElement> parse)
    {
        // The unmodified payload must parse cleanly first -- otherwise a rejection below would prove nothing.
        parse(validAfter);

        foreach (var propertyName in ConditionShapedPropertyNames)
        {
            var poisoned = WithExtraProperty(validAfter, propertyName);
            Assert.Throws<ContractParseException>(() => parse(poisoned));
        }
    }

    private static JsonElement WithExtraProperty(JsonElement original, string extraPropertyName)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in original.EnumerateObject())
                property.WriteTo(writer);
            writer.WriteString(extraPropertyName, "old-value");
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
