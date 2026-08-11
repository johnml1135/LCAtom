using SIL.Motif.Generator;
using SIL.Motif.Generator.Descriptions.Harvest;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// The pin file answers two questions, and keeping them apart is the whole design: <b>has a file we read
/// changed?</b> stops a refresh, and <b>has a project moved?</b> is worth saying and nothing more. The first
/// version conflated them and refused to run over three unrelated test files added to FieldWorks.
/// </summary>
public class SourcePinsTests
{
    private const string HashA = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string HashB = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    private static SourceArtifact Fw(
        string release, string commit, string sha256, string artifact = "DistFiles/.../ContextHelp.xml") =>
        new("FieldWorks", SourceArtifact.GitCheckoutKind, release, commit, artifact, sha256, "2026-08-10T00:00:00Z");

    [Fact]
    public void AnUnmovedSource_IsNotAMove_EvenWhenTheHarvestTimeDiffers()
    {
        var pinned = new[] { Fw("build-1448-39-g41bf33b61", "41bf33b61188", HashA) };
        var current = new[]
        {
            Fw("build-1448-39-g41bf33b61", "41bf33b61188", HashA) with { HarvestedUtc = "2026-09-01T12:00:00Z" },
        };

        Assert.Empty(SourcePins.Compare(pinned, current));
    }

    /// <summary>
    /// The correction. A repository advancing over files we never read is reported so nobody wonders whether
    /// the check looked — but it is not a content change, so it does not stop the run.
    /// </summary>
    [Fact]
    public void AProjectThatMovedWithoutChangingTheFile_IsReportedButIsNotAContentChange()
    {
        var moves = SourcePins.Compare(
            [Fw("build-1448-39-g41bf33b61", "41bf33b61188", HashA)],
            [Fw("build-1448-40-gfd08d8588", "fd08d8588ee2", HashA)]);

        var move = Assert.Single(moves);
        Assert.False(move.ContentChanged);
        Assert.Empty(SourcePins.ContentChanges(moves));

        var note = SourcePins.DescribeReleaseOnlyMoves(moves);
        Assert.Contains("build-1448-39-g41bf33b61", note, StringComparison.Ordinal);
        Assert.Contains("build-1448-40-gfd08d8588", note, StringComparison.Ordinal);
        Assert.Contains("unchanged", note, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedFile_IsAContentChange_EvenWhenTheProjectDidNotMove()
    {
        // A dirty working tree, or a rebuilt artifact: same describe, different bytes. The bytes win.
        var moves = SourcePins.Compare(
            [Fw("build-1448-39-g41bf33b61", "41bf33b61188", HashA)],
            [Fw("build-1448-39-g41bf33b61", "41bf33b61188", HashB)]);

        Assert.True(Assert.Single(moves).ContentChanged);
        Assert.Single(SourcePins.ContentChanges(moves));
    }

    [Fact]
    public void AnArtifactWithNoPinYet_CountsAsAContentChange_SoTheFirstRunHasToBeAccepted()
    {
        var moves = SourcePins.Compare([], [Fw("v1", "abc", HashA)]);

        var move = Assert.Single(moves);
        Assert.Null(move.Pinned);
        Assert.True(move.ContentChanged);
    }

    /// <summary>
    /// Each file is pinned on its own, so a change to one does not implicate the other two — that is the
    /// difference between "the help file changed, re-run harvest-help" and "something in FieldWorks changed".
    /// </summary>
    [Fact]
    public void ArtifactsArePinnedIndividually_NotPerProject()
    {
        var pinned = new[] { Fw("r1", "c1", HashA, "a.xml"), Fw("r1", "c1", HashA, "b.chm") };
        var current = new[] { Fw("r1", "c1", HashA, "a.xml"), Fw("r1", "c1", HashB, "b.chm") };

        var change = Assert.Single(SourcePins.ContentChanges(SourcePins.Compare(pinned, current)));
        Assert.Equal("b.chm", change.Artifact);
    }

    /// <summary>
    /// "Upgrade to the newest release" is only actionable if the message says which file, at which two
    /// states, and how to accept — so the wording is asserted rather than left to drift.
    /// </summary>
    [Fact]
    public void TheFailureMessage_NamesTheFileBothDigestsAndTheWayToAccept()
    {
        var changes = SourcePins.ContentChanges(SourcePins.Compare(
            [Fw("build-1448-39-g41bf33b61", "41bf33b61188", HashA)],
            [Fw("build-1449-2-gdeadbeef", "deadbeef0000", HashB)]));

        var message = SourcePins.DescribeContentChanges(changes, "manifest/source-pins.tsv");

        Assert.Contains("ContextHelp.xml", message, StringComparison.Ordinal);
        Assert.Contains("build-1448-39-g41bf33b61", message, StringComparison.Ordinal);
        Assert.Contains("build-1449-2-gdeadbeef", message, StringComparison.Ordinal);
        Assert.Contains(HashA[..19], message, StringComparison.Ordinal);
        Assert.Contains(HashB[..19], message, StringComparison.Ordinal);
        Assert.Contains("--accept-source-move", message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"motif-pins-{Guid.NewGuid():N}.tsv");
        var pins = new[]
        {
            new SourceArtifact(
                "liblcm", SourceArtifact.NuGetPackageKind, "11.0.0-beta0150", "", "MasterLCModel.xml", HashA,
                "2026-08-10T00:00:00Z"),
            Fw("build-1448-39-g41bf33b61", "41bf33b61188", HashB),
        };

        try
        {
            SourcePins.Write(path, pins);
            var read = SourcePins.Read(path);

            // Written in (Source, Artifact) order, not the order handed in, so a re-pin produces a stable diff.
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
    /// The pin file the repository actually ships. All three files have to be in it, or the check silently
    /// stops covering one of them — and the <c>.chm</c> is the one most easily forgotten, because nothing in
    /// the build ever opens it.
    /// </summary>
    [Fact]
    public void TheCheckedInPinFile_PinsAllThreeSourceFiles()
    {
        var pins = SourcePins.Read(RepoPaths.DefaultSourcePinsPath());

        Assert.Equal(3, pins.Count);
        Assert.Contains(pins, p => p.Artifact.EndsWith("MasterLCModel.xml", StringComparison.Ordinal));
        Assert.Contains(pins, p => p.Artifact.EndsWith("ContextHelp.xml", StringComparison.Ordinal));
        Assert.Contains(pins, p => p.Artifact.EndsWith(".chm", StringComparison.Ordinal));

        Assert.All(pins, p => Assert.StartsWith("sha256:", p.Sha256, StringComparison.Ordinal));
        Assert.All(pins, p => Assert.Equal(71, p.Sha256.Length)); // "sha256:" + 64 hex
        Assert.All(pins, p => Assert.False(string.IsNullOrWhiteSpace(p.Release)));
    }
}
