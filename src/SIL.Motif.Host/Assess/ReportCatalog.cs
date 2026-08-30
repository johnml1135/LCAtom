namespace SIL.Motif.Host.Assess;

/// <summary>
/// The registered set of report kinds one Motif installation knows how to produce. Adding a kind is
/// registering another <see cref="IReportProducer"/> here — never a new verb and never a new <c>case</c> in
/// a caller, which is the property ADR 0042's Reports amendment asks for.
/// </summary>
public sealed class ReportCatalog
{
    private readonly IReadOnlyDictionary<string, IReportProducer> _byKind;

    public ReportCatalog(IEnumerable<IReportProducer> producers)
    {
        ArgumentNullException.ThrowIfNull(producers);
        var byKind = new Dictionary<string, IReportProducer>(StringComparer.OrdinalIgnoreCase);
        foreach (var producer in producers)
        {
            ArgumentNullException.ThrowIfNull(producer);
            if (string.IsNullOrWhiteSpace(producer.Kind))
                throw new ArgumentException("Every report kind must have a non-blank name.", nameof(producers));
            if (!byKind.TryAdd(producer.Kind, producer))
            {
                throw new ArgumentException(
                    $"Two report kinds are both named '{producer.Kind}'; a catalog cannot resolve either one.",
                    nameof(producers));
            }
        }
        _byKind = byKind;
    }

    /// <summary>Every registered kind, ordinally by name — what <c>--list-kinds</c> shows.</summary>
    public IReadOnlyList<IReportProducer> All =>
        _byKind.Values.OrderBy(producer => producer.Kind, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <exception cref="KeyNotFoundException">No report kind is registered under this name.</exception>
    public IReportProducer Resolve(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("A report kind is required.", nameof(kind));
        if (_byKind.TryGetValue(kind, out var producer)) return producer;

        var known = _byKind.Count == 0
            ? "(none registered)"
            : string.Join(", ", _byKind.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        throw new KeyNotFoundException($"No report kind named '{kind}' is registered. Known kinds: {known}.");
    }
}
