namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// Stage 1 of the two-stage pipeline docs/issues.md D8 asks for. Stage 2 is the pipeline that already
/// existed: <see cref="KindDescriptionTsvParser"/> reads whatever this stage last wrote, and
/// <see cref="Checks.DescriptionCheck"/> gates emission on it. This stage's only job is to attach provenance —
/// it never invents prose. For every (Class, Field) already in <c>manifest/kind-descriptions.tsv</c>:
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>If it is one of the five hand-corrected <see cref="HandCorrectedFields.ProdRestrictFamily"/> rows,
/// its <c>Label</c>/<c>Description</c> are preserved byte-for-byte — regeneration must never silently replace
/// a human's fix for the D8 polarity bug with a fresh mechanical paraphrase of the same source. A citation is
/// still attached when the source is available, because the row *is* sourced; it was just corrected by a
/// human against that source rather than transcribed from it automatically.</item>
/// <item>Otherwise, if <c>MasterLCModel.xml</c> has a substantive comment on that field
/// (<see cref="LibLcmFieldComment.IsPlaceholderOnly"/> is false), the description becomes that comment's first
/// paragraph, cited.</item>
/// <item>Otherwise, if <see cref="FieldWorksContextHelpFieldMap"/> maps this field to a
/// <c>ContextHelp.xml</c> id that is actually present, the description becomes that entry's text, cited.</item>
/// <item>Otherwise, if <c>manifest/fieldworks-help-descriptions.tsv</c> carries a page harvested from
/// FieldWorks' compiled help for this field, the description becomes that page's <c>Description:</c> row,
/// cited. Later than <c>ContextHelp.xml</c> because balloon help is what the application shows *in the
/// field itself*, where a help page is a topic about it.</item>
/// <item>Otherwise, if the field is in <see cref="DescriptionExemptions"/>, the row keeps its text and is
/// marked <c>no-source-exists</c>, citing the search rather than a source — the one honest outcome for a
/// field that has been looked for exhaustively and found nowhere.</item>
/// <item>Otherwise the row is <c>unsourced</c>: its existing <c>Description</c> text is kept (an emitted kind
/// must always have <em>some</em> usable text — <see cref="Checks.DescriptionCheck"/> requires it — and this
/// stage does not write new prose), but <c>Source</c>/<c>SourceDetail</c> are cleared so nothing claims a
/// citation it does not have.</item>
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

    public static KindDescriptionRefreshResult Refresh(
        IReadOnlyList<KindDescription> existingRows,
        IReadOnlyDictionary<(string Class, string Field), LibLcmFieldComment> libLcmComments,
        IReadOnlyDictionary<string, ContextHelpEntry> contextHelpEntries,
        IReadOnlyDictionary<(string Class, string Field), HarvestedHelpDescription>? helpPages = null)
    {
        helpPages ??= new Dictionary<(string, string), HarvestedHelpDescription>();

        var contextHelpByField = FieldWorksContextHelpFieldMap.Entries
            .ToDictionary(e => (e.Class, e.Field), e => e);

        var refreshed = new List<KindDescription>(existingRows.Count);
        var handCorrected = new List<string>();
        var sourcedFromLibLcm = new List<string>();
        var sourcedFromFieldWorks = new List<string>();
        var sourcedFromHelp = new List<string>();
        var exempt = new List<string>();
        var unsourced = new List<string>();
        var drifted = new List<DescriptionDrift>();

        foreach (var row in existingRows)
        {
            var key = (row.Class, row.Field);

            if (HandCorrectedFields.ProdRestrictFamily.Contains(key))
            {
                var preserved = libLcmComments.TryGetValue(key, out var handCitation)
                    ? row with { Reviewed = "hand-corrected", Source = LibLcmSourceName, SourceDetail = handCitation.Citation }
                    : row with { Reviewed = "hand-corrected" };
                refreshed.Add(preserved);
                handCorrected.Add(row.Key);
                continue;
            }

            if (libLcmComments.TryGetValue(key, out var comment) && !comment.IsPlaceholderOnly)
            {
                refreshed.Add(Replace(row, comment.FirstParagraph, LibLcmSourceName, comment.Citation, drifted));
                sourcedFromLibLcm.Add(row.Key);
                continue;
            }

            if (contextHelpByField.TryGetValue(key, out var mapEntry) &&
                contextHelpEntries.TryGetValue(mapEntry.ContextHelpId, out var contextHelp))
            {
                refreshed.Add(Replace(
                    row, contextHelp.Text, FieldWorksSourceName,
                    $"{contextHelp.Citation} ({mapEntry.Confidence}; verified against {mapEntry.VerifiedAgainst})",
                    drifted));
                sourcedFromFieldWorks.Add(row.Key);
                continue;
            }

            if (helpPages.TryGetValue(key, out var helpPage))
            {
                refreshed.Add(Replace(row, helpPage.Description, FieldWorksHelpSourceName, helpPage.Citation, drifted));
                sourcedFromHelp.Add(row.Key);
                continue;
            }

            if (DescriptionExemptions.ByField.TryGetValue(key, out var exemption))
            {
                refreshed.Add(row with
                {
                    Reviewed = DescriptionExemptions.ReviewedValue,
                    Source = DescriptionExemptions.SourceValue,
                    SourceDetail = exemption.Evidence,
                });
                exempt.Add(row.Key);
                continue;
            }

            refreshed.Add(row with { Reviewed = "unsourced", Source = "", SourceDetail = "" });
            unsourced.Add(row.Key);
        }

        return new KindDescriptionRefreshResult(
            refreshed, handCorrected, sourcedFromLibLcm, sourcedFromFieldWorks, sourcedFromHelp, exempt,
            unsourced, drifted);
    }

    /// <summary>
    /// Replaces a row's text with the upstream sentence, recording it as drift when the two differ. Drift is
    /// the whole reason the sources are pinned: an upstream rewording is not a bug, but it silently replaces
    /// a sentence a reviewer already read, so the run has to say so out loud rather than just write the file.
    /// </summary>
    private static KindDescription Replace(
        KindDescription row, string text, string source, string citation, List<DescriptionDrift> drifted)
    {
        // Only a row that already claimed this provenance can drift. A row moving from `unsourced` (an
        // unverified draft) or `no-source-exists` to a real citation is a source being found, not a
        // sentence changing under anyone.
        if (row.Reviewed == "sourced" && !string.Equals(row.Description, text, StringComparison.Ordinal))
            drifted.Add(new DescriptionDrift(row.Key, source, row.Description, text));

        return row with { Description = text, Reviewed = "sourced", Source = source, SourceDetail = citation };
    }
}

