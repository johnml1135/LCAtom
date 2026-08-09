using System.Security.Cryptography;
using System.Text;
using SIL.LCModel;
using SIL.LCModel.Core.WritingSystems;

namespace SIL.Motif.Host.WritingSystems;

/// <summary>One writing system, as far as the grammar is concerned.</summary>
/// <param name="Id">The language tag — the durable identity.</param>
/// <param name="Name">The display name, for a reader. Never used for identity.</param>
/// <param name="IsDefaultVernacular">
/// True for the <b>first</b> vernacular writing system, which is the one the parser actually uses. See
/// <see cref="WritingSystemInventory"/>'s remarks for why the word "first" is doing so much work.
/// </param>
/// <param name="ValidCharacterCount">
/// How many characters the writing system declares as valid, or <c>null</c> when it declares none. This is
/// not decoration: with FieldWorks' <c>AcceptUnspecifiedGraphemes</c> parser option on, this list becomes the
/// parser's phonological alphabet outright.
/// </param>
public sealed record GrammarWritingSystem(
    string Id,
    string Name,
    bool IsDefaultVernacular,
    int? ValidCharacterCount);

/// <summary>
/// The project's writing systems in the order that decides the grammar, with a digest so an Assessment can
/// record which arrangement it was computed under.
/// </summary>
/// <remarks>
/// <para>
/// <b>Writing systems are grammar inputs, not display configuration.</b> This was asserted the other way round
/// and checked against the FieldWorks source on 2026-08-09, where it failed:
/// </para>
/// <list type="bullet">
///   <item><description>A phoneme's grapheme is stored <b>per writing system</b> — <c>PhCode.Representation</c>
///   is <c>MultiUnicode</c> (<c>MasterLCModel.xml:4452</c>).</description></item>
///   <item><description><c>HCLoader</c> builds the entire character-definition table by reading those
///   representations through <c>VernacularDefaultWritingSystem</c> (<c>HCLoader.cs:1398, 2679-2680</c>), and
///   loads the writing system's Valid Characters list as the segment inventory when
///   <c>AcceptUnspecifiedGraphemes</c> is on (<c>HCLoader.cs:2718</c>).</description></item>
///   <item><description>A lexical entry whose form is empty in that writing system is <b>excluded from the
///   grammar altogether</b> (<c>HCLoader.cs:543, 585-586</c>).</description></item>
/// </list>
/// <para>
/// <b>And "default" means "first".</b> <c>DefaultVernacularWritingSystem</c> is literally
/// <c>m_currentVernacularWritingSystems.FirstOrDefault()</c> (liblcm <c>OverridesLangProj.cs:844-849</c>), over
/// the ordered <c>CurVernWss</c> list. So <b>reordering that list silently changes the phoneme inventory and
/// can drop entries out of the grammar</b> — a grammar change that looks like a settings change.
/// </para>
/// <para>
/// That is why this exists. Without it, Motif's change summary would report "grammar unchanged" after exactly
/// the edit most likely to invalidate an Assessment while looking innocuous. Recording
/// <see cref="Digest"/> alongside an Assessment makes the arrangement part of what that Assessment claims to
/// have been computed under.
/// </para>
/// <para>
/// <b>Read-only, deliberately.</b> Motif reports writing systems and never edits them — see
/// <see cref="EditingIsAFieldWorksJob"/>.
/// </para>
/// </remarks>
public sealed record WritingSystemInventory(
    IReadOnlyList<GrammarWritingSystem> Vernacular,
    IReadOnlyList<GrammarWritingSystem> Analysis,
    string Digest)
{
    /// <summary>
    /// Why there is no method here that changes anything, stated where someone looking for one will find it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>To change a project's writing systems, or their order, edit them in FieldWorks.</b> Motif does not
    /// offer it and should not.
    /// </para>
    /// <para>
    /// The reason is proportion rather than difficulty. Reordering the vernacular list changes the grammar,
    /// which is Motif's business — but it also changes which alternative every multilingual field displays
    /// first, across the dictionary, the texts and every view in the application. Handing Motif a lever over
    /// project-wide configuration in order to fix a grammar-reporting problem is the wrong-sized tool, and it
    /// crosses the boundary
    /// <c>docs/adr/0034-the-boundary-with-fieldworks-state-versus-change.md</c> draws: FieldWorks owns the
    /// project's current state, Motif owns what a change did to it.
    /// </para>
    /// <para>
    /// So a report may tell a reader that the vernacular order explains their result. Acting on that is a trip
    /// to FieldWorks, and the report should say so rather than leaving them looking for a command that is
    /// deliberately absent.
    /// </para>
    /// </remarks>
    public const string EditingIsAFieldWorksJob =
        "Writing systems are edited in FieldWorks, not in Motif. Their order decides which one the parser " +
        "uses, so changing it changes the grammar — but it also changes how every multilingual field in the " +
        "project displays, which is more than a grammar tool should reach.";

    /// <summary>The writing system the parser actually uses, or <c>null</c> if the project declares none.</summary>
    public GrammarWritingSystem? DefaultVernacular => Vernacular.FirstOrDefault();
}

/// <summary>Reads the writing systems that bear on a grammar out of a live project.</summary>
public static class WritingSystemInventoryReader
{
    public static WritingSystemInventory Read(LcmCache cache)
    {
        if (cache is null) throw new ArgumentNullException(nameof(cache));

        var container = cache.ServiceLocator.WritingSystems;

        // CurrentVernacularWritingSystems is an ordered IList and the order is the whole point — the first
        // entry is the one the parser reads. Enumerated as-is; sorting it here would destroy the fact this
        // type exists to record.
        var vernacular = container.CurrentVernacularWritingSystems
            .Select((ws, index) => Describe(ws, isDefaultVernacular: index == 0))
            .ToList();

        var analysis = container.CurrentAnalysisWritingSystems
            .Select(ws => Describe(ws, isDefaultVernacular: false))
            .ToList();

        return new WritingSystemInventory(vernacular, analysis, ComputeDigest(vernacular));
    }

    private static GrammarWritingSystem Describe(CoreWritingSystemDefinition ws, bool isDefaultVernacular)
    {
        int? validCharacterCount = null;
        try
        {
            var valid = ValidCharacters.Load(ws);
            var count = valid?.AllCharacters?.Count() ?? 0;
            validCharacterCount = count > 0 ? count : null;
        }
        catch
        {
            // A writing system with no valid-character list, or an unparseable one, is ordinary rather than
            // exceptional — report "none declared" instead of failing a whole inventory over one entry.
            validCharacterCount = null;
        }

        return new GrammarWritingSystem(
            Id: ws.Id ?? ws.LanguageTag ?? string.Empty,
            Name: ws.DisplayLabel ?? string.Empty,
            IsDefaultVernacular: isDefaultVernacular,
            ValidCharacterCount: validCharacterCount);
    }

    /// <summary>
    /// Digests the <b>ordered</b> vernacular tags, and nothing else.
    /// </summary>
    /// <remarks>
    /// Order is included because it decides which writing system the parser uses. The analysis writing systems
    /// are excluded because they carry glosses and labels — <c>HCLoader</c> reads them for naming only, so they
    /// cannot change what the parser matches. Valid-character counts are excluded too: they are reported for a
    /// reader but a count is not an identity, and hashing one would make this digest move for a reason it
    /// cannot explain.
    /// </remarks>
    private static string ComputeDigest(IReadOnlyList<GrammarWritingSystem> vernacular)
    {
        var joined = string.Join("\n", vernacular.Select(ws => ws.Id));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
