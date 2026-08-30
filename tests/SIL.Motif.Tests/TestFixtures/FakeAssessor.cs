using SIL.Motif.Host.Assess;

namespace SIL.Motif.Tests.TestFixtures;

/// <summary>
/// A pure, in-process <see cref="IAssessor"/> that declares a fixed set of kinds and honours the same
/// all-or-nothing refusal contract a real Assessor must: nothing is produced when a requested kind is not
/// among the declared ones.
/// </summary>
internal sealed class FakeAssessor : IAssessor
{
    private const string FakeGrammarSha256 = "sha256:" + "ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff";

    private readonly IReadOnlyList<AssessmentKind> _declaredKinds;

    public FakeAssessor(string name, IReadOnlyList<AssessmentKind> declaredKinds)
    {
        Name = name;
        _declaredKinds = declaredKinds;
    }

    public string Name { get; }

    public IReadOnlyList<AssessmentKind> SupportedKinds => _declaredKinds;

    public Task<IReadOnlyList<ProducedAssessment>> ProduceAsync(
        AssessmentScope scope, string exportedCandidate, CancellationToken cancellationToken)
    {
        var wanted = scope.Collect.Count == 0 ? _declaredKinds : scope.Collect;
        foreach (var kind in wanted)
        {
            if (!_declaredKinds.Contains(kind))
                throw new AssessorRefusalException(Name, kind, "the fake Assessor was not configured to declare this kind.");
        }

        IReadOnlyList<ProducedAssessment> produced = wanted
            .Select(kind => new ProducedAssessment(kind, FakeGrammarSha256, "sha256:" + new string('0', 64),
                "sha256:" + new string('0', 64), "fake-model", "fake-pipeline", 0,
                new AssessmentRaw.WordMeasurements([])))
            .ToList();
        return Task.FromResult(produced);
    }
}
