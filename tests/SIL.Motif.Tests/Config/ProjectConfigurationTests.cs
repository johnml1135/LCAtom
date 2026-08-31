using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Config;
using Xunit;

namespace SIL.Motif.Tests.Config;

/// <summary>
/// Covers <c>&lt;project&gt;.motif.toml</c>'s resolution: the documented defaults when it is absent, its
/// refusal to guess at a malformed or unrecognised declaration, and that a declared scope survives being
/// written back out (ADR 0042 decisions 3 and 5).
/// </summary>
public sealed class ProjectConfigurationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-config-" + Guid.NewGuid().ToString("N"));

    public ProjectConfigurationTests() => Directory.CreateDirectory(_root);

    private ProjectLocator Project(string projectFileName = "Project.fwdata")
    {
        var fwDataPath = Path.Combine(_root, projectFileName);
        File.WriteAllText(fwDataPath, string.Empty);
        return new ProjectLocator(fwDataPath, Path.GetFileNameWithoutExtension(fwDataPath));
    }

    [Fact]
    public void AnAbsentFileYieldsTheDocumentedDefaults()
    {
        var configuration = new ProjectConfigurationReader().Read(Project());

        Assert.True(configuration.GateOnRegression);
        Assert.True(configuration.PurgeOnApply);
        var scope = Assert.Single(configuration.Scopes);
        Assert.Equal(AssessmentScopeConfiguration.DefaultName, scope.Name);
        Assert.Equal(AssessmentScopeConfiguration.DefaultQueryText, scope.Query);
        Assert.Equal(AssessmentScopeConfiguration.DefaultAssessorName, scope.Assessor);
        Assert.Equal(AssessmentScopeConfiguration.DefaultEngineName, scope.Engine);
        Assert.Empty(scope.Collect);
        Assert.Equal(TimeSpan.FromSeconds(1), scope.PerWordLimit);
    }

    [Fact]
    public void AMalformedFileRefusesNamingTheLine()
    {
        const string text = "[regression]\ngate = maybe\n";

        var exception = Assert.Throws<ProjectConfigurationException>(
            () => ProjectConfigurationFile.Parse(text, "Project.motif.toml"));

        Assert.Contains("line 2", exception.Message, StringComparison.Ordinal);
        Assert.Contains("gate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnrecognisedTableAlsoRefusesNamingTheLine()
    {
        const string text = "[regresion]\ngate = true\n";

        var exception = Assert.Throws<ProjectConfigurationException>(
            () => ProjectConfigurationFile.Parse(text, "Project.motif.toml"));

        Assert.Contains("line 1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("regresion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownKeyRefusesRatherThanBeingIgnored()
    {
        const string text = "[regression]\ngate = true\ntolerate-regressions = true\n";

        var exception = Assert.Throws<ProjectConfigurationException>(
            () => ProjectConfigurationFile.Parse(text, "Project.motif.toml"));

        Assert.Contains("line 3", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unknown key", exception.Message, StringComparison.Ordinal);
        Assert.Contains("tolerate-regressions", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownKeyInsideADeclaredScopeAlsoRefuses()
    {
        const string text = "[[scope]]\nname = \"tight\"\nbudget = \"tiny\"\n";

        var exception = Assert.Throws<ProjectConfigurationException>(
            () => ProjectConfigurationFile.Parse(text, "Project.motif.toml"));

        Assert.Contains("line 3", exception.Message, StringComparison.Ordinal);
        Assert.Contains("budget", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoScopesUnderOneNameRefuseRatherThanOneWinningSilently()
    {
        // Whichever won would be arbitrary, and the loser would read as though it had applied.
        const string text =
            """
            [[scope]]
            name = "quick"

            [[scope]]
            name = "quick"
            """;

        var exception = Assert.Throws<ProjectConfigurationException>(
            () => ProjectConfigurationFile.Parse(text, "Project.motif.toml"));

        Assert.Contains("quick", exception.Message, StringComparison.Ordinal);
        Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclaredScopeRoundTripsThroughParseAndRender()
    {
        var declared = new ProjectConfiguration(
            new[]
            {
                new AssessmentScopeConfiguration(
                    name: "tight-loop",
                    query: "all words in text #7",
                    assessor: "pangloss",
                    engine: "fast",
                    collect: new[] { "coverage", "timing" },
                    perWordLimit: TimeSpan.FromMilliseconds(2500)),
            },
            gateOnRegression: true,
            purgeOnApply: false);

        var rendered = ProjectConfigurationFile.Render(declared);
        var reparsed = ProjectConfigurationFile.Parse(rendered, "Project.motif.toml");

        Assert.Equal(declared.GateOnRegression, reparsed.GateOnRegression);
        Assert.Equal(declared.PurgeOnApply, reparsed.PurgeOnApply);
        var originalScope = Assert.Single(declared.Scopes);
        var reparsedScope = Assert.Single(reparsed.Scopes);
        Assert.Equal(originalScope.Name, reparsedScope.Name);
        Assert.Equal(originalScope.Query, reparsedScope.Query);
        Assert.Equal(originalScope.Assessor, reparsedScope.Assessor);
        Assert.Equal(originalScope.Engine, reparsedScope.Engine);
        Assert.Equal(originalScope.Collect, reparsedScope.Collect);
        Assert.Equal(originalScope.PerWordLimit, reparsedScope.PerWordLimit);
    }

    [Fact]
    public void APresentFileFillsOnlyWhatItDeclaresAndDefaultsTheRest()
    {
        var configPath = Path.Combine(_root, "Only.motif.toml");
        File.WriteAllText(configPath, "[apply]\npurge-on-apply = false\n");

        var configuration = new ProjectConfigurationReader().Read(Project("Only.fwdata"));

        Assert.True(configuration.GateOnRegression);
        Assert.False(configuration.PurgeOnApply);
        var scope = Assert.Single(configuration.Scopes);
        Assert.Equal(AssessmentScopeConfiguration.DefaultName, scope.Name);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}
