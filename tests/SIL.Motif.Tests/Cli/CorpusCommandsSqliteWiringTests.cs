using System;
using System.IO;
using System.Linq;
using SIL.Motif.Cli;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Projection.Usage;
using SIL.Motif.Tests.Projection;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// <see cref="CorpusCommands.StoreFor"/> points at <see cref="SqliteCorpusStore"/> over a project's
/// paired database; this proves the CLI verbs still work end to end against it, not only the store
/// in isolation.
/// </summary>
public sealed class CorpusCommandsSqliteWiringTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-cli-corpus-wiring-tests", Guid.NewGuid().ToString("N"));
    private readonly string _fwDataPath;

    public CorpusCommandsSqliteWiringTests()
    {
        Directory.CreateDirectory(_root);
        _fwDataPath = Path.Combine(_root, "Project.fwdata");
        File.WriteAllText(_fwDataPath, string.Empty);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void StoreForReturnsASqliteBackedStore_AndTheCliVerbsRoundTripThroughIt()
    {
        var project = new ProjectLocator(_fwDataPath, "Project");
        Assert.IsType<SqliteCorpusStore>(CorpusCommands.StoreFor(project));

        var addResult = CorpusCommands.AddCorpus(
            _fwDataPath, "1.0", "tst-corpus", "Testlang corpus", uri: null, licence: "CC-BY-SA-4.0",
            capabilities: LicenceCapabilities.Unknown(), tokeniser: "whitespace-and-punctuation",
            tokeniserVersion: "1", tokeniserNotes: null);
        Assert.Equal(0, addResult.ExitCode);

        var listResult = CorpusCommands.ListCorpora(_fwDataPath, "1.0");
        Assert.Contains("tst-corpus", listResult.Output);

        var usage = new UsageLog();
        var listJson = CorpusCommands.ListCorporaJson(_fwDataPath, "1.0", usage);
        var detailText = CorpusCommands.ShowCorpus(_fwDataPath, "1.0", "tst-corpus", usage);
        var detailJson = CorpusCommands.ShowCorpusJson(_fwDataPath, "1.0", "tst-corpus", usage);

        Assert.Equal(0, listJson.ExitCode);
        Assert.Equal(0, detailText.ExitCode);
        Assert.Equal(0, detailJson.ExitCode);
        Assert.Contains("tst-corpus", listJson.Output);
        Assert.Contains("Testlang corpus", listJson.Output);
        FigureAudit.AssertEveryTextFigureAppearsInJson(detailText.Output, detailJson.Output);
        Assert.Equal(new[] { "corpora", "show-corpus", "show-corpus" }, usage.Entries.Select(e => e.Command));
        Assert.All(usage.Entries, entry => Assert.DoesNotContain("tst-corpus", entry.ArgumentShape));

        var missingText = CorpusCommands.ShowCorpus(_fwDataPath, "1.0", "missing");
        var missingJson = CorpusCommands.ShowCorpusJson(_fwDataPath, "1.0", "missing");
        Assert.Equal(1, missingText.ExitCode);
        Assert.Equal(missingText, missingJson);
        Assert.Equal("No corpus 'missing' in store." + Environment.NewLine, missingText.Output);

        // The database lives beside the project, not in a directory the caller happened to run from.
        Assert.True(File.Exists(ProjectDatabaseCatalog.DatabasePathFor(project)));
    }
}
