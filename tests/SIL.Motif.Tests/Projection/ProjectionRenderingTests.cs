using SIL.Motif.Contract.Responses;
using System.Collections.Generic;
using SIL.Motif.Projection;
using SIL.Motif.Projection.Rendering;
using Xunit;

namespace SIL.Motif.Tests.Projection;

/// <summary>
/// Every read-surface projection, rendered to text and to JSON from hand-built data — no live
/// project needed, because a projection is a plain record and rendering is a pure function of it
/// (ADR 0021 decision 2). Each test pins that the text carries no figure the JSON lacks, and that
/// rendering the same projection twice produces byte-identical text.
/// </summary>
public sealed class ProjectionRenderingTests
{
    [Fact]
    public void ProposalList_TextFiguresAllAppearInJson()
    {
        var projection = new ProposalListProjection(new[]
        {
            new ProposalListItem("agent_AAECAwQFBgcICQoLDA0ODw", "proposed", "First label"),
            new ProposalListItem("agent_AQIDBAUGBwgJCgsMDQ4PEA", "applied", null),
        });

        var text = CommandTextRenderer.Render(projection);
        var json = ProjectionJson.Serialize(projection);

        Assert.Contains("agent_AAECAwQFBgcICQoLDA0ODw", text);
        Assert.Contains("proposed", text);
        FigureAudit.AssertEveryTextFigureAppearsInJson(text, json);
    }

    [Fact]
    public void ProposalList_EmptyStoreRendersWithNoFiguresToLose()
    {
        var projection = new ProposalListProjection(System.Array.Empty<ProposalListItem>());
        var text = CommandTextRenderer.Render(projection);
        Assert.Equal("No proposals in store." + System.Environment.NewLine, text);
    }

    [Fact]
    public void ProposalDetail_TextFiguresAllAppearInJson()
    {
        var projection = new ProposalDetailProjection(
            ProposalId: "agent_AAECAwQFBgcICQoLDA0ODw",
            Status: "proposed",
            Label: "Revise gloss",
            Comment: "because the old one was wrong",
            CurrentIntentDigest: "sha256:" + new string('a', 64),
            Operations: new[]
            {
                new ProposalOperationView(
                    OperationId: "agent_AQIDBAUGBwgJCgsMDQ4PEA",
                    Kind: "lexical/lexSense/setGloss",
                    Target: "agent_AgMEBQYHCAkKCwwNDg8QEQ",
                    EntityId: null,
                    DependsOn: new[] { "agent_AwQFBgcICQoLDA0ODxAREg" },
                    AfterJson: "{\"ws\":\"en\",\"text\":\"move quickly\"}"),
            });

        var text = CommandTextRenderer.Render(projection);
        var json = ProjectionJson.Serialize(projection);

        Assert.Contains(projection.ProposalId, text);
        Assert.Contains("agent_AgMEBQYHCAkKCwwNDg8QEQ", text); // the target id
        FigureAudit.AssertEveryTextFigureAppearsInJson(text, json);
    }

    [Fact]
    public void ProposalDetail_WithADecision_TextFiguresAllAppearInJson()
    {
        var projection = new ProposalDetailProjection(
            ProposalId: "agent_AAECAwQFBgcICQoLDA0ODw",
            Status: "approved",
            Label: "Revise gloss",
            Comment: "because the old one was wrong",
            CurrentIntentDigest: "sha256:" + new string('a', 64),
            Operations: System.Array.Empty<ProposalOperationView>(),
            Decision: new DecisionView("approved", "human", "a-linguist", "looks correct", "20260101T000000Z"));

        var text = CommandTextRenderer.Render(projection);
        var json = ProjectionJson.Serialize(projection);

        Assert.Contains("approved", text);
        Assert.Contains("a-linguist", text);
        FigureAudit.AssertEveryTextFigureAppearsInJson(text, json);
    }

