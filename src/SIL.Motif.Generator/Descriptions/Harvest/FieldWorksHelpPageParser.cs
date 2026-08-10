using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// Pulls the <c>Description:</c> sentence out of one FieldWorks help page. The pages are RoboHelp output
/// with a fixed two-column table shape — a label cell holding <c>Description:</c>, then a content cell of
/// <c>&lt;p&gt;</c> paragraphs — so this reads that shape directly rather than loading an XML DOM.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <c>XDocument</c>:</b> the pages open with an XML declaration <em>and</em> an HTML5 doctype,
/// carry unescaped <c>&amp;nbsp;</c>, and are declared <c>windows-1252</c>. A strict XML reader rejects
/// them; a lenient hand-rolled read of one known element shape does not, and this is dev-time harvesting
/// whose output is reviewed as a checked-in TSV before anything downstream sees it.
/// </para>
/// <para>
/// <b>It fails rather than guesses.</b> A page with no <c>Description:</c> row throws — the whole point of
/// D8 is that a missing source must look like a missing source, not like a sentence somebody wrote.
/// </para>
/// </remarks>
public static class FieldWorksHelpPageParser
{
    private static readonly Regex TitlePattern = new(
        @"<title>(?<title>.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>
    /// The label cell, then the content cell's first paragraph. <c>[\s\S]*?</c> rather than
    /// <c>RegexOptions.Singleline</c> on the whole pattern so the laziness is visible where it matters.
    /// </summary>
    private static readonly Regex DescriptionPattern = new(
        @"Description:[\s\S]{0,200}?</td>\s*<td[^>]*>\s*<p[^>]*>(?<body>[\s\S]*?)</p>",
        RegexOptions.IgnoreCase);

    private static readonly Regex TagPattern = new("<[^>]+>");
    private static readonly Regex WhitespacePattern = new(@"\s+");

    public static FieldWorksHelpPage Parse(string relativePath, string html)
    {
        var titleMatch = TitlePattern.Match(html);
        var title = titleMatch.Success ? CollapseToText(titleMatch.Groups["title"].Value) : "";

        var descriptionMatch = DescriptionPattern.Match(html);
        if (!descriptionMatch.Success)
        {
            throw new GeneratorException(
                $"FieldWorks help page '{relativePath}' has no 'Description:' row. Either the page is not a " +
                "field-description topic, or RoboHelp's output shape changed — check the page by hand rather " +
                "than relaxing this parser, because a description invented to fill the gap is the failure " +
                "docs/issues.md D8 exists to prevent.");
        }

        var description = CollapseToText(descriptionMatch.Groups["body"].Value);
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new GeneratorException(
                $"FieldWorks help page '{relativePath}' has an empty 'Description:' row.");
        }

        return new FieldWorksHelpPage(relativePath, title, description);
    }

    /// <summary>
    /// Reads a help page off disk as <c>windows-1252</c>, which is what the pages declare and what RoboHelp
    /// wrote them as. Decoding one of these as UTF-8 turns every curly quote and en dash into U+FFFD, and a
    /// replacement character copied into a manifest is a corruption nobody notices until a reviewer reads
    /// the row — so the encoding is pinned rather than guessed.
    /// </summary>
    public static FieldWorksHelpPage ParseFile(string relativePath, string fullPath) =>
        Parse(relativePath, Windows1252.Decode(File.ReadAllBytes(fullPath)));

    private static string CollapseToText(string markup)
    {
        // Tags come out with nothing in their place, not a space. These paragraphs wrap individual words in
        // <a>/<span> for cross-links and UI styling — "the headword (<a>lexeme</a> or <a>citation form</a>)"
        // — and substituting a space would punctuate the harvested sentence as "( lexeme ... form )".
        var text = TagPattern.Replace(markup, "");
        text = WebUtility.HtmlDecode(text);
        // A non-breaking space is still a space to a reader, and a manifest cell with U+00A0 in it silently
        // breaks a later grep for the same sentence.
        text = text.Replace('\u00a0', ' ');
        return WhitespacePattern.Replace(text, " ").Trim();
    }
}

/// <summary>
/// Just enough of <c>windows-1252</c> to read RoboHelp output, without taking a dependency on
/// <c>System.Text.Encoding.CodePages</c> for one file format. The encoding is Latin-1 except for the
/// <c>0x80</c>-<c>0x9F</c> block, which Latin-1 leaves as control characters and Microsoft filled with
/// punctuation - which is exactly the block these pages use, for their en dashes and curly quotes.
/// </summary>
internal static class Windows1252
{
    /// <summary>
    /// The <c>0x80</c>-<c>0x9F</c> block, in order. The five positions Microsoft left undefined
    /// (<c>0x81</c>, <c>0x8D</c>, <c>0x8F</c>, <c>0x90</c>, <c>0x9D</c>) map to U+FFFD here rather than to
    /// a plausible-looking character, so a page that somehow contains one shows up as damage in review
    /// instead of as text.
    /// </summary>
    private const string HighPunctuation =
        "\u20ac\ufffd\u201a\u0192\u201e\u2026\u2020\u2021" +
        "\u02c6\u2030\u0160\u2039\u0152\ufffd\u017d\ufffd" +
        "\ufffd\u2018\u2019\u201c\u201d\u2022\u2013\u2014" +
        "\u02dc\u2122\u0161\u203a\u0153\ufffd\u017e\u0178";

    public static string Decode(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
            builder.Append(b is >= 0x80 and <= 0x9f ? HighPunctuation[b - 0x80] : (char)b);
        return builder.ToString();
    }
}
