using SIL.Motif.Host.Assess;

namespace SIL.Motif.Tests.TestFixtures;

/// <summary>
/// A pure, in-process <see cref="IAssessor"/> that declares a fixed set of kinds and honours the same
/// all-or-nothing refusal contract a real Assessor must: nothing is produced when a requested kind is not
/// among the declared ones.
/// </summary>
internal sealed class FakeAssessor : IAssessor
{
    private readonly IReadOnlyList<AssessmentKind> _declaredKinds;

    public FakeAssessor(string name, IReadOnlyList<AssessmentKind> declaredKinds)
    {
        Name = name;
        _declaredKinds = declaredKinds;
    }

    public string Name { get; }

    public IReadOnlyList<AssessmentKind> KindsFor(AssessmentScope scope) => _declaredKinds;

    public Task<IReadOnlyList<ProducedAssessment>> ProduceAsync(
        AssessmentScope scope, string exportedCandidate, CancellationToken cancellationToken)
    {
        var wanted = scope.Collect.Count == 0 ? _declaredKinds : scope.Collect;
        foreach (var kind in wanted)
        {
            if (!_declaredKinds.Contains(kind))
                throw new AssessorRefusalException(Name, kind, "the fake Assessor was not configured to declare this kind.");
        }

        IReadOnlyList<ProducedAssessment> produced = wanted.Select(kind => new ProducedAssessment(kind, null, null)).ToList();
        return Task.FromResult(produced);
    }
}
