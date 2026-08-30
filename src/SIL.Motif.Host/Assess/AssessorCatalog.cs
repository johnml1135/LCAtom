namespace SIL.Motif.Host.Assess;

/// <summary>Resolves an Assessor by the name a scope declares it under.</summary>
public interface IAssessorCatalog
{
    /// <exception cref="KeyNotFoundException">No Assessor is registered under this name.</exception>
    IAssessor Resolve(string name);
}

/// <summary>
/// The fixed set of Assessors one Motif installation knows about, resolved by name.
/// </summary>
/// <remarks>
/// A project's <c>.motif.toml</c> names an Assessor by string (<see cref="SIL.Motif.Host.Config.AssessmentScopeConfiguration.Assessor"/>),
/// and that string must resolve to something or fail loudly — a scope naming an Assessor nobody registered
/// is a configuration error, not a silent no-op.
/// </remarks>
public sealed class AssessorCatalog : IAssessorCatalog
{
    private readonly IReadOnlyDictionary<string, IAssessor> _byName;

    public AssessorCatalog(IEnumerable<IAssessor> assessors)
    {
        ArgumentNullException.ThrowIfNull(assessors);
        var byName = new Dictionary<string, IAssessor>(StringComparer.OrdinalIgnoreCase);
        foreach (var assessor in assessors)
        {
            ArgumentNullException.ThrowIfNull(assessor);
            if (string.IsNullOrWhiteSpace(assessor.Name))
                throw new ArgumentException("Every Assessor must have a non-blank name.", nameof(assessors));
            if (!byName.TryAdd(assessor.Name, assessor))
            {
                throw new ArgumentException(
                    $"Two Assessors are both named '{assessor.Name}'; a catalog cannot resolve either one.",
                    nameof(assessors));
            }
        }
        _byName = byName;
    }

    /// <inheritdoc />
    public IAssessor Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("An Assessor name is required.", nameof(name));
        if (_byName.TryGetValue(name, out var assessor)) return assessor;

        var known = _byName.Count == 0
            ? "(none registered)"
            : string.Join(", ", _byName.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        throw new KeyNotFoundException($"No Assessor named '{name}' is registered. Known Assessors: {known}.");
    }
}
