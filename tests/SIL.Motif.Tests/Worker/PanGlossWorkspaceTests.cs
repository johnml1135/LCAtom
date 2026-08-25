using SIL.Motif.Tests.TestFixtures;
using SIL.Motif.Worker.PanGloss;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class PanGlossWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-pangloss-" + Guid.NewGuid().ToString("N"));
    private readonly IWorkspaceOwnership _ownership;

    public PanGlossWorkspaceTests()
    {
        Directory.CreateDirectory(_root);
        _ownership = WorkspaceOwnership.Bootstrap(_root);
    }

    [Fact]
    public void CompleteAndDelete_OnSuccess_RemovesTheWholeTreeAndItsMarker()
    {
        var workspace = PanGlossWorkspace.Create(_ownership, "success-job");
        Directory.CreateDirectory(Path.Combine(workspace.Root, "nested"));
        File.WriteAllText(Path.Combine(workspace.Root, "candidate.tsv"), "candidate");
        File.WriteAllText(Path.Combine(workspace.Root, "nested", "output.txt"), "output");
        var markerPath = Path.Combine(_root, "pangloss", "markers", "success-job.marker");
        Assert.True(File.Exists(markerPath));

        workspace.CompleteAndDelete();

        Assert.False(Directory.Exists(workspace.Root));
        Assert.False(File.Exists(markerPath));
        var work = Path.Combine(_root, "pangloss", "work");
        Assert.True(Directory.Exists(work));
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    [Fact]
    public void CompleteAndDelete_AfterAssessmentToolFailure_RemovesPartialEngineOutput()
    {
        var workspace = PanGlossWorkspace.Create(_ownership, "failure-job");
        var engineTemp = Path.Combine(workspace.Root, "engine-temp");
        Directory.CreateDirectory(engineTemp);
        File.WriteAllText(Path.Combine(engineTemp, "trace.log"), "partial");
        File.WriteAllText(Path.Combine(workspace.Root, "assessment.partial"), "broken");

        workspace.CompleteAndDelete();

        Assert.False(Directory.Exists(workspace.Root));
        Assert.False(Directory.Exists(engineTemp));
    }

    [Fact]
    public void CompleteAndDelete_AfterCancellation_RemovesAnInProgressExport()
    {
        var workspace = PanGlossWorkspace.Create(_ownership, "cancelled-job");
        var exportInProgress = Path.Combine(workspace.Root, "export");
        Directory.CreateDirectory(exportInProgress);
        File.WriteAllText(Path.Combine(exportInProgress, "half-written.fwdata"), "incomplete");

        workspace.CompleteAndDelete();

        Assert.False(Directory.Exists(workspace.Root));
        Assert.False(Directory.Exists(exportInProgress));
    }

    [Fact]
    public void SweepStartup_RemovesAWorkspaceOrphanedByAWorkerCrash()
    {
        var workspace = PanGlossWorkspace.Create(_ownership, "crashed-job");
        Directory.CreateDirectory(Path.Combine(workspace.Root, "engine-temp"));
        File.WriteAllText(Path.Combine(workspace.Root, "candidate.tsv"), "candidate");
        // No CompleteAndDelete/Dispose call: this stands in for the process dying mid-attempt.

        var result = PanGlossWorkspace.SweepStartup(_ownership);

        Assert.Contains(workspace.Root, result.DeletedPaths);
        Assert.Empty(result.Failures);
        Assert.False(Directory.Exists(workspace.Root));
        Assert.False(File.Exists(Path.Combine(_root, "pangloss", "markers", "crashed-job.marker")));
    }

    [Fact]
    public void SweepStartup_LeavesAnUnmarkedEntryAloneAndReportsIt()
    {
        var work = Path.Combine(_root, "pangloss", "work");
        var stray = Path.Combine(work, "not-ours");
        Directory.CreateDirectory(stray);
        File.WriteAllText(Path.Combine(stray, "sentinel.txt"), "keep");

        var result = PanGlossWorkspace.SweepStartup(_ownership);

        Assert.True(Directory.Exists(stray));
        Assert.True(File.Exists(Path.Combine(stray, "sentinel.txt")));
        Assert.Empty(result.DeletedPaths);
    }

    [Fact]
    public void CompleteAndDelete_WithALockedFile_LeavesTheMarkerForARetryInsteadOfThrowing()
    {
        var workspace = PanGlossWorkspace.Create(_ownership, "locked-job");
        var lockedFile = Path.Combine(workspace.Root, "locked.bin");
        File.WriteAllText(lockedFile, "data");
        var markerPath = Path.Combine(_root, "pangloss", "markers", "locked-job.marker");

        var lockStream = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            var exception = Record.Exception(() => workspace.CompleteAndDelete());
            Assert.Null(exception);
            Assert.True(Directory.Exists(workspace.Root));
            Assert.True(File.Exists(lockedFile));
            Assert.True(File.Exists(markerPath));
        }
        finally
        {
            lockStream.Dispose();
        }

        var retry = PanGlossWorkspace.SweepStartup(_ownership);

        Assert.Contains(workspace.Root, retry.DeletedPaths);
        Assert.Empty(retry.Failures);
        Assert.False(Directory.Exists(workspace.Root));
        Assert.False(File.Exists(markerPath));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..\\..\\evil")]
    [InlineData("C:evil")]
    public void Create_RejectsAMaliciousOrUnsafeNameWithoutCreatingAnything(string name)
    {
        Assert.Throws<ArgumentException>(() => PanGlossWorkspace.Create(_ownership, name));

        var work = Path.Combine(_root, "pangloss", "work");
        if (Directory.Exists(work))
            Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    [RequiresSymbolicLinkFact]
    public void Create_RefusesAPreExistingReparsePointAtTheWorkspacePath()
    {
        var outside = Path.Combine(_root, "outside-target");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "do-not-touch");
        var work = Path.Combine(_root, "pangloss", "work");
        Directory.CreateDirectory(work);
        var link = Path.Combine(work, "escape-job");
        Directory.CreateSymbolicLink(link, outside);

        Assert.Throws<ArgumentException>(() => PanGlossWorkspace.Create(_ownership, "escape-job"));

        Assert.True(File.Exists(sentinel));
        Assert.Equal("do-not-touch", File.ReadAllText(sentinel));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (DirectoryNotFoundException) { }
    }
}
