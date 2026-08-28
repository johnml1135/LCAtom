using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using SIL.Motif.Projection.Usage;
using Xunit;

namespace SIL.Motif.Tests.Store;

/// <summary>Covers the machine store: one database per logged-in user, holding Known projects and usage.</summary>
public sealed class MachineStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-machine-" + Guid.NewGuid().ToString("N"));

    public MachineStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void RecordingTheSameProjectTwiceUpdatesLastSeenRatherThanDuplicating()
    {
        using var database = MachineDatabase.Open(_root);
        var registry = new KnownProjectRegistry(database);
        var path = Path.Combine(_root, "project.fwdata");
        var firstSeen = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var secondSeen = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);

        registry.Record("workspace-1", path, firstSeen);
        registry.Record("workspace-1", path, secondSeen);

        var only = Assert.Single(registry.List());
        Assert.Equal("workspace-1", only.WorkspaceKey);
        Assert.Equal(path, only.FullFwDataPath);
        Assert.Equal(secondSeen, only.LastSeenUtc);
    }

    [Fact]
    public void ListReturnsWhatWasRecorded()
    {
        using var database = MachineDatabase.Open(_root);
        var registry = new KnownProjectRegistry(database);
        var seen = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        registry.Record("workspace-a", Path.Combine(_root, "a.fwdata"), seen);
        registry.Record("workspace-b", Path.Combine(_root, "b.fwdata"), seen);

        var listed = registry.List();
        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, record => record.WorkspaceKey == "workspace-a");
        Assert.Contains(listed, record => record.WorkspaceKey == "workspace-b");
    }

    [Fact]
    public void ForgetRemovesAProjectAndAMissingFwdataIsStillRecordable()
    {
        using var database = MachineDatabase.Open(_root);
        var registry = new KnownProjectRegistry(database);
        var seen = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        // The sweep forgets a project whose file is gone; the registry itself never checks the file exists.
        var goneMissing = Path.Combine(_root, "gone.fwdata");
        registry.Record("workspace-gone", goneMissing, seen);
        Assert.Single(registry.List());

        registry.Forget("workspace-gone");
        Assert.Empty(registry.List());
    }

    [Fact]
    public void ConcurrentUsageAppendsFromTwoConnectionsBothLand()
    {
        using var first = MachineDatabase.Open(_root);
        using var second = MachineDatabase.Open(_root);
        var firstLog = new MachineUsageLog(first);
        var secondLog = new MachineUsageLog(second);

        const int perWriter = 50;
        var writers = new[]
        {
            Task.Run(() => { for (var i = 0; i < perWriter; i++) firstLog.Append(Entry("first", i)); }),
            Task.Run(() => { for (var i = 0; i < perWriter; i++) secondLog.Append(Entry("second", i)); })
        };
        Task.WaitAll(writers);

        var recorded = firstLog.ReadAll();
        Assert.Equal(perWriter * 2, recorded.Count);
        Assert.Equal(perWriter, recorded.Count(entry => entry.Command == "first"));
        Assert.Equal(perWriter, recorded.Count(entry => entry.Command == "second"));
    }

    [Fact]
    public void OpeningAProjectDatabaseAsAMachineDatabaseIsRefused()
    {
        var path = Path.Combine(_root, "motif.db");
        var locator = new ProjectLocator(Path.Combine(_root, "project.fwdata"), "project");
        using (MotifDatabase.OpenOwned(path, locator, MotifSchema.CurrentSchema, new Version(1, 0))) { }

        var refusal = Assert.Throws<InvalidDataException>(() => MachineDatabase.Open(_root));
        // Two databases now refuse in the same words unless each says which one it is.
        Assert.Contains("Motif machine database", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpeningAMachineDatabaseAsAProjectDatabaseIsRefused()
    {
        using (MachineDatabase.Open(_root)) { }

        var path = Path.Combine(_root, "motif.db");
        var locator = new ProjectLocator(Path.Combine(_root, "project.fwdata"), "project");
        var refusal = Assert.Throws<InvalidDataException>(() =>
            MotifDatabase.OpenOwned(path, locator, MotifSchema.CurrentSchema, new Version(1, 0)));
        Assert.Contains("Motif database", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("machine", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static UsageLogEntry Entry(string command, int index) =>
        new(DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ"), command, new[] { $"index:{index}" });

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}
