using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Runner.Operations;
using SIL.LCModel;

namespace SIL.Motif.Runner.Composers;

/// <summary>
/// Lowers one <see cref="AuthorLexemeFormIntent"/> into the ordinary, closed-schema Layer-0
/// operations that realize it — Motif's first Layer-1 composer (ADR 0009 decision 1). One authored
/// construct becomes up to three operations: <c>lexical/lexEntry/createLexemeForm</c>, then
/// (only when <see cref="AuthorLexemeFormIntent.IsAbstract"/> is true)
/// <c>grammar/moForm/setIsAbstract</c> targeting the form the first operation creates, then (only
/// when <see cref="AuthorLexemeFormIntent.Sense"/> is supplied) <c>lexical/lexSense/setGloss</c>
/// targeting that pre-existing sense.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composer, not primitive.</b> <see cref="Build"/> takes <c>(cache, intent)</c> because it must
/// resolve every reference the intent names and fail closed before authoring anything — ADR 0009
/// decision 1's "composers are project-reading; the primitive builder is pure." The returned
/// operations add zero permanent contract surface: dispatch, validation, and lowering for all three
/// kinds already exist and are exercised directly, not reimplemented here.
/// </para>
/// <para>
/// <b>Ordering is declared, not positional.</b> The <c>setIsAbstract</c> operation carries an
/// explicit <see cref="OperationDependency"/> on the create operation's id, because its target is
/// the entity the create operation's <c>entityId</c> proposes — that target cannot resolve until
/// the create has run, so the dependency is structural, not stylistic. The <c>setGloss</c> operation
/// carries no dependency at all: it targets a sense that already exists, so nothing in this Proposal
/// need run before it. A composer that wired a dependency there anyway would be declaring an
/// ordering constraint that is not true.
/// </para>
/// <para>
/// <b>No operation this method builds can carry a condition.</b> Every payload below is built
/// through the same closed-schema <c>*Payload</c> type its own operation handler parses at dry-run
/// and apply time (<see cref="LexEntryLexemeFormCreatePayload"/>, <see cref="MoFormIsAbstractSetPayload"/>,
/// <see cref="SetGlossPayload"/>), each of which has a fixed property allow-list a future conditional
/// field could not join unnoticed — pinned by
/// `LoweredCreateLexemeForm_CannotCarryAConditionalProperty_TheClosedPayloadRejectsIt`,
/// `LoweredSetIsAbstract_CannotCarryAConditionalProperty_TheClosedPayloadRejectsIt`, and
/// `LoweredSetGloss_CannotCarryAConditionalProperty_TheClosedPayloadRejectsIt`.
/// </para>
/// </remarks>
public static class AuthorLexemeFormComposer
{
    private const string ConstructName = "AuthorLexemeForm";

    /// <param name="cache">The project to resolve <paramref name="intent"/>'s references against.</param>
    /// <param name="intent">The authored construct.</param>
    /// <param name="mintId">
    /// Overrides id minting; defaults to <see cref="CanonicalId.Mint(string)"/>. Tests supply a
    /// deterministic source to pin the composer's lowering as reproducible.
    /// </param>
    public static IReadOnlyList<OperationEnvelope> Build(
        LcmCache cache, AuthorLexemeFormIntent intent, Func<CanonicalId>? mintId = null)
    {
        if (cache is null) throw new ArgumentNullException(nameof(cache));
        if (intent is null) throw new ArgumentNullException(nameof(intent));
        RequireConsistentGloss(intent);

        var mint = mintId ?? (() => CanonicalId.Mint());

        var entry = ReferenceFieldLowering.Resolve<ILexEntry>(cache, intent.Entry, ConstructName);
        ReferenceFieldLowering.Resolve<IMoMorphType>(cache, intent.MorphType, ConstructName);

        ILexSense? sense = null;
        if (intent.Sense is { } senseId)
        {
            sense = ReferenceFieldLowering.Resolve<ILexSense>(cache, senseId, ConstructName);
            if (!entry.SensesOS.Any(candidate => candidate.Guid == sense.Guid))
            {
                throw new InvalidOperationException(
                    $"'{ConstructName}': sense '{senseId.Value}' does not belong to entry '{intent.Entry.Value}'.");
            }
        }

        var operations = new List<OperationEnvelope>();

        var createOpId = mint();
        var newFormId = mint();
        operations.Add(new OperationEnvelope(
            operationId: createOpId,
            kind: LexEntryLexemeFormOperationKinds.CreateLexemeForm,
            entityId: newFormId,
            target: intent.Entry,
            after: BuildCreateLexemeFormAfter(intent.MorphType, intent.LexemeFormWritingSystem, intent.LexemeFormText),
            rationale: Rationale));

        if (intent.IsAbstract)
        {
            operations.Add(new OperationEnvelope(
                operationId: mint(),
                kind: MoFormIsAbstractOperationKinds.SetIsAbstract,
                target: newFormId,
                after: BuildSetIsAbstractAfter(),
                dependsOn: new[] { new OperationDependency(createOpId) },
                rationale: Rationale));
        }

        if (sense is not null)
        {
            operations.Add(new OperationEnvelope(
                operationId: mint(),
                kind: LexicalSenseOperationKinds.SetGloss,
                target: intent.Sense!.Value,
                after: BuildSetGlossAfter(intent.GlossWritingSystem!, intent.GlossText!),
                rationale: Rationale));
        }

        return operations;
    }

    private const string Rationale = "Authored by the AuthorLexemeForm composer.";

    // Sense/GlossWritingSystem/GlossText must be all-set or all-null; a direct build bypasses the parser.
    private static void RequireConsistentGloss(AuthorLexemeFormIntent intent)
    {
        var allSet = intent.Sense is not null && intent.GlossWritingSystem is not null && intent.GlossText is not null;
        var allNull = intent.Sense is null && intent.GlossWritingSystem is null && intent.GlossText is null;
        if (allSet || allNull)
            return;

        throw new InvalidOperationException(
            $"'{ConstructName}': 'Sense', 'GlossWritingSystem', and 'GlossText' must all be set or all be null.");
    }

    private static JsonElement BuildCreateLexemeFormAfter(CanonicalId morphType, string ws, string text)
    {
        var json = JsonSerializer.Serialize(new { morphType = morphType.Value, ws, text });
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement BuildSetIsAbstractAfter()
    {
        using var document = JsonDocument.Parse("{\"value\":true}");
        return document.RootElement.Clone();
    }

    private static JsonElement BuildSetGlossAfter(string ws, string text)
    {
        var json = JsonSerializer.Serialize(new { ws, text });
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
