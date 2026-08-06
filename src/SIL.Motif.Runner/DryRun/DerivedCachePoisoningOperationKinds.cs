using System;
using System.Collections.Generic;
using SIL.Motif.Runner.Operations;

namespace SIL.Motif.Runner.DryRun;

/// <summary>
/// The "may poison a derived cache" predicate for <see cref="ProposalDryRunner.Run"/>: does
/// running this operation kind's mutate-then-rollback sequence risk leaving <c>LexEntry</c>
/// headword/homograph or <c>MoStemAllomorph</c> monomorphemic derived caches stale, because
/// <c>UndoStack.Rollback</c> (unlike <c>Undo</c>/<c>Redo</c>) skips the hooks that refresh them
/// (docs/adr/0006-engine-reality-apply-readback-preflight.md, decision 3)?
/// </summary>
/// <remarks>
/// <para>
/// <b>Dormant today, by construction.</b> Stage C/D implement exactly one operation kind,
/// <see cref="LexicalSenseOperationKinds.SetGloss"/>, which sets a <c>LexSense.Gloss</c> MultiUnicode
/// alternative — a field none of those three caches derive from. This predicate therefore reports
/// <c>false</c> for everything Run can actually dispatch today; the guard exists so it is already
/// wired in the moment the first lexeme-form or citation-form operation kind ships (both feed
/// <c>LexEntry.HomographFormKey</c>, hence the headword and homograph indexes, and the monomorphemic
/// allomorph cache), rather than being bolted on reactively after a real staleness report.
/// </para>
/// <para>
/// <b>Hardcoded today; a seam, not the final shape.</b> The two kind strings below name operations
/// this codebase does not yet implement (no handler is registered for them, and
/// <see cref="ProposalDryRunner.Run"/>/<c>ProposalApplier.Apply</c> would reject them as
/// unsupported if actually dispatched) — they exist purely as forward-looking predicate entries. This
/// is deliberately a hand-maintained <see cref="HashSet{T}"/>, not derived from anything, so it can
/// drift out of sync with the real operation vocabulary as new kinds are added elsewhere.
/// </para>
/// <para>
/// TODO: once the operation-kind coverage manifest docs/operation-catalog-plan.md describes exists
/// (a generated/reviewed table of every kind and which LibLCM fields/derived caches it touches),
/// replace this hardcoded set with a lookup against that manifest's "touches a forward-only derived
/// cache" column, so a newly added kind can never silently ship without an explicit answer to this
/// question.
/// </para>
/// </remarks>
public static class DerivedCachePoisoningOperationKinds
{
    // Not implemented by any operation handler yet (Stage C/D support only setGloss) — named here so
    // the guard already exists before lexeme-form/citation-form authoring ships. See remarks above.
    //
    // The two entries below predate ADR 0023's derived-kind-name rule and use its superseded
    // "entry" construct segment rather than the derived "lexEntry" — "lexical/entry/setLexemeForm"
    // still names nothing dispatchable (LexemeForm is owning/atomic, out of MOT-4's set|clear
    // slice), matching ProposalDryRunnerTests' own literal string, so it is left as-is rather than
    // silently fixed underneath a pinned test. "lexical/entry/setCitationForm" is genuinely stale
    // now that citationForm ships (real name: lexical/lexEntry/setCitationForm, added below) —
    // left in place anyway rather than removed, since a dead entry here is harmless and this set is
    // documented as hand-maintained, not derived.
    //
    // MOT-4 additions, both with real, live handlers and manifest/liblcm-inventory.tsv's
    // AssessPoisonsCache=yes: LexEntry.CitationForm (OverridesLing_Lex.cs
    // ITsStringAltChangedSideEffectsInternal: a default-vernacular-WS edit calls UpdateHomographs +
    // MLHeadwordChanged) and MoForm.Form (OverridesLing_MoClasses.cs: a LexEntry-owned MoForm.Form
    // edit fires MLHeadwordChanged/MoFormFormChanged -> UpdateHomographs, and MoStemAllomorph.Form
    // additionally clears the monomorphemic-morph-data cache). Both verbs (set and clear) are
    // listed: clearing a MultiUnicode alternative is the same "alt changed" side effect as setting
    // one to a new value.
    private static readonly HashSet<string> PoisoningKinds = new(StringComparer.Ordinal)
    {
        "lexical/entry/setLexemeForm",
        "lexical/entry/setCitationForm",
        "lexical/lexEntry/setCitationForm",
        "lexical/lexEntry/clearCitationForm",
        "grammar/moForm/setForm",
        "grammar/moForm/clearForm",
    };

    /// <summary>
    /// <c>true</c> when Run running <paramref name="operationKind"/>'s mutate-then-rollback
    /// sequence may leave a forward-only derived cache stale after rollback.
    /// </summary>
    public static bool MayPoisonDerivedCache(string operationKind) =>
        PoisoningKinds.Contains(operationKind);
}
