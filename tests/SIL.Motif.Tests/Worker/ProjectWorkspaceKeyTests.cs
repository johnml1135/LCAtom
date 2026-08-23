using System;
using System.Linq;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Worker.Projects;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class ProjectWorkspaceKeyTests
{
    [Fact]
    public void Compute_NormalizesWindowsPathSeparatorsCaseAndTrailingSeparator()
    {
        var first = ProjectWorkspaceKey.Compute(new ProjectLocator(
            @"C:\Projects\Lang\Lang.fwdata", "project-1"));
        var second = ProjectWorkspaceKey.Compute(new ProjectLocator(
            @"c:/projects/lang/LANG.fwdata\", "project-1"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_RejectsRelativeAndDriveRelativePaths()
    {
        Assert.Throws<ArgumentException>(() =>
            new ProjectLocator(@".\relative\Lang.fwdata", "project-1"));
        Assert.Throws<ArgumentException>(() =>
            new ProjectLocator(@"C:relative\Lang.fwdata", "project-1"));
    }

    [Fact]
    public void Compute_NormalizesFullyQualifiedUncPath()
    {
        var first = ProjectWorkspaceKey.Compute(new ProjectLocator(
            @"\\Server\Share\Lang.fwdata", "project-1"));
        var second = ProjectWorkspaceKey.Compute(new ProjectLocator(
            @"//server/share/LANG.fwdata\", "project-1"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_DistinguishesSameIdentityAtDifferentPaths()
    {
        var first = ProjectWorkspaceKey.Compute(new ProjectLocator(@"C:\Projects\One.fwdata", "project-1"));
        var second = ProjectWorkspaceKey.Compute(new ProjectLocator(@"C:\Projects\Two.fwdata", "project-1"));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Compute_DistinguishesDifferentIdentityAtTheSamePath()
    {
        var first = ProjectWorkspaceKey.Compute(new ProjectLocator(@"C:\Projects\Lang.fwdata", "project-1"));
        var second = ProjectWorkspaceKey.Compute(new ProjectLocator(@"C:\Projects\Lang.fwdata", "project-2"));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Compute_PreservesOpaqueUnicodeIdentityAndTupleOrder()
    {
        var composed = ProjectWorkspaceKey.Compute(new ProjectLocator(@"C:\Projects\Lang.fwdata", "é"));
        var decomposed = ProjectWorkspaceKey.Compute(new ProjectLocator(@"C:\Projects\Lang.fwdata", "e\u0301"));
        var reversed = ProjectWorkspaceKey.Compute(new ProjectLocator(@"C:\Projects\é.fwdata", "e\u0301"));

        Assert.NotEqual(composed, decomposed);
        Assert.NotEqual(decomposed, reversed);
    }

    [Fact]
    public void Compute_ReturnsCanonicalSha256Value()
    {
        var key = ProjectWorkspaceKey.Compute(new ProjectLocator(@"C:\Projects\Lang.fwdata", "project-1"));

        Assert.Matches("^sha256:[0-9a-f]{64}$", key);
        Assert.Equal(
            "sha256:ae976446c71d46154d9ddfd3d1a7efe0dfcd57650637f77d688c452fe6ea0003",
            ProjectWorkspaceKey.Compute(new ProjectLocator(@"C:\Projects\Lang.fwdata", "project-1")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Compute_RejectsMissingPath(string? path)
    {
        Assert.ThrowsAny<System.ArgumentException>(() =>
            new ProjectLocator(path!, "project-1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Compute_RejectsMissingOpaqueIdentity(string? identity)
    {
        Assert.ThrowsAny<System.ArgumentException>(() =>
            new ProjectLocator(@"C:\Projects\Lang.fwdata", identity!));
    }

    [Fact]
    public void CanonicalBytes_UseOrderedLengthFramedUtf8Tuple()
    {
        var bytes = ProjectWorkspaceKey.CanonicalBytes(
            new ProjectLocator(@"C:\Projects\Lang.fwdata", "project-1"));

        Assert.Equal(new byte[] { 0, 0, 0, 23 }, bytes.Take(4));
        Assert.Equal(new byte[] { 0, 0, 0, 9 }, bytes.Skip(27).Take(4));
    }
}
