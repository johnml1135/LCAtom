using SIL.Motif.Generator;
using SIL.Motif.Generator.Descriptions.Harvest;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// The compiled-help pages are RoboHelp's two-column tables; these fixtures are trimmed copies of the real
/// shape, so the parser is exercised without needing the <c>.chm</c> or the Windows-only tool that opens it.
/// </summary>
public class FieldWorksHelpPageParserTests
{
    private const string RealShape = """
        <?xml version="1.0" encoding="windows-1252" ?>
        <!DOCTYPE html>
        <html><head><title>Abbreviation field (Feature Types)</title></head>
        <body>
        <h2>Abbreviation field</h2>
        <table width="100%"><tbody>
        <tr>
        <td style="width: 20%;"><p class="BodyText"><span class="Strong">Full name:</span></p></td>
        <td style="width: 80%;"><p class="BodyText"><span class="UserInterface">Abbreviation</span></p></td>
        </tr>
        <tr>
        <td style="width: 20%;"><p class="BodyText"><span class="Strong">Description:</span></p></td>
        <td style="width: 80%;">
        <p class="BodyText">This field stores the abbreviation of the current <a href="x.htm">named</a> feature type.</p>
        <p class="BodyText">Feature type abbreviations in the <a href="y.htm">Grammar Sketch</a>.</p>
        </td>
        </tr>
        </tbody></table>
        </body></html>
        """;

    [Fact]
    public void TheDescriptionRowsFirstParagraph_IsWhatIsHarvested()
    {
        var page = FieldWorksHelpPageParser.Parse("Feature_Types_fields/abbreviation.htm", RealShape);

        Assert.Equal("Abbreviation field (Feature Types)", page.Title);
        Assert.Equal("This field stores the abbreviation of the current named feature type.", page.Description);
    }

    /// <summary>
    /// Second and later paragraphs are application advice — where the abbreviation shows up in the Grammar
    /// Sketch, which dictionary option controls it — not a description of the field, and the same
    /// first-paragraph rule <see cref="LibLcmFieldComment"/> already applies to model comments.
    /// </summary>
    [Fact]
    public void LaterParagraphsOfTheDescriptionRow_AreNotHarvested()
    {
        var page = FieldWorksHelpPageParser.Parse("x.htm", RealShape);

        Assert.DoesNotContain("Grammar Sketch", page.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// These paragraphs wrap individual words in <c>&lt;a&gt;</c>/<c>&lt;span&gt;</c>, so a tag that became a
    /// space would repunctuate the sentence: "the headword ( lexeme or citation form) was derived".
    /// </summary>
    [Fact]
    public void InlineLinksAndSpans_DoNotLeaveSpacesAroundPunctuation()
    {
        const string html = """
            <html><head><title>Source Form (Etymology)</title></head><body><table><tbody><tr>
            <td><p><span class="Strong">Description:</span></p></td>
            <td><p>The form from which the headword (<a href="a.htm">lexeme</a> or <a href="b.htm">citation form</a>) was derived.</p></td>
            </tr></tbody></table></body></html>
            """;

        var page = FieldWorksHelpPageParser.Parse("Source_Form_Etymology.htm", html);

        Assert.Equal(
            "The form from which the headword (lexeme or citation form) was derived.", page.Description);
    }

    [Fact]
    public void EntitiesAndNonBreakingSpaces_AreDecodedAndNormalised()
    {
        const string html = """
            <html><head><title>T</title></head><body><table><tbody><tr>
            <td><p><span class="Strong">Description:</span></p></td>
            <td><p>Stores&nbsp;the abbreviation, such as &quot;II&quot; for
            &quot;2nd&nbsp;declension.&quot;</p></td>
            </tr></tbody></table></body></html>
            """;

        var page = FieldWorksHelpPageParser.Parse("x.htm", html);

        Assert.Equal("Stores the abbreviation, such as \"II\" for \"2nd declension.\"", page.Description);
    }

    /// <summary>
    /// The failure mode this whole pipeline exists to prevent is a description that was written rather than
    /// found. A page with no <c>Description:</c> row must therefore stop the harvest, not yield something.
    /// </summary>
    [Fact]
    public void APageWithNoDescriptionRow_ThrowsRatherThanReturningSomethingElse()
    {
        const string html = "<html><head><title>Overview</title></head><body><p>Some overview prose.</p></body></html>";

        var ex = Assert.Throws<GeneratorException>(() => FieldWorksHelpPageParser.Parse("overview.htm", html));
        Assert.Contains("overview.htm", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no 'Description:' row", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyDescriptionRow_Throws()
    {
        const string html = """
            <html><head><title>T</title></head><body><table><tbody><tr>
            <td><p><span class="Strong">Description:</span></p></td>
            <td><p>   </p></td>
            </tr></tbody></table></body></html>
            """;

        Assert.Throws<GeneratorException>(() => FieldWorksHelpPageParser.Parse("x.htm", html));
    }
}
