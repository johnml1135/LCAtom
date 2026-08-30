using SIL.Motif.Worker.Assess;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Assess;

/// <summary>
/// A cache path is stable for one grammar digest, Assessor and engine, and differs when any of the three
/// differs — the key that keeps two engines from ever sharing one cache file (ADR 0042 decision 8).
/// </summary>
public sealed class StatsCacheStoreTests : IDisposable
{
    private const string GrammarA = "sha256:" + "aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa11aa";
    private const string GrammarB = "sha256:" + "bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb22bb";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-stats-cache-" + Guid.NewGuid().ToString("N"));
    private readonly StatsCacheStore _store;

    public StatsCacheStoreTests()
    {
        Directory.CreateDirectory(_root);
        _store = new StatsCacheStore(WorkspaceOwnership.Bootstrap(_root));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    [Fact]
    public void TheSameGrammarAssessorAndEngine_YieldTheSamePathTwice()
    {
        var first = _store.PathFor(GrammarA, "pangloss", "fast");
        var second = _store.PathFor(GrammarA, "pangloss", "fast");

        Assert.Equal(first, second);
    }

    [Fact]
    public void ADifferentGrammarDigest_YieldsADifferentPath()
    {
        var first = _store.PathFor(GrammarA, "pangloss", "fast");
        var second = _store.PathFor(GrammarB, "pangloss", "fast");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ADifferentAssessor_YieldsADifferentPath()
    {
        var first = _store.PathFor(GrammarA, "pangloss", "fast");
        var second = _store.PathFor(GrammarA, "hermitcrab", "fast");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ADifferentEngine_YieldsADifferentPath()
    {
        var first = _store.PathFor(GrammarA, "pangloss", "fast");
        var second = _store.PathFor(GrammarA, "pangloss", "accurate");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ThePathIsUnderTheWorkerRoot_AndItsParentDirectoryExists()
    {
        var path = _store.PathFor(GrammarA, "pangloss", "fast");

        Assert.StartsWith(_root, path, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.GetDirectoryName(path)));
    }
}
