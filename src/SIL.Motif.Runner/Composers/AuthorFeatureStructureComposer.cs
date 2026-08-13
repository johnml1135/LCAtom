using System;
using System.Collections.Generic;
using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Runner.Operations;
using SIL.LCModel;

namespace SIL.Motif.Runner.Composers;

/// <summary>
/// Lowers one <see cref="AuthorFeatureStructureIntent"/> into the single, closed-schema Layer-0
/// operation that realizes it — Motif's first grammar composer (ADR 0009 decision 1), alongside the
/// lexical <see cref="AuthorLexemeFormComposer"/>. One authored construct becomes exactly one
/// <c>grammar/moStemMsa/createMsFeatures</c> operation.
/// </summary>
/// <remarks>
/// <b>Composer, not primitive.</b> <see cref="Build"/> takes <c>(cache, intent)</c> because it must
/// resolve the intent's reference and refuse an already-occupied slot before authoring anything — the
/// same "project-reading composer, pure primitive builder" split <see cref="AuthorLexemeFormComposer"/>
/// follows. Refusing here, rather than letting a create-into-occupied slot silently replace the
/// existing feature structure, is deliberate: unlike <c>LexemeForm</c> (a single required value with no
/// prior meaning worth preserving), an existing <c>FsFeatStruc</c> may already carry authored feature
/// values that a silent replacement would destroy.
/// </remarks>
public static class AuthorFeatureStructureComposer
{
    private const string ConstructName = "AuthorFeatureStructure";

    /// <param name="cache">The project to resolve <paramref name="intent"/>'s reference against.</param>
    /// <param name="intent">The authored construct.</param>
    /// <param name="mintId">
    /// Overrides id minting; defaults to <see cref="CanonicalId.Mint(string)"/>. Tests supply a
    /// deterministic source to pin the composer's lowering as reproducible.
    /// </param>
    public static IReadOnlyList<OperationEnvelope> Build(
        LcmCache cache, AuthorFeatureStructureIntent intent, Func<CanonicalId>? mintId = null)
    {
        if (cache is null) throw new ArgumentNullException(nameof(cache));
        if (intent is null) throw new ArgumentNullException(nameof(intent));

        var mint = mintId ?? (() => CanonicalId.Mint());

        var msa = ReferenceFieldLowering.Resolve<IMoStemMsa>(cache, intent.Msa, ConstructName);
        if (msa.MsFeaturesOA is not null)
        {
            throw new InvalidOperationException(
                $"'{ConstructName}': MSA '{intent.Msa.Value}' already has a feature structure; " +
                "this construct authors one, it does not replace one.");
        }

        var operationId = mint();
        var newFeatStrucId = mint();

        return new[]
        {
            new OperationEnvelope(
                operationId: operationId,
                kind: MoStemMsaMsFeaturesOperationKinds.CreateMsFeatures,
                entityId: newFeatStrucId,
                target: intent.Msa,
                after: EmptyAfter(),
                rationale: Rationale),
        };
    }

    private const string Rationale = "Authored by the AuthorFeatureStructure composer.";

    private static JsonElement EmptyAfter()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