    [Fact]
    public void ProposalDetail_SupersededBy_TextFiguresAllAppearInJson()
    {
        var projection = new ProposalDetailProjection(
            ProposalId: "agent_AAECAwQFBgcICQoLDA0ODw",
            Status: "superseded",
            Label: null,
            Comment: null,
            CurrentIntentDigest: "sha256:" + new string('a', 64),
            Operations: System.Array.Empty<ProposalOperationView>(),
            SupersededBy: "agent_AQIDBAUGBwgJCgsMDQ4PEA");

        var text = CommandTextRenderer.Render(projection);
        var json = ProjectionJson.Serialize(projection);

        Assert.Contains("agent_AQIDBAUGBwgJCgsMDQ4PEA", text);
        FigureAudit.AssertEveryTextFigureAppearsInJson(text, json);
    }

    [Fact]
    public void ProposalDetail_WithExtensions_TextFiguresAllAppearInJson()
    {
        var projection = new ProposalDetailProjection(
            ProposalId: "agent_AAECAwQFBgcICQoLDA0ODw",
            Status: "proposed",
            Label: null,
            Comment: null,
            CurrentIntentDigest: "sha256:" + new string('a', 64),
            Operations: System.Array.Empty<ProposalOperationView>(),
            ExtensionsJson: """{"promotions":[{"corpusId":"wiki-testlang","licence":"CC-BY-SA-4.0"}]}""");

        var text = CommandTextRenderer.Render(projection);
        var json = ProjectionJson.Serialize(projection);

        Assert.Contains("wiki-testlang", text);
        Assert.Contains("CC-BY-SA-4.0", text);
        FigureAudit.AssertEveryTextFigureAppearsInJson(text, json);
    }

    [Fact]
    public void AppliedLog_TextFiguresAllAppearInJson()
    {
        var projection = new AppliedLogProjection(
            ProjectPath: @"C:\projects\sample.fwdata",
            EntryCount: 1,
            Entries: new[]
            {
                new AppliedLogEntryView(
                    "8f14e45f-ceea-467e-9432-a0eb92c2a92a", "20260101T000000Z", "an-agent",
                    "deadbeef00112233445566778899aabbccddeeff0011223344556677889900",
                    "revised the gloss"),
            },
            Diagnostics: System.Array.Empty<string>());

        var text = CommandTextRenderer.Render(projection);
        var json = ProjectionJson.Serialize(projection);

        Assert.Contains("1 Motif entry", text);
        Assert.Contains("an-agent", text);
        // Single-quoted in the renderer, so FigureAudit's own sweep (double-quoted/bare tokens) skips these.
        Assert.Contains("an-agent", json);
        Assert.Contains("sample.fwdata", json);
        FigureAudit.AssertEveryTextFigureAppearsInJson(text, json);
    }

    [Fact]
    public void AppliedLog_PluralizesWhenNotExactlyOne()
    {
        var zero = new AppliedLogProjection("p", 0, System.Array.Empty<AppliedLogEntryView>(), System.Array.Empty<string>());
        var two = new AppliedLogProjection(
            "p", 2,
            new[]
            {
                new AppliedLogEntryView("a", "t", "u", "d", "x"),
                new AppliedLogEntryView("b", "t", "u", "d", "x"),
            },
            System.Array.Empty<string>());

        Assert.Contains("0 Motif entries", CommandTextRenderer.Render(zero));
        Assert.Contains("2 Motif entries", CommandTextRenderer.Render(two));
    }

    [Fact]
    public void DryRun_TextFiguresAllAppearInJson()
    {
        var projection = new DryRunProjection(
            ProposalId: "agent_AAECAwQFBgcICQoLDA0ODw",
            IntentDigest: "sha256:" + new string('1', 64),
            BaselineNote: "footprint-scoped baseline",
            Effects: new[]
            {
                new EffectView(
                    "agent_AgMEBQYHCAkKCwwNDg8QEQ",
                    "lexical/sense/gloss",
                    new[] { new EffectChange("en", "run quickly", "move quickly") }),
            },
            EffectDigest: "sha256:" + new string('2', 64),
            FootprintDigest: "sha256:" + new string('3', 64));

        var text = CommandTextRenderer.Render(projection);
        var json = ProjectionJson.Serialize(projection);

        Assert.Contains("\"run quickly\" -> \"move quickly\"", text);
        Assert.Contains("effectDigest: sha256:", text);
        FigureAudit.AssertEveryTextFigureAppearsInJson(text, json);
    }

