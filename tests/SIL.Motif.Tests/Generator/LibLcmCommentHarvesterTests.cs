using SIL.Motif.Generator;
using SIL.Motif.Generator.Descriptions.Harvest;
using SIL.Motif.Generator.ModelSource;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// docs/issues.md D8: <c>MasterLCModel.xml</c> carries substantive prose the generator never looked at
/// before. These tests exercise <see cref="LibLcmCommentHarvester"/> against small synthetic documents so the
/// edge cases (no comment, a placeholder-only comment, which class a same-named field belongs to) are pinned
/// without depending on the exact prose of the real 47,000-line file — <see cref="RealModel_*"/> tests below
/// cover that file directly, the same way <c>MasterLcModelParserTests</c> does.
/// </summary>
public class LibLcmCommentHarvesterTests
{
    private const string Xml = """
        <EntireModel version="1">
          <CellarModule>
            <class num="1" id="Alpha" abstract="false" base="CmObject" depth="0">
              <props>
                <basic num="1" id="WithComment" sig="Unicode">
                  <comment>
                    <para>  This is the   substantive   first paragraph.  </para>
                    <para>Changed from OldName.</para>
                  </comment>
                </basic>
                <basic num="2" id="NoComment" sig="Unicode"/>
                <basic num="3" id="RenameNoteOnly" sig="Unicode">
                  <comment>
                    <para>Changed from RenameNoteOnly.</para>
                  </comment>
                </basic>
                <basic num="4" id="PlaceholderOnly" sig="Unicode">
                  <comment>
                    <para>Put something here.</para>
                  </comment>
                </basic>
                <rel num="5" id="Shared" card="atomic" sig="Beta">
                  <comment>
                    <para>Alpha's own version of Shared.</para>
                  </comment>
                </rel>
              </props>
            </class>
            <class num="2" id="Beta" abstract="false" base="CmObject" depth="0">
              <props>
                <rel num="1" id="Shared" card="atomic" sig="Alpha">
                  <comment>
                    <para>Beta's own version of Shared.</para>
                  </comment>
                </rel>
              </props>
            </class>
          </CellarModule>
        </EntireModel>
        """;

    private static IReadOnlyDictionary<(string Class, string Field), LibLcmFieldComment> Harvest() =>
        LibLcmCommentHarvester.HarvestText("test.xml", Xml);

    [Fact]
    public void ASubstantiveComment_ReturnsTheFirstParagraph_WhitespaceNormalized()
    {
        var comment = Harvest()[("Alpha", "WithComment")];

        Assert.Equal("This is the substantive first paragraph.", comment.FirstParagraph);
        Assert.Equal(2, comment.ParagraphCount);
        Assert.False(comment.IsPlaceholderOnly);
    }

    [Fact]
    public void ASelfClosingField_HasNoEntry()
    {
        Assert.False(Harvest().ContainsKey(("Alpha", "NoComment")));
    }

    [Fact]
    public void ACommentThatIsOnlyARenameNote_IsFoundButFlaggedPlaceholderOnly()
    {
        var comment = Harvest()[("Alpha", "RenameNoteOnly")];

        Assert.Equal("Changed from RenameNoteOnly.", comment.FirstParagraph);
        Assert.True(comment.IsPlaceholderOnly);
    }

    [Fact]
    public void ALiteralAuthorPlaceholder_IsFlaggedPlaceholderOnly()
    {
        Assert.True(Harvest()[("Alpha", "PlaceholderOnly")].IsPlaceholderOnly);
    }

    [Fact]
    public void ARenameNoteAfterASubstantiveFirstParagraph_IsNotPlaceholderOnly()
    {
        // MoStemMsa.ProdRestrict's real shape: paragraph 1 is the -ity/[+Latinate] explanation, paragraph 2
        // is "Changed from ProductivityRestrictions." Only a *lone* rename note should be flagged.
        Assert.False(Harvest()[("Alpha", "WithComment")].IsPlaceholderOnly);
    }

    [Fact]
    public void TheSameFieldNameOnTwoClasses_ResolvesEachToItsOwnClassScopedComment()
    {
        var harvested = Harvest();

        Assert.Equal("Alpha's own version of Shared.", harvested[("Alpha", "Shared")].FirstParagraph);
        Assert.Equal("Beta's own version of Shared.", harvested[("Beta", "Shared")].FirstParagraph);
    }

    [Fact]
    public void TheCitation_NamesTheLineElementAndClass()
    {
        var comment = Harvest()[("Alpha", "WithComment")];

        Assert.Contains("<basic id=\"WithComment\">", comment.Citation);
        Assert.Contains("<class id=\"Alpha\">", comment.Citation);
        Assert.Contains("line ", comment.Citation);
        Assert.True(comment.LineNumber > 0);
    }

    [Fact]
    public void MalformedXml_ThrowsGeneratorException()
    {
        Assert.Throws<GeneratorException>(() => LibLcmCommentHarvester.HarvestText("bad.xml", "<not-closed>"));
    }

    // --- against the real, pinned file, mirroring MasterLcModelParserTests' own convention ---

    [Fact]
    public void RealModel_MoStemMsaProdRestrict_IsSubstantiveAndCitesTheLatinateExample()
    {
        var harvested = LibLcmCommentHarvester.Harvest(ModelPathResolver.Resolve().Path);
        var comment = harvested[("MoStemMsa", "ProdRestrict")];

        Assert.False(comment.IsPlaceholderOnly);
        Assert.Contains("Latinate", comment.FirstParagraph);
        Assert.Contains("felicity", comment.FirstParagraph);
    }

    [Fact]
    public void RealModel_MoDerivAffMsaFromProdRestrict_SaysTheStemMustBearTheClass()
    {
        // The exact polarity docs/issues.md D8 says the first agent-written batch got backwards: the stem
        // must bear the class, not "blocked by" it.
        var harvested = LibLcmCommentHarvester.Harvest(ModelPathResolver.Resolve().Path);
        var comment = harvested[("MoDerivAffMsa", "FromProdRestrict")];

        Assert.Contains("must bear", comment.FirstParagraph);
    }

    [Fact]
    public void RealModel_LexSenseMorphoSyntaxAnalysis_HasNoComment_ConfirmingItNeedsAnotherSource()
    {
        var harvested = LibLcmCommentHarvester.Harvest(ModelPathResolver.Resolve().Path);

        Assert.False(harvested.ContainsKey(("LexSense", "MorphoSyntaxAnalysis")));
    }
}
