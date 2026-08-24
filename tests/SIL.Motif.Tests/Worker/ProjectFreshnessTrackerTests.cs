using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Worker.Projects;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class ProjectFreshnessTrackerTests
{
    private const string DigestA = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DigestB = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void UpdatesIgnoreStaleEpochsAndLowerGenerations()
    {
        var tracker = new ProjectFreshnessTracker();
        Assert.True(tracker.Register(Observation("first", 4, false, DigestA)));
        Assert.False(tracker.Update(Observation("first", 3, true, DigestB)));
        Assert.False(tracker.Update(Observation("stale", 8, true, DigestB)));
        Assert.Equal(4, tracker.Current!.EditGeneration);
    }

    [Fact]
    public void NewRegistrationStartsANewEpochAndFreshnessUsesSavedDigest()
    {
        var tracker = new ProjectFreshnessTracker();
        tracker.Register(Observation("first", 99, false, DigestA));
        tracker.Disconnect("first");
        tracker.Register(Observation("second", 0, false, DigestB));
        var token = Token("first", 99, DigestA);

        Assert.Equal(BaselineFreshness.KnownOld, tracker.Check(token));
    }

    [Fact]
    public void MissingObservationReportsCurrentnessNotChecked()
    {
        var tracker = new ProjectFreshnessTracker();

        Assert.Equal(BaselineFreshness.CurrentnessNotChecked, tracker.Check(Token("first", 1, DigestA)));
    }

    [Fact]
    public void DirtyOrLaterSameEpochObservationMakesBaselineKnownOld()
    {
        var tracker = new ProjectFreshnessTracker();
        tracker.Register(Observation("first", 2, false, DigestA));
        var token = Token("first", 1, DigestA);

        Assert.Equal(BaselineFreshness.KnownOld, tracker.Check(token));
        tracker.Update(Observation("first", 2, true, DigestA));
        Assert.Equal(BaselineFreshness.KnownOld, tracker.Check(token));
    }

    [Fact]
    public void EqualCleanSameEpochObservationIsCurrent()
    {
        var tracker = new ProjectFreshnessTracker();
        tracker.Register(Observation("first", 2, false, DigestA));

        Assert.Equal(BaselineFreshness.Current, tracker.Check(Token("first", 2, DigestA)));
    }

    [Fact]
    public void NewEpochWithEqualSavedDigestIsCurrent()
    {
        var tracker = new ProjectFreshnessTracker();
        tracker.Register(Observation("second", 0, false, DigestA));

        Assert.Equal(BaselineFreshness.Current, tracker.Check(Token("first", 99, DigestA)));
    }

    private static LiveProjectObservation Observation(string session, long generation, bool dirty, string digest) =>
        new(session, generation, dirty, digest);

    private static BaselineToken Token(string session, long generation, string digest) =>
        new("project", digest, "1", "2026-08-24T00:00:00Z", DigestA, session, generation);
}
