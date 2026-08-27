using SIL.Motif.Host.Parser;
using SIL.Motif.Tests.TestFixtures;
using Xunit;

namespace SIL.Motif.Tests.Parser;

/// <summary>
/// Drives the real <see cref="PanGlossAssessmentProcess"/> against a fake parser executable.
/// </summary>
/// <remarks>
/// These cover the process boundary itself rather than a substitute for it: argument building, draining
/// both streams before waiting, exit codes, cancellation, and report parsing all run for real. What is
/// fake is only the parser on the far side, which is what lets the failure paths be covered at all —
/// a parser that exits without writing, or writes something unreadable, is not a state a real grammar
/// can be asked to produce on demand.
/// </remarks>
public sealed class FakeParserSeamTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-fake-parser-" + Guid.NewGuid().ToString("N"));

    public FakeParserSeamTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AnAssessmentRoundTripsThroughTheRealProcessBoundary()
    {
        var candidate = Candidate("ok");

        var report = await Run(candidate);

        Assert.Equal("foma-confirm", report.Pipeline);
        var word = Assert.Single(report.Words);
        Assert.Equal("motifa", word.Word);
        // Interned keys resolved through the report's own table, which is the whole point of reading one.
        Assert.Equal("11111111-1111-1111-1111-111111111111", Assert.Single(word.Analyses).MorphemeGuids[0]);
    }

    [Fact]
    public async Task TheParserIsHandedTheExportedGrammarSourceRatherThanTheDirectory()
    {
        var candidate = Candidate("names-source");

        var report = await Run(candidate);

        // The fake echoes what it was given; a directory here would mean the dispatch never found the file.
        Assert.NotEmpty(report.OutcomeDigest);
    }

    [Fact]
    public async Task WhateverPipelineTheParserReportsIsWhatMotifStores()
    {
        var candidate = Candidate("pipeline");
        FakeParser.Behave(candidate, new { pipeline = "fst-only" });

        var report = await Run(candidate);

        // Motif sends no mode and records the one it was given; it must not substitute a preferred value.
        Assert.Equal("fst-only", report.Pipeline);
    }

    [Fact]
    public async Task AParserThatExitsWithoutWritingIsRefusedRatherThanReadAsEmpty()
    {
        var candidate = Candidate("no-report");
        FakeParser.Behave(candidate, new { mode = "noReport" });

        var failure = await Record.ExceptionAsync(() => Run(candidate));

        // A clean exit code with no report is the shape most easily mistaken for "the grammar parsed nothing".
        Assert.IsType<ParserUnavailableException>(failure);
    }

    [Fact]
    public async Task AParserThatFailsIsDistinguishedFromOneThatParsedNothing()
    {
        var candidate = Candidate("fail");
        FakeParser.Behave(candidate, new { mode = "fail", exitCode = 3, standardError = "grammar exploded" });

        var failure = await Record.ExceptionAsync(() => Run(candidate));

        Assert.NotNull(failure);
        Assert.Contains("grammar exploded", failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableReportIsRefusedRatherThanPartlyBelieved()
    {
        var candidate = Candidate("malformed");
        FakeParser.Behave(candidate, new { mode = "malformedReport" });

        var failure = await Record.ExceptionAsync(() => Run(candidate));

        Assert.NotNull(failure);
    }

    [Fact]
    public async Task DiagnosticsTravelWithTheReportRatherThanBeingDropped()
    {
        var candidate = Candidate("diagnostics");
        FakeParser.Behave(candidate, new { diagnosticCount = 137 });

        var report = await Run(candidate);

        // A coverage figure computed while these were ignored is not a figure about the grammar.
        Assert.Equal(137, report.DiagnosticCount);
    }

    [Fact]
    public async Task CancellingTheAssessmentStopsTheParserProcess()
    {
        var candidate = Candidate("slow");
        var heartbeat = Path.Combine(_root, "heartbeat.txt");
        FakeParser.Behave(candidate, new { heartbeatPath = heartbeat });
        using var cancellation = new CancellationTokenSource();

        var run = Run(candidate, cancellation.Token);
        for (var i = 0; i < 200 && !File.Exists(heartbeat); i++) await Task.Delay(25);
        Assert.True(File.Exists(heartbeat), "The fake parser never started ticking.");
        await cancellation.CancelAsync();
        await Record.ExceptionAsync(() => run);

        var stoppedAt = File.ReadAllText(heartbeat);
        await Task.Delay(300);
        // Still ticking here would mean cancellation abandoned the process rather than killing it.
        Assert.Equal(stoppedAt, File.ReadAllText(heartbeat));
    }

    private static Task<AssessReport> Run(string candidate, CancellationToken cancellationToken = default) =>
        new PanGlossAssessmentProcess(FakeParser.ExecutablePath).RunAsync(candidate, cancellationToken);

    private string Candidate(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "candidate.fwdata"), "the fake parser never reads this.");
        return directory;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}
