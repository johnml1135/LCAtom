using SIL.Motif.Generator;
using SIL.Motif.Generator.ModelSource;
using Xunit;
using Xunit.Abstractions;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// The generator loads all 898 joined rows, reports its own model coverage, and runs in CI without a
/// liblcm source tree. End to end against the real model and manifest.
/// </summary>
public class ModelCoverageReportTests
{
    private readonly ITestOutputHelper _output;

    public ModelCoverageReportTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Load_RealModelAndManifest_ReportsDocumentedCoverageNumbers()
    {
        var loaded = MotifModelLoader.Load();
        var coverage = loaded.Coverage;

        _output.WriteLine(coverage.ToString());

        Assert.Equal(ModelPathSource.NuGetPackageCache, coverage.ModelFilePathSource);
        Assert.True(File.Exists(coverage.ModelFilePath));
        Assert.Equal("7000072", coverage.ModelVersion);
        Assert.Equal(898, coverage.TotalFieldCount);
        // 495, not 494: WfiWordform.SpellingStatus is on, same hand-authored ADR 0025 treatment as its siblings.
        Assert.Equal(495, coverage.InScopeCount);
        Assert.Equal(898 - 495, coverage.OutOfScopeCount);
    }

    [Fact]
    public void Load_RealModelAndManifest_CountsByKindAndCardSumTo898()
    {
        var coverage = MotifModelLoader.Load().Coverage;

        Assert.Equal(898, coverage.CountsByKindAndCard.Values.Sum());
        Assert.Equal(445, coverage.CountsByKindAndCard["basic"]);
        Assert.Equal(235,
            coverage.CountsByKindAndCard.Where(kv => kv.Key.StartsWith("owning")).Sum(kv => kv.Value));
        Assert.Equal(218,
            coverage.CountsByKindAndCard.Where(kv => kv.Key.StartsWith("rel")).Sum(kv => kv.Value));
    }

    [Fact]
    public void Load_RealModelAndManifest_KindsPerGroupIsNonEmptyAndOnlyKnownGroups()
    {
        var coverage = MotifModelLoader.Load().Coverage;

        Assert.NotEmpty(coverage.KindsPerGroup);
        var knownGroups = new HashSet<string> { "grammar", "lexical", "system", "lists", "analysis" };
        Assert.All(coverage.KindsPerGroup.Keys, group => Assert.Contains(group, knownGroups));

        // Every KindsPerGroup count comes from in-scope, authorable (Verbs != n/a) fields, so it must be positive.
        Assert.All(coverage.KindsPerGroup.Values, count => Assert.True(count > 0));
    }

    [Fact]
    public void ToString_IncludesEveryHeadlineNumber()
    {
        var report = MotifModelLoader.Load().Coverage;
        var text = report.ToString();

        Assert.Contains(report.ModelFilePath, text);
        Assert.Contains(report.ModelFilePathSource.ToString(), text);
        Assert.Contains(report.ModelVersion, text);
        Assert.Contains(report.TotalFieldCount.ToString(), text);
        Assert.Contains(report.InScopeCount.ToString(), text);
    }
}
