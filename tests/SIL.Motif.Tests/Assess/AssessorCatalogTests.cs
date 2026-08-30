using SIL.Motif.Host.Assess;
using SIL.Motif.Tests.TestFixtures;
using Xunit;

namespace SIL.Motif.Tests.Assess;

public sealed class AssessorCatalogTests
{
    [Fact]
    public void ResolvesARegisteredAssessorByName()
    {
        var pangloss = new FakeAssessor("pangloss", [AssessmentKind.ParseTime]);
        var catalog = new AssessorCatalog([pangloss, new FakeAssessor("hermitcrab", [AssessmentKind.Correctness])]);

        Assert.Same(pangloss, catalog.Resolve("pangloss"));
    }

    [Fact]
    public void RefusesAnUnknownAssessorName()
    {
        var catalog = new AssessorCatalog([new FakeAssessor("pangloss", [AssessmentKind.ParseTime])]);

        var failure = Assert.Throws<KeyNotFoundException>(() => catalog.Resolve("nonexistent"));

        Assert.Contains("nonexistent", failure.Message, StringComparison.Ordinal);
        Assert.Contains("pangloss", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesTwoAssessorsRegisteredUnderTheSameName()
    {
        var duplicate = Assert.Throws<ArgumentException>(() => new AssessorCatalog(
        [
            new FakeAssessor("pangloss", [AssessmentKind.ParseTime]),
            new FakeAssessor("pangloss", [AssessmentKind.Correctness]),
        ]));

        Assert.Contains("pangloss", duplicate.Message, StringComparison.Ordinal);
    }
}
