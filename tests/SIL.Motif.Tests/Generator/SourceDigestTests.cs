using SIL.Motif.Generator.Descriptions;
using SIL.Motif.Generator.Descriptions.Harvest;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// The digest is what lets a preserved-text row report that its source moved, so its two properties matter:
/// it renders the way every other digest in this repository renders, and it is over the fragment's bytes and
/// nothing else — no line number, no surrounding element, nothing that moves when the file above it is
/// edited.
/// </summary>
public class SourceDigestTests
{
    [Fact]
    public void ADigest_IsRenderedAsSha256ColonSixtyFourLowercaseHex()
    {
        var digest = SourceDigest.OfText("The short meaning shown for this sense.");

        Assert.StartsWith("sha256:", digest, StringComparison.Ordinal);
        Assert.Equal(71, digest.Length);
        Assert.All(digest[7..], c => Assert.True(char.IsAsciiDigit(c) || (c is >= 'a' and <= 'f')));
    }

    [Fact]
    public void TheSameTextAlwaysDigestsTheSame_AndDifferentTextDoesNot()
    {
        const string text = "Allows the user to write phonological rules.";

        Assert.Equal(SourceDigest.OfText(text), SourceDigest.OfText(text));
        Assert.NotEqual(SourceDigest.OfText(text), SourceDigest.OfText(text + " "));
    }

    /// <summary>
    /// The property the whole scheme depends on, asserted through the refresher rather than through the
    /// digest alone: <b>a fragment that moved down its file digests identically.</b> The citation still
    /// names the new line — that is what a reviewer follows — but the line is not part of the digest, so
    /// editing anything above a field does not report that field as changed. Hashing the citation would have
    /// made every edit to <c>MasterLCModel.xml</c> look like an edit to all 66 rows below it.
    /// </summary>
    [Fact]
    public void AFragmentThatMovedDownItsFile_DigestsIdentically_AndIsNotReportedAsDrift()
    {
        const string sentence = "The short meaning shown for this sense.";
        var row = new KindDescription(
            "LexSense", "Gloss", "Gloss", sentence, "sourced", KindDescriptionRefresher.LibLcmSourceName,
            "line 42", SourceDigest.OfText(sentence));

        // Same text, 300 lines further down the file after something above it grew.
        var moved = new Dictionary<(string, string), LibLcmFieldComment>
        {
            [("LexSense", "Gloss")] = new("LexSense", "Gloss", "basic", sentence, 1, 342),
        };

        var result = KindDescriptionRefresher.Refresh([row], moved, new Dictionary<string, ContextHelpEntry>());

        Assert.Empty(result.Drifted);
        Assert.Contains("line 342", Assert.Single(result.Rows).SourceDetail, StringComparison.Ordinal);
        Assert.Equal(row.SourceHash, result.Rows[0].SourceHash);
    }

    [Fact]
    public void AFileDigest_MatchesTheDigestOfItsBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"motif-digest-{Guid.NewGuid():N}.txt");
        const string content = "some source file content";

        try
        {
            File.WriteAllText(path, content);

            // Written as UTF-8 with no BOM, so the file's bytes are the string's bytes.
            Assert.Equal(SourceDigest.OfText(content), SourceDigest.OfFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
