using SIL.LCModel;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Spikes.ScratchCache;
using SIL.Motif.Tests.TestFixtures;
using Xunit;
using Xunit.Abstractions;

namespace SIL.Motif.Tests.Runner;

/// <summary>
/// ADR 0016: does a scratch cache actually work, and is it equivalent to the live cache it came from?
/// </summary>
/// <remarks>
/// These run against the small <c>TestLangProj</c> fixture, so they assert <b>behaviour</b> and say nothing
/// about cost — timing at real scale is the console harness's job
/// (<c>spikes/SIL.Motif.Spikes.ScratchCache</c>, pointed at Sena 3). Keeping the equivalence checks here
/// means a regression in either strategy fails the suite rather than waiting for someone to re-run a spike.
/// </remarks>
[Collection(LcmCacheTestCollection.Name)]
[Trait("Fixture", "FieldWorks")]
public class ScratchCacheEquivalenceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _tempRoot;
    private readonly string _fwDataPath;
    private readonly LcmCache _live;

    public ScratchCacheEquivalenceTests(ITestOutputHelper output)
    {
        _output = output;
        _tempRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _fwDataPath = TestLangProjFixture.CopyToTempAndGetFwDataPath(_tempRoot);
        _live = new FwDataProjectLoader().LoadCache(_fwDataPath);
    }

    [Fact]
    public void InMemoryCopy_ProducesAUsableCacheWithTheSameObjectsAndText()
    {
        var factory = new ScratchCacheFactory();
        using var scratch = factory.CreateInMemoryCopy(_live);

        var report = ScratchComparison.Compare(_live, scratch, "in-memory copy");
        Log(report);

        // The load-bearing claims: the scratch is a real cache holding the same data.
        Assert.Equal(_live.ServiceLocator.GetInstance<ILexEntryRepository>().Count, report.ScratchEntryCount);
        Assert.True(report.ScratchEntryCount > 0, "the fixture should contain lexical entries");
        Assert.Empty(report.SenseTextMismatches);
        Assert.Empty(report.CustomFields.Missing);
        Assert.Empty(report.CustomFields.FlidDrift);
        Assert.Empty(report.WritingSystems.Missing);
    }

    [Fact]
    public void InMemoryCopy_DoesNotShareStateWithTheLiveCache()
    {
        var factory = new ScratchCacheFactory();
        using var scratch = factory.CreateInMemoryCopy(_live);

        var sense = _live.ServiceLocator.GetInstance<ILexSenseRepository>().AllInstances().OrderBy(s => s.Guid).First();
        var originalGloss = sense.Gloss.AnalysisDefaultWritingSystem?.Text;

        // Mutate the SCRATCH: ADR 0016's whole point is that a dry run may mutate freely here.
        var scratchSense = scratch.ServiceLocator.GetInstance<ILexSenseRepository>().GetObject(sense.Guid);
        SIL.LCModel.Infrastructure.NonUndoableUnitOfWorkHelper.Do(scratch.ActionHandlerAccessor, () =>
        {
            scratchSense.Gloss.SetAnalysisDefaultWritingSystem("mutated-in-scratch");
        });

        Assert.Equal("mutated-in-scratch", scratchSense.Gloss.AnalysisDefaultWritingSystem?.Text);
        Assert.Equal(originalGloss, sense.Gloss.AnalysisDefaultWritingSystem?.Text);
    }

    [Fact]
    public void InMemoryCopy_CapturesUnsavedEditsFromTheLiveCache()
    {
        // The on-disk .fwdata is stale until Save, so a file copy would miss this edit entirely.
        var sense = _live.ServiceLocator.GetInstance<ILexSenseRepository>().AllInstances().OrderBy(s => s.Guid).First();
        SIL.LCModel.Infrastructure.NonUndoableUnitOfWorkHelper.Do(_live.ActionHandlerAccessor, () =>
        {
            sense.Gloss.SetAnalysisDefaultWritingSystem("unsaved-live-edit");
        });

        var factory = new ScratchCacheFactory();
        using var scratch = factory.CreateInMemoryCopy(_live);

        var scratchSense = scratch.ServiceLocator.GetInstance<ILexSenseRepository>().GetObject(sense.Guid);
        Assert.Equal("unsaved-live-edit", scratchSense.Gloss.AnalysisDefaultWritingSystem?.Text);
    }

    [Fact]
    public void FileCopyPath_ProducesAUsableCache_AndIsTheWritingSystemControl()
    {
        var factory = new ScratchCacheFactory();
        var destRoot = Path.Combine(_tempRoot, "file-scratch");
        using var scratch = factory.CreateFromFileCopy(_fwDataPath, destRoot);

        var report = ScratchComparison.Compare(_live, scratch, "file copy + open");
        Log(report);

        Assert.Empty(report.SenseTextMismatches);
        Assert.Empty(report.WritingSystems.Missing);

        // A real writing-system store means every WS comes back value-equal; a failure means fixture drift.
        Assert.Equal(report.WritingSystems.LiveCount, report.WritingSystems.ValueEqualCount);
        Assert.True(report.IsUsableForWritingSystemSensitiveWork,
            "the file path is the control: it should be equivalent to live on every axis, including writing systems");
    }

    /// <summary>
    /// Pins the measured hazard as a test rather than a note: if liblcm ever attaches a real writing-system
    /// store to a memory-only cache, this fails and ADR 0016's single-path decision can be revisited.
    /// </summary>
    [Fact]
    public void InMemoryCopy_LosesWritingSystemDefinitions_WhichIsWhyStrategyChoiceMatters()
    {
        var factory = new ScratchCacheFactory();
        using var scratch = factory.CreateInMemoryCopy(_live);

        var report = ScratchComparison.Compare(_live, scratch, "in-memory copy");
        Log(report);

        Assert.True(report.IsUsableForTextFidelity, "text must still round-trip correctly");
        Assert.False(report.IsUsableForWritingSystemSensitiveWork,
            "a memory-only scratch synthesizes writing systems from the bare language tag");
        Assert.NotEmpty(report.WritingSystems.Degraded);
    }

    /// <summary>
    /// The measurement that removed the choice: a memory-only copy of a FILE-LOADED scratch — one whose
    /// writing systems are provably intact — still loses them, because the loss belongs to the target
    /// backend rather than the source. So "build losslessly, then fan out cheaply" is not available, and
    /// ADR 0016 settles on the file path alone.
    /// </summary>
    [Fact]
    public void InMemoryCopy_OfALosslessScratch_IsStillLossy_WhichIsWhyThereIsOnlyOneCanonicalPath()
    {
        var factory = new ScratchCacheFactory();
        using var fileScratch = factory.CreateFromFileCopy(_fwDataPath, Path.Combine(_tempRoot, "file-scratch-2"));

        var control = ScratchComparison.Compare(_live, fileScratch, "file copy (control)");
        Assert.True(control.IsUsableForWritingSystemSensitiveWork, "precondition: the file-loaded scratch is lossless");

        using var derived = factory.CreateInMemoryCopy(fileScratch, "derived-from-file");
        var report = ScratchComparison.Compare(_live, derived, "in-memory copy of the file-loaded scratch");
        Log(report);

        Assert.False(report.IsUsableForWritingSystemSensitiveWork,
            "if this ever passes, liblcm has started attaching a real writing-system store to memory-only " +
            "caches, and ADR 0016's single-path decision can be revisited");
    }

    private void Log(ScratchComparison.ComparisonReport report)
    {
        _output.WriteLine($"[{report.Strategy}] objects live={report.LiveObjectCount} scratch={report.ScratchObjectCount}; " +
                          $"ws {report.WritingSystems.ValueEqualCount}/{report.WritingSystems.LiveCount} value-equal; " +
                          $"custom fields {report.CustomFields.LiveCount}/{report.CustomFields.ScratchCount}; " +
                          $"text-fidelity={report.IsUsableForTextFidelity}; ws-safe={report.IsUsableForWritingSystemSensitiveWork}");
        foreach (var finding in report.Findings) _output.WriteLine($"  - {finding}");
    }

    public void Dispose()
    {
        _live.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort */ }
    }
}
