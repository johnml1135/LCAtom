using SIL.Motif.Contract.Responses;
using SIL.Motif.Projection;
using SIL.Motif.Projection.Rendering;
using SIL.Motif.Tests.TestFixtures;
using Xunit;

namespace SIL.Motif.Tests.Projection;

/// <summary>
/// The one read surface whose projection needs a live cache — <see cref="ProjectSummaryReader"/>
/// reads <c>ILexEntryRepository</c> directly, so it is proved against a real seeded project rather
/// than a hand-built record like the other surfaces in <see cref="ProjectionRenderingTests"/>.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class ProjectSummaryReaderTests
{
    private readonly PristineProjectFixture _pristine;

    public ProjectSummaryReaderTests(PristineProjectFixture pristine) => _pristine = pristine;

    [Fact]
    public void Read_ReportsTheProjectNameAndLexicalEntryCount()
    {
        using var cache = _pristine.NewScratch();

        var projection = ProjectSummaryReader.Read(cache);

        Assert.Equal(cache.ProjectId.Name, projection.ProjectName);
        Assert.Equal(2, projection.LexicalEntryCount); // SeededProject writes exactly two entries.
    }

    [Fact]
    public void Render_And_Json_CarryTheSameFigures()
    {
        using var cache = _pristine.NewScratch();
        var projection = ProjectSummaryReader.Read(cache);

        var text = CommandTextRenderer.Render(projection);
        var json = ProjectionJson.Serialize(projection);

        Assert.Contains(projection.ProjectName, text);
        Assert.Contains(projection.LexicalEntryCount.ToString(), text);
        Assert.Contains(projection.ProjectName, json);
        Assert.Contains(projection.LexicalEntryCount.ToString(), json);
    }
}
