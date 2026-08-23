using System;
using SIL.Motif.Contract.Projects;
using Xunit;

namespace SIL.Motif.Tests.Contract;

public sealed class ProjectLocatorTests
{
    [Theory]
    [InlineData(@"C:\Projects\Lang.fwdata", @"C:/Projects/Lang.fwdata")]
    [InlineData(@"C:\Projects\.\Lang.fwdata", @"C:\Projects\Lang.fwdata")]
    [InlineData(@"\\server\share\Projects\Lang.fwdata", "//server/share/Projects/Lang.fwdata")]
    public void EquivalentWindowsPathsCreateEqualCanonicalLocators(string firstPath, string secondPath)
    {
        var first = new ProjectLocator(firstPath, "fw-id");
        var second = new ProjectLocator(secondPath, "fw-id");

        Assert.Equal(first, second);
        Assert.Equal(first.FullFwDataPath, second.FullFwDataPath);
    }

    [Theory]
    [InlineData(@".\Lang.fwdata")]
    [InlineData(@"C:relative\Lang.fwdata")]
    [InlineData(@"C:\Projects\Lang.fwdata\")]
    [InlineData(@"C:\Projects\Lang.txt")]
    [InlineData(@"\\server\Lang.fwdata")]
    public void InvalidDirectoryLikePathsAreRejected(string path)
    {
        Assert.Throws<ArgumentException>(() => new ProjectLocator(path, "fw-id"));
    }

    [Theory]
    [InlineData(@"C:\Projects\One\..\Lang.fwdata", @"C:\Projects\Lang.fwdata")]
    [InlineData(@"\\server\share\Projects\..\Lang.fwdata", @"\\server\share\Lang.fwdata")]
    [InlineData(@"C:\Projects\\Sub\Lang.fwdata", @"C:\Projects\Sub\Lang.fwdata")]
    public void LexicallyNormalizesSegmentsAndSeparators(string path, string expected)
    {
        var locator = new ProjectLocator(path, "fw-id");

        Assert.Equal(expected, locator.FullFwDataPath);
        Assert.DoesNotContain('/', locator.FullFwDataPath);
    }

    [Theory]
    [InlineData(@"C:\..\Lang.fwdata")]
    [InlineData(@"\\server\share\..\Lang.fwdata")]
    [InlineData(@"C:\Projects\.fwdata")]
    public void RejectsRootTraversalAndEmptyFilenameStem(string path)
    {
        Assert.Throws<ArgumentException>(() => new ProjectLocator(path, "fw-id"));
    }
}