/// <summary>One description whose upstream text changed since it was last harvested.</summary>
/// <param name="PreviousText">What the checked-in manifest said.</param>
/// <param name="CurrentText">What the source says now.</param>
public sealed record DescriptionDrift(string Key, string Source, string PreviousText, string CurrentText);

/// <param name="Rows">The regenerated rows, same order and count as the input.</param>
/// <param name="HandCorrected">Keys preserved verbatim (the ProdRestrict family).</param>
/// <param name="SourcedFromLibLcm">Keys whose description now comes from a MasterLCModel.xml comment.</param>
/// <param name="SourcedFromFieldWorks">Keys whose description now comes from FieldWorks' ContextHelp.xml.</param>
/// <param name="SourcedFromHelp">Keys whose description now comes from a FieldWorks help page.</param>
/// <param name="Exempt">Keys for which no source exists, with the search cited.</param>
/// <param name="Unsourced">Keys with no upstream source found and no exemption — still open.</param>
/// <param name="Drifted">Rows whose upstream text changed since the last harvest.</param>
public sealed record KindDescriptionRefreshResult(
    IReadOnlyList<KindDescription> Rows,
    IReadOnlyList<string> HandCorrected,
    IReadOnlyList<string> SourcedFromLibLcm,
    IReadOnlyList<string> SourcedFromFieldWorks,
    IReadOnlyList<string> SourcedFromHelp,
    IReadOnlyList<string> Exempt,
    IReadOnlyList<string> Unsourced,
    IReadOnlyList<DescriptionDrift> Drifted);
