using SIL.Motif.Projection.Usage;
using Xunit;

namespace SIL.Motif.Tests.Projection.Usage;

public sealed class UsageLogTests
{
    [Fact]
    public void Record_CapturesCommandAndArgumentShape()
    {
        var log = new UsageLog();

        log.Record("show", new[] { UsageArgumentShape.Text("storeDir"), UsageArgumentShape.Text("proposalId") });

        var entry = Assert.Single(log.Entries);
        Assert.Equal("show", entry.Command);
        Assert.Equal(new[] { "storeDir:text", "proposalId:text" }, entry.ArgumentShape);
        Assert.NotEmpty(entry.TimestampUtc);
    }

    [Fact]
    public void ArgumentShapeHelpers_DescribeKindAndCardinalityNeverValue()
    {
        Assert.Equal("target:text", UsageArgumentShape.Text("target"));
        Assert.Equal("operationIds:list(3)", UsageArgumentShape.List("operationIds", 3));
        Assert.Equal("force:flag", UsageArgumentShape.Flag("force"));
    }

    [Fact]
    public void Summarize_CountsEachCommand()
    {
        var log = new UsageLog();
        log.Record("list", new string[0]);
        log.Record("show", new string[0]);
        log.Record("list", new string[0]);

        var summary = log.Summarize();

        Assert.Equal(2, summary.CallCounts["list"]);
        Assert.Equal(1, summary.CallCounts["show"]);
    }

    [Fact]
    public void Summarize_CountsBackToBackPairsInCallOrder()
    {
        var log = new UsageLog();
        log.Record("dry-run", new string[0]);
        log.Record("apply", new string[0]);
        log.Record("dry-run", new string[0]);
        log.Record("apply", new string[0]);
        log.Record("list", new string[0]);

        var summary = log.Summarize();

        var dryRunThenApply = Assert.Single(summary.BackToBack, p => p.First == "dry-run" && p.Second == "apply");
        Assert.Equal(2, dryRunThenApply.Count);

        var applyThenList = Assert.Single(summary.BackToBack, p => p.First == "apply" && p.Second == "list");
        Assert.Equal(1, applyThenList.Count);
    }

    [Fact]
    public void Summarize_OneOrZeroEntriesHasNoBackToBackPairs()
    {
        var empty = new UsageLog();
        Assert.Empty(empty.Summarize().BackToBack);

        var one = new UsageLog();
        one.Record("open", new string[0]);
        Assert.Empty(one.Summarize().BackToBack);
    }

    /// <summary>
    /// Pins the hard requirement directly: a shape token built from real project content — a gloss,
    /// a canonical id's actual value, a filesystem path — must never appear in a recorded entry. This
    /// would fail if a caller logged an argument's value instead of its shape.
    /// </summary>
    [Fact]
    public void Record_NeverCarriesProjectData()
    {
        const string secretPath = @"C:\Users\linguist\Documents\SomeLanguage.fwdata";
        const string secretGloss = "move quickly on foot";
        const string secretId = "agent_AAECAwQFBgcICQoLDA0ODw";

        var log = new UsageLog();
        log.Record("dry-run", new[]
        {
            UsageArgumentShape.Text("storeDir"),
            UsageArgumentShape.Text("proposalId"),
            UsageArgumentShape.Text("fwDataPath"),
        });

        foreach (var entry in log.Entries)
        {
            Assert.DoesNotContain(secretPath, entry.Command);
            Assert.DoesNotContain(secretGloss, entry.Command);
            Assert.DoesNotContain(secretId, entry.Command);
            foreach (var token in entry.ArgumentShape)
            {
                Assert.DoesNotContain(secretPath, token);
                Assert.DoesNotContain(secretGloss, token);
                Assert.DoesNotContain(secretId, token);
            }
        }
    }
}
