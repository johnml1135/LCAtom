using System.Linq;
using Microsoft.Data.Sqlite;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Store;

public sealed class AssessmentRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-assessments-" + Guid.NewGuid().ToString("N"));

    public AssessmentRepositoryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void RecordsAnAssessmentWithWordsAndAnalysesAndReadsItBackById()
    {
        var repository = NewRepository("record.fwdata", out _);
        var assessment = NewAssessment("a1", proposalId: null, proposalIntentDigest: null, kind: "parse");

        repository.Record(assessment);
        var result = repository.Get("a1");

        Assert.Equal("a1", result.AssessmentId);
        Assert.Null(result.ProposalId);
        Assert.Null(result.ProposalIntentDigest);
        Assert.Equal("pangloss", result.Assessor);
        Assert.Equal("parse", result.Kind);
        Assert.Equal("{\"words\":\"all\"}", result.ScopeJson);
        Assert.Equal("sha256:scope", result.ScopeDigest);
        Assert.Equal("whitespace", result.TokeniserName);
        Assert.Equal("1", result.TokeniserVersion);
        Assert.Equal("{\"baseline\":true}", result.BaselineToken);
        Assert.NotNull(result.Words);
        Assert.Equal(2, result.Words!.Count);
        Assert.Equal("bo", result.Words[0].Word);
        Assert.Single(result.Words[0].Analyses);
        Assert.Equal("guid-root", result.Words[0].Analyses[0].MorphemeGuids[0]);
        Assert.Empty(result.Words[1].Analyses);
    }

    [Fact]
    public void GetThrowsForAnUnrecordedAssessment()
    {
        var repository = NewRepository("missing.fwdata", out _);
        Assert.Throws<KeyNotFoundException>(() => repository.Get("nope"));
    }

    [Fact]
    public void ListsByProposalAndByKind()
    {
        var repository = NewRepository("lists.fwdata", out var database);
        var proposal1 = CanonicalId.Mint("proposal/");
        var proposal2 = CanonicalId.Mint("proposal/");
        SeedProposal(database, proposal1.Value);
        SeedProposal(database, proposal2.Value);

        repository.Record(NewAssessment("parse-1", proposal1, "sha256:intent1", "parse"));
        repository.Record(NewAssessment("parse-2", proposal2, "sha256:intent2", "parse"));
        repository.Record(NewAssessment("size-1", proposal1, "sha256:intent1", "engine-size"));

        var byProposal = repository.ListByProposal(proposal1);
        Assert.Equal(2, byProposal.Count);
        Assert.All(byProposal, record => Assert.Equal(proposal1, record.ProposalId));
        Assert.All(byProposal, record => Assert.Null(record.Words));

        var byKind = repository.ListByKind("parse");
        Assert.Equal(2, byKind.Count);
        Assert.All(byKind, record => Assert.Equal("parse", record.Kind));
        Assert.Empty(repository.ListByKind("difference"));
    }

    [Fact]
    public void PromotionMovesThePointerAndReadingCurrentReturnsWhatWasPromoted()
    {
        var repository = NewRepository("promote.fwdata", out _);
        Assert.Null(repository.GetCurrent());
        repository.Record(NewAssessment("candidate-1", null, null, "parse"));
        repository.Record(NewAssessment("candidate-2", null, null, "parse"));

        repository.PromoteToCurrent("candidate-1");
        Assert.Equal("candidate-1", repository.GetCurrent()!.AssessmentId);

        repository.PromoteToCurrent("candidate-2");
        Assert.Equal("candidate-2", repository.GetCurrent()!.AssessmentId);
    }

    [Fact]
    public void PromoteToCurrentThrowsForAnUnrecordedAssessment()
    {
        var repository = NewRepository("promote-missing.fwdata", out _);
        Assert.Throws<KeyNotFoundException>(() => repository.PromoteToCurrent("nope"));
    }

    [Fact]
    public void RecordRollsBackEverythingOnAGenuinePrimaryKeyCollision()
    {
        var repository = NewRepository("atomic.fwdata", out var database);
        var original = NewAssessment("collide", null, null, "parse");
        repository.Record(original);

        var colliding = NewAssessment("collide", null, null, "parse", words:
        [
            new AssessedWord("different-word", "complete", [])
        ]);

        Assert.ThrowsAny<SqliteException>(() => repository.Record(colliding));

        // The original row survives untouched, and none of the second attempt's words landed anywhere.
        using var connection = database.OpenConnection();
        using (var assessmentCount = connection.CreateCommand())
        {
            assessmentCount.CommandText = "SELECT COUNT(*) FROM Assessments WHERE AssessmentId = 'collide';";
            Assert.Equal(1L, assessmentCount.ExecuteScalar());
        }
        using (var wordCount = connection.CreateCommand())
        {
            wordCount.CommandText = "SELECT COUNT(*) FROM AssessedWords WHERE AssessmentId = 'collide';";
            Assert.Equal(2L, wordCount.ExecuteScalar());
        }
        using (var differentWord = connection.CreateCommand())
        {
            differentWord.CommandText = "SELECT COUNT(*) FROM AssessedWords WHERE Word = 'different-word';";
            Assert.Equal(0L, differentWord.ExecuteScalar());
        }
        var reread = repository.Get("collide");
        Assert.Equal(2, reread.Words!.Count);
        Assert.Equal("bo", reread.Words[0].Word);
    }

    /// <summary>
    /// The trap ADR 0042's Trial amendment names: promotion must happen before the sweep, and the promoted
    /// Assessment must survive by identity, not by hoping the sweep never learns to run first.
    /// </summary>
    [Fact]
    public void SweepDeletesTheProposalsOtherAssessments_ButThePromotedOneSurvivesByIdentity()
    {
        var repository = NewRepository("sweep-trap.fwdata", out var database);
        var proposal = CanonicalId.Mint("proposal/");
        SeedProposal(database, proposal.Value);

        repository.Record(NewAssessment("promoted", proposal, "sha256:intent", "Correctness"));
        repository.Record(NewAssessment("scratch-1", proposal, "sha256:intent", "ParseTime"));
        repository.Record(NewAssessment("scratch-2", proposal, "sha256:older-intent", "Correctness"));

        // Promotion happens first, exactly as apply must sequence it.
        repository.PromoteToCurrent("promoted");
        repository.DeleteByProposal(proposal, exceptAssessmentId: "promoted");

        // Asserted after the sweep has already run — the identity exclusion, not the ordering, is what is pinned.
        Assert.Equal("promoted", repository.Get("promoted").AssessmentId);
        Assert.Equal("promoted", repository.GetCurrent()!.AssessmentId);
        Assert.Throws<KeyNotFoundException>(() => repository.Get("scratch-1"));
        Assert.Throws<KeyNotFoundException>(() => repository.Get("scratch-2"));
        Assert.Empty(repository.ListByProposal(proposal).Where(record => record.AssessmentId != "promoted"));
    }

    [Fact]
    public void SweepWithNoExceptionDeletesEveryAssessmentOnTheProposal()
    {
        var repository = NewRepository("sweep-all.fwdata", out var database);
        var proposal = CanonicalId.Mint("proposal/");
        SeedProposal(database, proposal.Value);
        repository.Record(NewAssessment("only-one", proposal, "sha256:intent", "Correctness"));

        repository.DeleteByProposal(proposal, exceptAssessmentId: null);

        Assert.Empty(repository.ListByProposal(proposal));
    }

    [Fact]
    public void SweepLeavesAnotherProposalsAssessmentsUntouched()
    {
        var repository = NewRepository("sweep-other.fwdata", out var database);
        var swept = CanonicalId.Mint("proposal/");
        var untouched = CanonicalId.Mint("proposal/");
        SeedProposal(database, swept.Value);
        SeedProposal(database, untouched.Value);
        repository.Record(NewAssessment("swept-1", swept, "sha256:intent", "Correctness"));
        repository.Record(NewAssessment("kept-1", untouched, "sha256:intent", "Correctness"));

        repository.DeleteByProposal(swept, exceptAssessmentId: null);

        Assert.Empty(repository.ListByProposal(swept));
        Assert.Equal("kept-1", repository.Get("kept-1").AssessmentId);
    }

    private IAssessmentRepository NewRepository(string fileName, out MotifDatabase database)
    {
        var project = new ProjectLocator(Path.Combine(_root, fileName), Path.GetFileNameWithoutExtension(fileName));
        database = MotifDatabase.OpenOwned(
            Path.Combine(_root, Path.GetFileNameWithoutExtension(fileName) + ".motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        return new AssessmentRepository(database);
    }

    private static void SeedProposal(MotifDatabase database, string proposalId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Proposals (ProposalId, CurrentIntentDigest, Status) " +
            "VALUES ($id, 'sha256:intent', 'proposed');";
        command.Parameters.AddWithValue("$id", proposalId);
        command.ExecuteNonQuery();
    }

    private static NewAssessmentRecord NewAssessment(
        string assessmentId, CanonicalId? proposalId, string? proposalIntentDigest, string kind,
        IReadOnlyList<AssessedWord>? words = null)
    {
        var corpus = Selection.Create("corpus-1", ["bo", "za"]);
        return new NewAssessmentRecord(
            AssessmentId: assessmentId,
            ProposalId: proposalId,
            ProposalIntentDigest: proposalIntentDigest,
            Assessor: "pangloss",
            Kind: kind,
            ScopeJson: "{\"words\":\"all\"}",
            ScopeDigest: "sha256:scope",
            TokeniserName: "whitespace",
            TokeniserVersion: "1",
            BaselineToken: "{\"baseline\":true}",
            Selection: corpus,
            OutcomeDigest: "sha256:outcome",
            SemanticDigest: "sha256:semantic",
            GrammarSourceSha256: "sha256:grammar",
            ModelFingerprint: "fingerprint",
            Pipeline: "pipeline",
            DiagnosticCount: 0,
            Words: words ??
            [
                new AssessedWord("bo", "complete", [new ParsedAnalysis("cat-guid", ["guid-root"], 0, "sha256:identity")]),
                new AssessedWord("za", "incomplete", [])
            ]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}
