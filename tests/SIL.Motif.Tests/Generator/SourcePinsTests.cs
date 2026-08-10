using SIL.Motif.Generator;
using SIL.Motif.Generator.Descriptions.Harvest;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// The pin file answers one question — have the sources moved since the descriptions were copied out of
/// them? — and the answer has to be exact, because "moved" is what turns a silent upstream rewording into a
/// visible decision.
/// </summary>
public class SourcePinsTests
{
    private static SourceRelease Git(string source, string release, string commit, string when = "2026-08-10T00:00:00Z") =>
        new(source, SourceRelease.GitCheckoutKind, release, commit, when);

    [Fact]
    public void AnUnmovedSource_IsNotAMove_EvenWhenTheHarvestTimeDiffers()
    {
        var pinned = new[] { Git("FieldWorks", "build-1448-39-g41bf33b61", "41bf33b61188") };
        var current = new[] { Git("FieldWorks", "build-1448-39-g41bf33b61", "41bf33b61188", "2026-09-01T12:00:00Z") };

        Assert.Empty(SourcePins.Compare(pinned, current));
    }

    /// <summary>
    /// Both source repos sit some commits past their newest tag, so a bare <c>git describe</c> would report
    /// the same tag for two different states. The commit is compared as well for exactly that reason.
    /// </summary>
    [Fact]
    public void ANewCommitUnderTheSameTag_IsStillAMove()
    {
        var pinned = new[] { Git("FieldWorks", "build-1448-39-g41bf33b61", "41bf33b61188") };
        var current = new[] { Git("FieldWorks", "build-1448-39-g41bf33b61", "aaaaaaaaaaaa") };

        var move = Assert.Single(SourcePins.Compare(pinned, current));
        Assert.Equal("FieldWorks", move.Source);
        Assert.NotNull(move.PinnedRelease);
    }

    [Fact]
    public void ASourceWithNoPinYet_IsReportedAsAMoveWithNoPreviousRelease()
    {
        var move = Assert.Single(SourcePins.Compare([], [Git("liblcm", "v1", "abc")]));

        Assert.Null(move.PinnedRelease);
        Assert.Equal("liblcm", move.Source);
    }

    /// <summary>
    /// "Upgrade to the newest release" is only actionable if the message says which repo, from what, to
    /// what, and how to accept — so the wording is asserted rather than left to drift.
    /// </summary>
    [Fact]
    public void TheFailureMessage_NamesEachRepoBothReleasesAndTheWayToAccept()
    {
        var moves = SourcePins.Compare(
            [Git("FieldWorks", "build-1448-39-g41bf33b61", "41bf33b61188")],
            [Git("FieldWorks", "build-1449-2-gdeadbeef", "deadbeef0000")]);

        var message = SourcePins.DescribeMoves(moves, "manifest/source-pins.tsv");

        Assert.Contains("FieldWorks", message, StringComparison.Ordinal);
        Assert.Contains("build-1448-39-g41bf33b61", message, StringComparison.Ordinal);
        Assert.Contains("build-1449-2-gdeadbeef", message, StringComparison.Ordinal);
        Assert.Contains("--accept-source-move", message, StringComparison.Ordinal);
        Assert.Contains("manifest/source-pins.tsv", message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"motif-pins-{Guid.NewGuid():N}.tsv");
        var pins = new[]
        {
            new SourceRelease("liblcm", SourceRelease.NuGetPackageKind, "11.0.0-beta0150", "", "2026-08-10T00:00:00Z"),
            Git("FieldWorks", "build-1448-39-g41bf33b61", "41bf33b61188"),
        };

        try
        {
            SourcePins.Write(path, pins);
            var read = SourcePins.Read(path);

            // Written in Source order, not the order handed in, so a re-pin produces a stable diff.
            Assert.Equal(["FieldWorks", "liblcm"], read.Select(p => p.Source));
            Assert.Empty(SourcePins.Compare(read, pins));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingPinFile_ReadsAsNothingPinnedYet_RatherThanThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"motif-pins-absent-{Guid.NewGuid():N}.tsv");

        Assert.Empty(SourcePins.Read(path));
    }

    /// <summary>
    /// The pin file the repository actually ships. It has to name both sources, or the check it exists for
    /// silently stops covering one of them.
    /// </summary>
    [Fact]
    public void TheCheckedInPinFile_PinsBothDescriptionSources()
    {
        var pins = SourcePins.Read(RepoPaths.DefaultSourcePinsPath());

        Assert.Equal(["FieldWorks", "liblcm"], pins.Select(p => p.Source).OrderBy(s => s, StringComparer.Ordinal));
        Assert.All(pins, p => Assert.False(string.IsNullOrWhiteSpace(p.Release)));
    }
}
