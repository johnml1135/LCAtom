using System;
using System.IO;
using SIL.Motif.Projection.Usage;
using Xunit;

namespace SIL.Motif.Tests.Projection.Usage;

public sealed class UsageLogFileTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "SIL.Motif.Tests.Usage", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReadAll_OnAMissingFile_ReturnsEmpty()
    {
        var path = Path.Combine(_tempRoot, "usage.jsonl");
        Assert.Empty(UsageLogFile.ReadAll(path));
    }

    [Fact]
    public void Append_ThenReadAll_RoundTripsEveryEntryAcrossCalls()
    {
        var path = Path.Combine(_tempRoot, "usage.jsonl");

        // Two separate calls, as two separate CLI process invocations would produce.
        UsageLogFile.Append(path, new UsageLogEntry("20260101T000000Z", "list", new[] { "storeDir:text" }));
        UsageLogFile.Append(
            path, new UsageLogEntry("20260101T000001Z", "show", new[] { "storeDir:text", "proposalId:text" }));

        var entries = UsageLogFile.ReadAll(path);

        Assert.Equal(2, entries.Count);
        Assert.Equal("list", entries[0].Command);
        Assert.Equal(new[] { "storeDir:text" }, entries[0].ArgumentShape);
        Assert.Equal("show", entries[1].Command);
        Assert.Equal(new[] { "storeDir:text", "proposalId:text" }, entries[1].ArgumentShape);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best effort */ }
    }
}
