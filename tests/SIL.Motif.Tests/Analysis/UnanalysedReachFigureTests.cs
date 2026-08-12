using SIL.Motif.Host.Analysis;
using Xunit;

namespace SIL.Motif.Tests.Analysis;

/// <summary>
/// <see cref="UnanalysedReachFigure"/> — ADR 0038 decision 7's one counted figure about word forms
/// nobody has analysed, and the caveat that must travel with it: it is evidence about reach, never about
/// correctness, because a rising figure is equally consistent with the grammar improving and with it
/// getting looser.
/// </summary>
/// <remarks>
/// Rules defended here, in order of how much damage getting them wrong would do:
/// <list type="number">
/// <item>The caveat is stated by the type itself, unconditionally — there is no way to render this
/// figure as prose without it, so a caller cannot quote a bare number.</item>
/// <item>The caveat holds regardless of the values: a figure at 0, at its maximum, or anywhere between
/// carries the identical warning, because the warning is about what the number can support, not about
/// its magnitude.</item>
/// </list>
/// </remarks>
public class UnanalysedReachFigureTests
{
    [Fact]
    public void Describe_AlwaysStatesTheReachNotCorrectnessCaveat()
    {
        var figure = new UnanalysedReachFigure(UnanalysedCount: 40, ParsedCount: 25);

        var sentence = figure.Describe();

        Assert.Contains("reach, not correctness", sentence);
        Assert.Contains("improving", sentence);
        Assert.Contains("looser", sentence);
        Assert.Contains("25", sentence);
        Assert.Contains("40", sentence);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 0)]
    [InlineData(10, 10)]
    public void Describe_CarriesTheCaveat_NoMatterWhatTheNumbersAre(int unanalysed, int parsed)
    {
        var figure = new UnanalysedReachFigure(unanalysed, parsed);

        // The warning does not weaken or vanish at the extremes - it is unconditional (ADR 0038 decision 7).
        Assert.Contains("reach, not correctness", figure.Describe());
    }
}
