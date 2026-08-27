using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Host.Parser;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using SIL.LCModel.Infrastructure;
using Xunit;

namespace SIL.Motif.Tests.Parser;

/// <summary>
/// Skips at discovery when the parser executable is absent, rather than failing. It is a Rust build in a
/// sibling repository and not checked in, so a developer without it has a build-environment gap, not a
/// broken repository.
/// </summary>
/// <summary>Skips a test that needs a genuine parser, not merely a parser-shaped process.</summary>
/// <remarks>
/// A fake cannot serve these. They assert that every morpheme the parser names is an object the project
/// contains, and that the fallback engine agrees on which words parse — both of which a fake would satisfy
/// by echoing back whatever the test told it, turning the assertion into a tautology. A green test proving
/// nothing is worse than a skipped one, because it looks like coverage.
/// Assertions about Motif's own plumbing take a fake instead; see <c>GrammarCoverageProvenanceTests</c>.
/// </remarks>
public sealed class RealParserFactAttribute : FactAttribute
{
    public RealParserFactAttribute()
    {
        if (PanGlossExecutable.TryLocate() is null)
            Skip = $"pangloss not found. Build it (cargo build --release -p pg-cli) or set " +
                   $"{PanGlossExecutable.PathVariable}.";
    }
}

/// <summary>
/// The seam that everything about grammar review rests on: <b>a project goes in, and analyses come out
/// whose identities name real objects in that same project.</b>
/// </summary>
/// <remarks>
/// <para>
/// This is the test the route was chosen for. Motif can read a coverage percentage from any parser; what it
/// cannot do without GUID-keyed analyses is say <i>which entry</i> or <i>which rule</i> an analysis used, and
/// therefore whether a Proposal that edited that entry changed parsing the way it intended. The HermitCrab-XML
/// route answers in synthetic keys (<c>mrule128</c>, <c>entry1083</c>) that name nothing Motif can look up;
/// only the GUID one is usable.
/// </para>
/// <para>
/// So the assertion is not "the parser ran" but <b>"every morpheme the parser named is an object this project
/// actually contains"</b> — checked by resolving each GUID through the live cache's own object repository. If
/// that ever fails, the whole grammar-feedback design fails with it, and it fails silently otherwise: coverage
/// numbers would keep working while correlation quietly returned nothing.
/// </para>
/// <para>
/// Runs against <see cref="PristineProjectFixture"/>'s two seeded stems rather than a large real project: the
/// claim under test is that a GUID names an object, which the seam either honours or does not regardless of
/// how many entries the project holds.
/// </para>
/// </remarks>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class ParserSeamIntegrationTests : IDisposable
{
    private readonly LcmCache _cache;
    private readonly string _projectPath;

    public ParserSeamIntegrationTests(PristineProjectFixture pristine)
    {
        _cache = pristine.NewScratch();
        RealParserProject.PrepareForParsing(_cache, "m", "o", "t", "i", "f", "a", "b");
        _projectPath = _cache.ProjectId.Path;
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
    }

    [RealParserFact]
    public void EveryMorphemeTheParserNames_IsAnObjectTheProjectContains()
    {
        var words = new[] { SeededProject.FirstForm, SeededProject.SecondForm };

        var (report, refusal) = new PanGlossParser().Assess(_projectPath, words);

        Assert.Null(refusal);
        Assert.NotNull(report);

        // Provenance arrives for free and is what a coverage figure must cite (ADR 0032 §4).
        Assert.StartsWith("sha256:", report!.GrammarSourceSha256);
        Assert.Equal("foma-confirm", report.Pipeline);

        var analysed = report.Words.Where(w => w.Analyses.Count > 0).ToList();
        Assert.NotEmpty(analysed);

        var objects = _cache.ServiceLocator.GetInstance<ICmObjectRepository>();

        var unresolved = new List<string>();
        var resolvedCount = 0;

        foreach (var word in analysed)
        foreach (var analysis in word.Analyses)
        foreach (var morphemeGuid in analysis.MorphemeGuids)
        {
            if (!Guid.TryParse(morphemeGuid, out var guid))
            {
                unresolved.Add($"{word.Word}: '{morphemeGuid}' is not a GUID — this is the synthetic-key " +
                               "shape the HermitCrab-XML route produces, which means the wrong route ran.");
                continue;
            }

            if (objects.IsValidObjectId(guid)) resolvedCount++;
            else unresolved.Add($"{word.Word}: {guid} names no object in this project.");
        }

        Assert.True(
            unresolved.Count == 0,
            $"{unresolved.Count} morpheme identity/identities could not be resolved against the project the " +
            $"parser read. Correlation between parse results and Proposal effects depends on every one of " +
            $"them resolving:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", unresolved.Take(10)));

        Assert.True(resolvedCount > 0, "No morphemes were checked, so this test proved nothing.");
    }

    [RealParserFact]
    public void TheFallbackEngineIsReachable_AndAgreesOnWhichWordsParse()
    {
        var words = new[] { SeededProject.FirstForm, SeededProject.SecondForm, "zzznotaseededword" };
        var parser = new PanGlossParser();

        var pruned = parser.AnalyseBatch(_projectPath, words, ParserEngine.FstPrunedByHermitCrab);
        var hermitCrabOnly = parser.AnalyseBatch(_projectPath, words, ParserEngine.HermitCrabOnly);

        Assert.True(pruned.Succeeded, pruned.Refusal?.Detail ?? "the pruned engine refused");
        Assert.True(hermitCrabOnly.Succeeded, hermitCrabOnly.Refusal?.Detail ?? "the fallback engine refused");

        // The two engines must agree on verdicts; compared by outcome only, since timing differs by design.
        var prunedVerdicts = pruned.Analysis!.Words
            .ToDictionary(w => w.Word, w => w.Outcome);
        var fallbackVerdicts = hermitCrabOnly.Analysis!.Words
            .ToDictionary(w => w.Word, w => w.Outcome);

        var disagreements = prunedVerdicts
            .Where(p => fallbackVerdicts.TryGetValue(p.Key, out var other) && other != p.Value)
            .Select(p => $"{p.Key}: pruned={p.Value} fallback={fallbackVerdicts[p.Key]}")
            .ToList();

        Assert.True(
            disagreements.Count == 0,
            "The two engines disagreed about which words parse. They are designed to be equivalent, so a " +
            "disagreement means the fallback is not a safe substitute:" + Environment.NewLine + "  " +
            string.Join(Environment.NewLine + "  ", disagreements));
    }
}
