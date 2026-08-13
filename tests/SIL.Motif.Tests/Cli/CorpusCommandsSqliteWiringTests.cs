using System;
using System.IO;
using SIL.Motif.Cli;
using SIL.Motif.Host.Corpus;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// <see cref="CorpusCommands.StoreFor"/> now points at <see cref="SqliteCorpusStore"/>
/// (ADR 0036 decision 6); this proves the CLI verbs still work end to end against it, not only the
/// store in isolation.
/// </summary>
public sealed class CorpusCommandsSqliteWiringTests : IDisposable
{
    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), "motif-cli-corpus-wiring-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storeDir)) Directory.Delete(_storeDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void StoreForReturnsASqliteBackedStore_AndTheCliVerbsRoundTripThroughIt()
    {
        Assert.IsType<SqliteCorpusStore>(CorpusCommands.StoreFor(_storeDir));

        var addResult = CorpusCommands.AddCorpus(
            _storeDir, "tst-corpus", "Testlang corpus", uri: null, licence: "CC-BY-SA-4.0",
            capabilities: LicenceCapabilities.Unknown(), tokeniser: "whitespace-and-punctuation",
            tokeniserVersion: "1", tokeniserNotes: null);
        Assert.Equal(0, addResult.ExitCode);

        var listResult = CorpusCommands.ListCorpora(_storeDir);
        Assert.Contains("tst-corpus", listResult.Output);

        // The database file lives under the same store root Proposals and Receipts already use.
        Assert.True(File.Exists(Path.Combine(_storeDir, "motif.db")));
    }
}
