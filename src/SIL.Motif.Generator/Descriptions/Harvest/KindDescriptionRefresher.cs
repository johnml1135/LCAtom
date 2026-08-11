namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// Stage 1 of a two-stage pipeline. Stage 2 is the pipeline that already
/// existed: <see cref="KindDescriptionTsvParser"/> reads whatever this stage last wrote, and
/// <see cref="Checks.DescriptionCheck"/> gates emission on it. This stage's only job is to attach provenance —
/// it never invents prose. For every (Class, Field) already in <c>manifest/kind-descriptions.tsv</c>:
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>If it is one of the five hand-corrected <see cref="HandCorrectedFields.ProdRestrictFamily"/> rows,
/// its <c>Label</c>/<c>Description</c> are preserved byte-for-byte — regeneration must never silently replace
/// a human's fix for a polarity bug with a fresh mechanical paraphrase of the same source. A citation
/// and a <see cref="SourceDigest"/> are still attached when the source is available, because the row *is*
/// sourced; it was just corrected by a human against that source rather than transcribed from it. The digest
/// is what makes the correction outlive the source safely: if the comment it was corrected against is ever
/// reworded, the digest changes and the row is reported, even though our text is untouched.</item>
/// <item>Otherwise, if the field has an upstream source of its own — a substantive <c>MasterLCModel.xml</c>
/// comment, a mapped <c>ContextHelp.xml</c> item, or a harvested compiled-help page, in that order — the
/// description becomes that text, cited and digested. The order is deliberate: the model's own comment first,
/// then the balloon help the application shows <em>in</em> the field, then the help topic <em>about</em> it.</item>
/// <item>Otherwise, if <see cref="DescriptionAdaptations"/> has a rule for the field, the description is
/// derived from a sibling field's source text by that rule's substitution, and marked <c>adapted</c>.</item>
/// <item>Otherwise, if the field is in <see cref="DescriptionExemptions"/>, the row keeps its text and is
/// marked <c>no-source-exists</c>, citing the search rather than a source — the one honest outcome for a
/// field that has been looked for exhaustively and found nowhere.</item>
/// <item>Otherwise the row is <c>unsourced</c>: its existing <c>Description</c> text is kept (an emitted kind
/// must always have <em>some</em> usable text — <see cref="Checks.DescriptionCheck"/> requires it — and this
/// stage does not write new prose), but <c>Source</c>/<c>SourceDetail</c>/<c>SourceHash</c> are cleared so
/// nothing claims a citation it does not have.</item>
/// </list>
/// <para>
/// A row whose (Class, Field) this refresher has never seen before — e.g. one appended by someone else's
/// change while this ran — falls through to the <c>unsourced</c> branch automatically, which is exactly
/// "preserve it, don't invent a description for it."
/// </para>
/// </remarks>
public static class KindDescriptionRefresher
{
    public const string LibLcmSourceName = "liblcm/MasterLCModel.xml";
    public const string FieldWorksSourceName = "FieldWorks/DistFiles/Language Explorer/Configuration/ContextHelp.xml";
    public const string FieldWorksHelpSourceName = FieldWorksHelpHarvester.SourceName;

    /// <summary>One field's upstream text, wherever it was found.</summary>
    private sealed record ResolvedSource(string Text, string Source, string Citation)
    {
        public string Hash => SourceDigest.OfText(Text);
    }

    public static KindDescriptionRefreshResult Refresh(
        IReadOnlyList<KindDescription> existingRows,
        IReadOnlyDictionary<(string Class, string Field), LibLcmFieldComment> libLcmComments,
        IReadOnlyDictionary<string, ContextHelpEntry> contextHelpEntries,
        IReadOnlyDictionary<(string Class, string Field), HarvestedHelpDescription>? helpPages = null)
    {
        helpPages ??= new Dictionary<(string, string), HarvestedHelpDescription>();

        var contextHelpByField = FieldWorksContextHelpFieldMap.Entries
            .ToDictionary(e => (e.Class, e.Field), e => e);

        ResolvedSource? Resolve((string Class, string Field) key)
        {
            if (libLcmComments.TryGetValue(key, out var comment) && !comment.IsPlaceholderOnly)
                return new ResolvedSource(comment.FirstParagraph, LibLcmSourceName, comment.Citation);

            if (contextHelpByField.TryGetValue(key, out var mapEntry) &&
                contextHelpEntries.TryGetValue(mapEntry.ContextHelpId, out var contextHelp))
            {
                return new ResolvedSource(
                    contextHelp.Text, FieldWorksSourceName,
                    $"{contextHelp.Citation} ({mapEntry.Confidence}; verified against {mapEntry.VerifiedAgainst})");
            }

            if (helpPages.TryGetValue(key, out var helpPage))
                return new ResolvedSource(helpPage.Description, FieldWorksHelpSourceName, helpPage.Citation);

            return null;
        }

        var refreshed = new List<KindDescription>(existingRows.Count);
        var handCorrected = new List<string>();
        var sourcedFromLibLcm = new List<string>();
        var sourcedFromFieldWorks = new List<string>();
        var sourcedFromHelp = new List<string>();
        var adapted = new List<string>();
        var exempt = new List<string>();
        var unsourced = new List<string>();
        var drifted = new List<DescriptionDrift>();

        foreach (var row in existingRows)
        {
            var key = (row.Class, row.Field);
            var resolved = Resolve(key);

            if (HandCorrectedFields.ProdRestrictFamily.Contains(key))
            {
                // Text preserved, provenance refreshed: the digest is over the source, so a reworded upstream comment is still reported even though our prose can't reveal it.
                refreshed.Add(resolved is null
                    ? row with { Reviewed = "hand-corrected" }
                    : Cite(row, row.Description, resolved, "hand-corrected", textIsTheSource: false, drifted));
                handCorrected.Add(row.Key);
                continue;
            }

            if (resolved is not null)
            {
                refreshed.Add(Cite(row, resolved.Text, resolved, "sourced", textIsTheSource: true, drifted));
                (resolved.Source switch
                {
                    LibLcmSourceName => sourcedFromLibLcm,
                    FieldWorksSourceName => sourcedFromFieldWorks,
                    _ => sourcedFromHelp,
                }).Add(row.Key);
                continue;
            }

            if (DescriptionAdaptations.ByField.TryGetValue(key, out var rule))
            {
                var sibling = Resolve((rule.SourceClass, rule.SourceField))
                    ?? throw new GeneratorException(
                        $"{rule.Key}: its description is adapted from {rule.SourceKey}, which no longer has " +
                        "an upstream source of its own. The adaptation has nothing to derive from — find " +
                        "out what happened to the sibling's source before regenerating.");

                var text = DescriptionAdaptations.Apply(rule, sibling.Text);
                var citation = DescriptionAdaptations.Citation(rule, sibling.Citation);
                refreshed.Add(Cite(
                    row, text, sibling with { Citation = citation }, DescriptionAdaptations.ReviewedValue,
                    textIsTheSource: false, drifted));
                adapted.Add(row.Key);
                continue;
            }

            if (DescriptionExemptions.ByField.TryGetValue(key, out var exemption))
            {
                refreshed.Add(row with
                {
                    Reviewed = DescriptionExemptions.ReviewedValue,
                    Source = DescriptionExemptions.SourceValue,
                    SourceDetail = exemption.Evidence,
                    SourceHash = "",
                });
                exempt.Add(row.Key);
                continue;
            }

            refreshed.Add(row with { Reviewed = "unsourced", Source = "", SourceDetail = "", SourceHash = "" });
            unsourced.Add(row.Key);
        }

        return new KindDescriptionRefreshResult(
            refreshed, handCorrected, sourcedFromLibLcm, sourcedFromFieldWorks, sourcedFromHelp, adapted,
            exempt, unsourced, drifted);
    }

