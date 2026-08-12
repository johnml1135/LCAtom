using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Host.Parser;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests.Parser;

/// <summary>
/// Skips at discovery when the parser executable or the real project this test needs is absent, rather than
/// failing. Neither is checked in — one is a Rust build in a sibling repository, the other a 56 MB FieldWorks
/// project — and a developer without them has a build-environment gap, not a broken repository.
/// </summary>
public sealed class RealParserFactAttribute : FactAttribute
{
    public RealParserFactAttribute()
    {
        if (PanGlossExecutable.TryLocate() is null)
            Skip = $"pangloss not found. Build it (cargo build --release -p pg-cli) or set " +
                   $"{PanGlossExecutable.PathVariable}.";
        else if (RealProject.Sena3Path() is null)
            Skip = "The Sena 3 project was not found in a sibling FieldWorks checkout.";
    }
}

/// <summary>Locates the real project these tests need.</summary>
internal static class RealProject
{
    public static string? Sena3Path()
    {
        var candidate = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
            "FieldWorks", "DistFiles", "Projects", "Sena 3", "Sena 3.fwdata");

        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }
}

/// <summary>
/// The seam that everything about grammar review rests on: <b>a real FieldWorks project goes in, and analyses
/// come out whose identities name real objects in that same project.</b>
/// </summary>
/// <remarks>
/// <para>
/// This is the test the route was chosen for. Motif can read a coverage percentage from any parser; what it
/// cannot do without GUID-keyed analyses is say <i>which entry</i> or <i>which rule</i> an analysis used, and
/// therefore whether a Proposal that edited that entry changed parsing the way it intended. The HermitCrab-XML
/// route answers in synthetic keys (<c>mrule128</c>, <c>entry1083</c>) that name nothing Motif can look up;
/// measured side by side on the same 40 Sena 3 words, the two routes agree on every analysis — same
/// morpheme counts, same consistent identity mapping — so the difference is purely the namespace, and
/// only the GUID one (<c>603fc0f8-…</c>, <c>0832679c-…</c> for those same two keys) is usable.
/// </para>
/// <para>
/// So the assertion is not "the parser ran" but <b>"every morpheme the parser named is an object this project
/// actually contains"</b> — checked by resolving each GUID through the live cache's own object repository. If
/// that ever fails, the whole grammar-feedback design fails with it, and it fails silently otherwise: coverage
/// numbers would keep working while correlation quietly returned nothing.
/// </para>
/// </remarks>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
[Trait("Fixture", "TestLangProj")]
public sealed class ParserSeamIntegrationTests
{
    // Sena words drawn from the project's own corpus. Small, because the FST build alone is ~11 s.
    private static readonly string[] Words = { "mbali", "ya", "miseru", "nkazi", "munthu", "anthu" };

    [RealParserFact]
    public void EveryMorphemeTheParserNames_IsAnObjectTheProjectContains()
    {
        var projectPath = RealProject.Sena3Path()!;

        var (report, refusal) = new PanGlossParser().Assess(projectPath, Words);

        Assert.Null(refusal);
        Assert.NotNull(report);

        // Provenance arrives for free and is what a coverage figure must cite (ADR 0032 §4).
        Assert.StartsWith("sha256:", report!.GrammarSourceSha256);
        Assert.Equal("foma-confirm", report.Pipeline);

        var analysed = report.Words.Where(w => w.Analyses.Count > 0).ToList();
        Assert.NotEmpty(analysed);

        using var cache = new FwDataProjectLoader().LoadCache(projectPath);
        var objects = cache.ServiceLocator.GetInstance<ICmObjectRepository>();

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
        var projectPath = RealProject.Sena3Path()!;
        var parser = new PanGlossParser();

        var pruned = parser.AnalyseBatch(projectPath, Words, ParserEngine.FstPrunedByHermitCrab);
        var hermitCrabOnly = parser.AnalyseBatch(projectPath, Words, ParserEngine.HermitCrabOnly);

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
