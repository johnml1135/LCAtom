using System.Text.Json;
using SIL.Motif.Contract.Responses;
using Xunit;

namespace SIL.Motif.Tests.Contract;

/// <summary>
/// Deserialises CLI responses using only <c>SIL.Motif.Contract</c>, standing in for the consumer that
/// cannot reference anything else.
/// </summary>
/// <remarks>
/// A FieldWorks surface runs <c>motif --json</c> and binds the result. It may reference Contract and
/// nothing else of Motif's — no Projection, no Host, no LibLCM, no SQLite provider. This file therefore
/// names no other Motif namespace, and that restraint is the assertion: if a response shape ever needs a
/// type from another assembly, this stops compiling rather than failing quietly in a consumer nobody has
/// built yet.
/// </remarks>
public sealed class ResponseBindingTests
{
    private static readonly JsonSerializerOptions ReaderOptions =
        new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void AProposalListBindsFromItsOwnRenderedJson()
    {
        var rendered = ProjectionJson.Serialize(new ProposalListProjection(new[]
        {
            new ProposalListItem("proposal/abc", "proposed", "raise the vowel"),
            new ProposalListItem("proposal/def", "applied", null)
        }));

        var bound = JsonSerializer.Deserialize<ProposalListProjection>(rendered, ReaderOptions);

        Assert.NotNull(bound);
        Assert.Equal(2, bound!.Proposals.Count);
        Assert.Equal("proposal/abc", bound.Proposals[0].ProposalId);
        Assert.Equal("raise the vowel", bound.Proposals[0].Label);
        // An absent label stays absent rather than becoming empty text.
        Assert.Null(bound.Proposals[1].Label);
    }

    [Fact]
    public void AnEffectSetBindsWithItsWritingSystemAlternatives()
    {
        var rendered = ProjectionJson.Serialize(new[]
        {
            new EffectView("lexEntry/1", "Gloss", new[]
            {
                new EffectChange("en", "before", "after"),
                new EffectChange("fr", null, "nouveau")
            })
        });

        var bound = JsonSerializer.Deserialize<IReadOnlyList<EffectView>>(rendered, ReaderOptions);

        var effect = Assert.Single(bound!);
        Assert.Equal("Gloss", effect.Field);
        Assert.Equal(2, effect.Changes.Count);
        Assert.Null(effect.Changes[1].Before);
        Assert.Equal("nouveau", effect.Changes[1].After);
    }

    [Fact]
    public void AProjectSummaryBindsFromCamelCasedJson()
    {
        // The renderer's naming policy is part of the contract: a consumer that guesses wrong reads nulls.
        var rendered = ProjectionJson.Serialize(new ProjectSummaryProjection("Sena 3", 42));

        Assert.Contains("\"projectName\"", rendered, StringComparison.Ordinal);
        var bound = JsonSerializer.Deserialize<ProjectSummaryProjection>(rendered, ReaderOptions);

        Assert.Equal("Sena 3", bound!.ProjectName);
        Assert.Equal(42, bound.LexicalEntryCount);
    }

    [Fact]
    public void AnUnknownFieldIsIgnoredRatherThanRefused()
    {
        // Additive evolution is the versioning rule; a consumer built against an older shape must survive.
        const string withFutureField =
            "{\"projectName\":\"Sena 3\",\"lexicalEntryCount\":1,\"somethingAddedLater\":true}";

        var bound = JsonSerializer.Deserialize<ProjectSummaryProjection>(withFutureField, ReaderOptions);

        Assert.Equal("Sena 3", bound!.ProjectName);
    }
}