    /// <summary>Writes one row's text and provenance, recording drift when the upstream fragment's digest has moved; <paramref name="textIsTheSource"/> is false for a hand-corrected or adapted row, where only the digest — not the wording — can reveal that the source moved.</summary>
    private static KindDescription Cite(
        KindDescription row,
        string text,
        ResolvedSource resolved,
        string reviewed,
        bool textIsTheSource,
        List<DescriptionDrift> drifted)
    {
        var hash = resolved.Hash;

        // A row with no recorded digest yet is not drifting, it is being measured for the first time.
        if (row.SourceHash.Length > 0 && row.SourceHash != hash)
        {
            drifted.Add(new DescriptionDrift(
                row.Key, resolved.Source, row.SourceHash, hash,
                PreviousText: textIsTheSource ? row.Description : null,
                CurrentSourceText: resolved.Text,
                OurTextDiffersFromSource: !textIsTheSource));
        }

        return row with
        {
            Description = text,
            Reviewed = reviewed,
            Source = resolved.Source,
            SourceDetail = resolved.Citation,
            SourceHash = hash,
        };
    }
}

/// <summary>
/// One description whose upstream fragment changed since it was last harvested — detected by digest, so it
/// is caught even on the rows whose own prose is deliberately preserved and therefore cannot reveal it.
/// </summary>
/// <param name="PreviousText">
/// The wording that was replaced, when the row's stored text was itself the source text; <c>null</c> for a
/// preserved-text row, where the previous upstream wording was never stored — only its digest was.
/// </param>
/// <param name="OurTextDiffersFromSource">
/// True for a <c>hand-corrected</c> row, whose text is a human's fix, and for an <c>adapted</c> row, whose
/// text is derived from a sibling. These are the ones worth a human's attention, and the ones a
/// text-comparison could never have flagged: their prose is <em>supposed</em> to differ from the source, so
/// only the digest can report that the source moved underneath it.
/// </param>
public sealed record DescriptionDrift(
    string Key,
    string Source,
    string PreviousHash,
    string CurrentHash,
    string? PreviousText,
    string CurrentSourceText,
    bool OurTextDiffersFromSource);

/// <param name="Rows">The regenerated rows, same order and count as the input.</param>
/// <param name="HandCorrected">Keys preserved verbatim (the ProdRestrict family).</param>
/// <param name="SourcedFromLibLcm">Keys whose description now comes from a MasterLCModel.xml comment.</param>
/// <param name="SourcedFromFieldWorks">Keys whose description now comes from FieldWorks' ContextHelp.xml.</param>
/// <param name="SourcedFromHelp">Keys whose description now comes from a FieldWorks help page.</param>
/// <param name="Adapted">Keys derived from a sibling field's source by a substitution rule.</param>
/// <param name="Exempt">Keys for which no source exists, with the search cited.</param>
/// <param name="Unsourced">Keys with no upstream source found and no exemption — still open.</param>
/// <param name="Drifted">Rows whose upstream fragment changed since the last harvest.</param>
public sealed record KindDescriptionRefreshResult(
    IReadOnlyList<KindDescription> Rows,
    IReadOnlyList<string> HandCorrected,
    IReadOnlyList<string> SourcedFromLibLcm,
    IReadOnlyList<string> SourcedFromFieldWorks,
    IReadOnlyList<string> SourcedFromHelp,
    IReadOnlyList<string> Adapted,
    IReadOnlyList<string> Exempt,
    IReadOnlyList<string> Unsourced,
    IReadOnlyList<DescriptionDrift> Drifted);
