using SIL.Motif.Host.Assess;
using SIL.Motif.Tests.TestFixtures;
using Xunit;

namespace SIL.Motif.Tests.Assess;

/// <summary>
/// The seam itself, exercised against a fake Assessor rather than PanGloss: an Assessor declares which
/// kinds it can produce for a scope, and a request for one it did not declare is a refusal naming the kind
/// and the reason (ADR 0042 decision 1 and its amendment on Assessment kind) — pinned by
/// `AskingForAnUndeclaredKind_RefusesNamingTheKindAndTheReason`.
/// </summary>
public sealed class AssessorSeamTests
{
    private static AssessmentScope Scope(params AssessmentKind[] collect) => new(
        words: ["motifa"], engine: "fast", collect: collect, perWordLimit: TimeSpan.FromSeconds(1));

    [Fact]
    public async Task AnAssessorDeclaringTwoKinds_ProducesExactlyThose()
    {
        var assessor = new FakeAssessor("fake", [AssessmentKind.ParseTime, AssessmentKind.Correctness]);

        var produced = await assessor.ProduceAsync(
            Scope(AssessmentKind.ParseTime, AssessmentKind.Correctness), "unused", CancellationToken.None);

        Assert.Equal(
            new[] { AssessmentKind.ParseTime, AssessmentKind.Correctness },
            produced.Select(p => p.Kind));
    }

    [Fact]
    public async Task AskingForAnUndeclaredKind_RefusesNamingTheKindAndTheReason()
    {
        var assessor = new FakeAssessor("fake", [AssessmentKind.ParseTime, AssessmentKind.Correctness]);

        var failure = await Assert.ThrowsAsync<AssessorRefusalException>(() => assessor.ProduceAsync(
            Scope(AssessmentKind.ParseTime, AssessmentKind.ObjectTiming), "unused", CancellationToken.None));

        Assert.Equal(AssessmentKind.ObjectTiming, failure.Kind);
        Assert.Contains("ObjectTiming", failure.Message, StringComparison.Ordinal);
        Assert.Contains("not configured to declare", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusingOneKind_ProducesNoneOfTheOthers()
    {
        var assessor = new FakeAssessor("fake", [AssessmentKind.ParseTime]);

        await Assert.ThrowsAsync<AssessorRefusalException>(() => assessor.ProduceAsync(
            Scope(AssessmentKind.ParseTime, AssessmentKind.Correctness), "unused", CancellationToken.None));
    }
}
