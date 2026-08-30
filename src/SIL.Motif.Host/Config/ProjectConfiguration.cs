using SIL.Motif.Contract.Projects;

namespace SIL.Motif.Host.Config;

/// <summary>
/// One declared Assessment scope: which words, which Assessor and engine, what to collect, and the
/// per-word limit (ADR 0042 decision 3).
/// </summary>
/// <remarks>
/// <c>Query</c> names which words a scope wants, in words a future query language will interpret; it is
/// not a <c>Selection</c> — the resolved word list belongs to an Assessment, not to a declaration that can
/// be edited after the fact. An empty <see cref="Collect"/> means the Assessor's own default for its kind,
/// because no default collection set is settled yet.
/// </remarks>
public sealed record AssessmentScopeConfiguration
{
    public const string DefaultName = "default";
    public const string DefaultQueryText = "all words carrying a manual analysis";
    public const string DefaultAssessorName = "pangloss";
    public const string DefaultEngineName = "fast";

    public static readonly TimeSpan DefaultPerWordLimit = TimeSpan.FromSeconds(1);

    public AssessmentScopeConfiguration(
        string name,
        string query,
        string assessor,
        string engine,
        IReadOnlyList<string> collect,
        TimeSpan perWordLimit)
    {
        Name = RequireNonBlank(name, nameof(name));
        Query = RequireNonBlank(query, nameof(query));
        Assessor = RequireNonBlank(assessor, nameof(assessor));
        Engine = RequireNonBlank(engine, nameof(engine));
        ArgumentNullException.ThrowIfNull(collect);
        Collect = collect;
        if (perWordLimit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(perWordLimit), "A per-word limit must be positive.");
        PerWordLimit = perWordLimit;
    }

    /// <summary>The scope's name, unique within a project's declarations.</summary>
    public string Name { get; }

    /// <summary>Which words the scope wants, in a future query language's terms.</summary>
    public string Query { get; }

    /// <summary>Which Assessor makes this scope's Assessments.</summary>
    public string Assessor { get; }

    /// <summary>Which engine the Assessor runs under.</summary>
    public string Engine { get; }

    /// <summary>Which kinds to collect; empty means the Assessor's own default.</summary>
    public IReadOnlyList<string> Collect { get; }

    /// <summary>The per-word cap; part of the scope because coverage under one cap is not comparable with another.</summary>
    public TimeSpan PerWordLimit { get; }

    /// <summary>The scope declared when a project names none of its own.</summary>
    public static AssessmentScopeConfiguration Default() => new(
        DefaultName, DefaultQueryText, DefaultAssessorName, DefaultEngineName,
        Array.Empty<string>(), DefaultPerWordLimit);

    private static string RequireNonBlank(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-blank value is required.", parameterName)
            : value;
}

/// <summary>
/// A project's resolved Assessment configuration: its declared scopes, its regression policy, and whether
/// applying sweeps a Proposal's working artifacts (ADR 0042 decisions 3 and 5).
/// </summary>
/// <remarks>
/// Always fully resolved — every field is populated, whether from <c>&lt;project&gt;.motif.toml</c> or from
/// the documented defaults. A caller never has to ask which defaults applied; <see cref="Defaults"/> and
/// <see cref="ProjectConfigurationReader"/> are the only two ways one of these is produced.
/// </remarks>
public sealed record ProjectConfiguration
{
    public ProjectConfiguration(
        IReadOnlyList<AssessmentScopeConfiguration> scopes,
        bool gateOnRegression,
        bool purgeOnApply)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        if (scopes.Count == 0)
            throw new ArgumentException("A configuration must declare at least one scope.", nameof(scopes));
        Scopes = scopes;
        GateOnRegression = gateOnRegression;
        PurgeOnApply = purgeOnApply;
    }

    /// <summary>Every declared Assessment scope, at least one.</summary>
    public IReadOnlyList<AssessmentScopeConfiguration> Scopes { get; }

    /// <summary>Whether a regression blocks <c>apply</c> (ADR 0042 decision 5). Off by default.</summary>
    public bool GateOnRegression { get; }

    /// <summary>Whether applying a Proposal sweeps its Trials, Dry Runs and Assessments. On by default.</summary>
    public bool PurgeOnApply { get; }

    /// <summary>The documented configuration for a project with no <c>.motif.toml</c>.</summary>
    public static ProjectConfiguration Defaults() => new(
        new[] { AssessmentScopeConfiguration.Default() },
        gateOnRegression: false,
        purgeOnApply: true);
}

/// <summary>Hands over a project path and receives a resolved configuration; a caller never sees TOML.</summary>
public interface IProjectConfigurationReader
{
    /// <summary>Reads and resolves <c>&lt;project&gt;.motif.toml</c>, or the documented defaults if absent.</summary>
    /// <exception cref="ProjectConfigurationException">The file is present and malformed, naming the line.</exception>
    ProjectConfiguration Read(ProjectLocator project);
}

/// <summary>The only production <see cref="IProjectConfigurationReader"/>: TOML on disk, resolved.</summary>
public sealed class ProjectConfigurationReader : IProjectConfigurationReader
{
    /// <summary>Derives the sibling <c>.motif.toml</c> path from a project data-file locator.</summary>
    public static string PathFor(ProjectLocator project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var directory = Path.GetDirectoryName(project.FullFwDataPath)!;
        var stem = Path.GetFileNameWithoutExtension(project.FullFwDataPath);
        return Path.Combine(directory, stem + ".motif.toml");
    }

    public ProjectConfiguration Read(ProjectLocator project)
    {
        var path = PathFor(project);
        return File.Exists(path)
            ? ProjectConfigurationFile.Parse(File.ReadAllText(path), path)
            : ProjectConfiguration.Defaults();
    }
}