    [Fact]
    public void DryRun_RenderingIsPureAndDeterministic()
    {
        var projection = new DryRunProjection(
            "agent_AAECAwQFBgcICQoLDA0ODw", "sha256:" + new string('1', 64), "note",
            new[] { new EffectView("agent_id", "field", System.Array.Empty<EffectChange>()) },
            "sha256:" + new string('2', 64), "sha256:" + new string('3', 64));

        var first = CommandTextRenderer.Render(projection);
        var second = CommandTextRenderer.Render(projection);

        Assert.Equal(first, second);
        Assert.Contains("(no observable before/after change)", first);
    }

    [Fact]
    public void Apply_AlreadyApplied_TextFiguresAllAppearInJson()
    {
        var projection = new ApplyProjection(
            ProposalId: "agent_AAECAwQFBgcICQoLDA0ODw",
            AlreadyApplied: true,
            ResultNote: "already applied at an earlier time",
            Effects: System.Array.Empty<EffectView>(),
            EffectDigest: "sha256:" + new string('4', 64),
            AppliedLogEntry: new AppliedLogEntrySummary(
                "8f14e45f-ceea-467e-9432-a0eb92c2a92a", "20260101T000000Z", "an-agent",
                "deadbeef00112233445566778899aabbccddeeff0011223344556677889900"));

        var text = CommandTextRenderer.Render(projection);
        var json = ProjectionJson.Serialize(projection);

        Assert.Contains("was already applied", text);
        Assert.Contains("an-agent", json); // single-quoted in the renderer; FigureAudit's sweep skips it.
        FigureAudit.AssertEveryTextFigureAppearsInJson(text, json);
    }

    [Fact]
    public void Apply_Fresh_TextFiguresAllAppearInJson()
    {
        var projection = new ApplyProjection(
            ProposalId: "agent_AAECAwQFBgcICQoLDA0ODw",
            AlreadyApplied: false,
            ResultNote: "applied 1 operation",
            Effects: new[]
            {
                new EffectView(
                    "agent_AgMEBQYHCAkKCwwNDg8QEQ",
                    "lexical/sense/gloss",
                    new[] { new EffectChange("en", "run quickly", "move quickly") }),
            },
            EffectDigest: "sha256:" + new string('5', 64),
            AppliedLogEntry: new AppliedLogEntrySummary(
                "8f14e45f-ceea-467e-9432-a0eb92c2a92a", "20260101T000000Z", "an-agent",
                "deadbeef00112233445566778899aabbccddeeff0011223344556677889900"));

        var text = CommandTextRenderer.Render(projection);
        var json = ProjectionJson.Serialize(projection);

        Assert.Contains("Applied Proposal", text);
        Assert.Contains("\"run quickly\" -> \"move quickly\"", text);
        Assert.Contains("an-agent", json); // single-quoted in the renderer; FigureAudit's sweep skips it.
        FigureAudit.AssertEveryTextFigureAppearsInJson(text, json);
    }

    [Fact]
    public void Apply_RenderingIsPureAndDeterministic()
    {
        var projection = new ApplyProjection(
            "agent_AAECAwQFBgcICQoLDA0ODw", false, "note",
            new[] { new EffectView("agent_id", "field", System.Array.Empty<EffectChange>()) },
            "sha256:" + new string('5', 64),
            new AppliedLogEntrySummary("id", "ts", "user", "digest"));

        Assert.Equal(CommandTextRenderer.Render(projection), CommandTextRenderer.Render(projection));
    }
}
