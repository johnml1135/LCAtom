using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Host.Assess;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using SIL.Motif.Host.Store;

namespace SIL.Motif.Worker.Store;

/// <summary>
/// Durable Assessment storage: one immutable measurement per row, plus the project's single pointer to
/// its current Assessment. Every later task (Trial, Reports, comparison, promotion) reaches Assessments
/// only through this seam, so it does not need to know <c>Assessments</c>, <c>AssessedWords</c>, or
/// <c>ParsedAnalyses</c> exist as separate tables.
/// </summary>
public interface IAssessmentRepository
{
    /// <summary>
    /// Records one Assessment together with its words and analyses, in one transaction — a half-written
    /// Assessment must never be observable. Assessments are immutable (ADR 0042 decision 2): recording
    /// under an id that already exists is a genuine primary-key collision, not an upsert.
    /// </summary>
    void Record(NewAssessmentRecord assessment);

    /// <summary>Gets one Assessment, with its full word and analysis detail, by id.</summary>
    /// <exception cref="KeyNotFoundException">No Assessment is recorded under this id.</exception>
    AssessmentRecord Get(string assessmentId);

    /// <summary>
    /// Lists Assessment headers recorded against one Proposal, oldest first. Word and analysis detail is
    /// omitted — call <see cref="Get"/> for one Assessment's full content.
    /// </summary>
    IReadOnlyList<AssessmentRecord> ListByProposal(CanonicalId proposalId);

    /// <summary>
    /// Lists Assessment headers of one kind, oldest first. Word and analysis detail is omitted — call
    /// <see cref="Get"/> for one Assessment's full content.
    /// </summary>
    IReadOnlyList<AssessmentRecord> ListByKind(string kind);

    /// <summary>
    /// Promotes one Assessment to be the project's current Assessment (ADR 0042 decision 2): a pointer
    /// the project holds, not a state the Assessment carries.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No Assessment is recorded under this id.</exception>
    void PromoteToCurrent(string assessmentId);

    /// <summary>Gets the project's current Assessment, or <c>null</c> when none has been promoted yet.</summary>
    /// <exception cref="InvalidDataException">The pointer names an Assessment that is no longer recorded.</exception>
    AssessmentRecord? GetCurrent();

    /// <summary>
    /// Deletes every Assessment recorded against one Proposal, except <paramref name="exceptAssessmentId"/> —
    /// the sweep purge-on-apply drives (ADR 0042's Trial amendment). A promoted candidate must always be
    /// named here by its exact id: this call, not the order it runs in relative to promotion, is what keeps
    /// it out of the sweep.
    /// </summary>
    void DeleteByProposal(CanonicalId proposalId, string? exceptAssessmentId);
}

/// <summary>
/// One Assessment ready to record: its identity, Assessor, kind, scope and digests, and the words and
/// analyses it produced. <see cref="ProposalId"/> and <see cref="ProposalIntentDigest"/> are both null for
/// an Assessment that measures the project itself rather than a candidate Proposal (a Baseline run).
/// <see cref="ProposalIntentDigest"/> alone is null when the Proposal it measured was an uncommitted
/// draft, which has no revision to pin to and so cites the draft's content digest instead.
/// </summary>
public sealed record NewAssessmentRecord(
    string AssessmentId,
    CanonicalId? ProposalId,
    string? ProposalIntentDigest,
    string Assessor,
    string Kind,
    string ScopeJson,
    string ScopeDigest,
    string TokeniserName,
    string TokeniserVersion,
    string BaselineToken,
    CorpusDescriptor Corpus,
    string OutcomeDigest,
    string SemanticDigest,
    string GrammarSourceSha256,
    string ModelFingerprint,
    string Pipeline,
    int DiagnosticCount,
    IReadOnlyList<AssessedWord> Words,
    string? SavedUtc = null);

/// <summary>
/// One recorded Assessment. <see cref="Words"/> is <c>null</c> on a header returned by
/// <see cref="IAssessmentRepository.ListByProposal"/> or <see cref="IAssessmentRepository.ListByKind"/>;
/// <see cref="IAssessmentRepository.Get"/> and <see cref="IAssessmentRepository.GetCurrent"/> populate it.
/// </summary>
public sealed record AssessmentRecord(
    string AssessmentId,
    CanonicalId? ProposalId,
    string? ProposalIntentDigest,
    string Assessor,
    string Kind,
    string ScopeJson,
    string ScopeDigest,
    string TokeniserName,
    string TokeniserVersion,
    string BaselineToken,
    CorpusDescriptor Corpus,
    string OutcomeDigest,
    string SemanticDigest,
    string GrammarSourceSha256,
    string ModelFingerprint,
    string Pipeline,
    int DiagnosticCount,
    string SavedUtc,
    IReadOnlyList<AssessedWord>? Words = null);

/// <summary>
/// Where a caller turns one recorded Assessment into the narrower view it actually needs — a Report's, a
/// comparison's, or a regression check's (ADR 0042's projections). Kept beside <see cref="AssessmentRecord"/>
/// itself rather than beside each narrower type: those live in <c>SIL.Motif.Host</c>, which this module
/// depends on and never the reverse, so this is the one place able to see both the row and every view of it.
/// </summary>
public static class AssessmentRecordProjections
{
    /// <summary>The material a Report is computed from — see <see cref="ReportableAssessment"/>.</summary>
    public static ReportableAssessment ToReportable(this AssessmentRecord record) => new(
        record.AssessmentId, record.Assessor, record.Kind, record.ScopeJson,
        record.Corpus.CorpusId, record.Corpus.Words, record.Corpus.Sha256, record.GrammarSourceSha256,
        record.Words ?? Array.Empty<AssessedWord>());

    /// <summary>The fields a join between two Assessments needs — see <see cref="ComparableAssessment"/>.</summary>
    public static ComparableAssessment ToComparable(this AssessmentRecord record) => new(
        record.AssessmentId, record.Assessor, record.Kind, record.TokeniserName, record.TokeniserVersion,
        record.Words ?? Array.Empty<AssessedWord>());

    /// <summary>The <c>Correctness</c>-kind fields a regression check needs — see <see cref="CorrectnessAssessment"/>.</summary>
    public static CorrectnessAssessment ToCorrectness(this AssessmentRecord record) => new(
        record.AssessmentId, record.Assessor, record.TokeniserName, record.TokeniserVersion,
        ScopeCodec.ReadTrial(record.ScopeJson, RegressionChecker.RequiredKind),
        record.Corpus, record.GrammarSourceSha256, record.Words ?? Array.Empty<AssessedWord>());
}

/// <summary>Reads and writes normalized Assessment tables and the project's current-Assessment pointer.</summary>
public sealed class AssessmentRepository : IAssessmentRepository
{
    private readonly MotifDatabase _database;

    /// <summary>Creates a repository over an already worker-owned database.</summary>
    public AssessmentRepository(MotifDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <inheritdoc />
    public void Record(NewAssessmentRecord assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (string.IsNullOrWhiteSpace(assessment.AssessmentId) || string.IsNullOrWhiteSpace(assessment.Assessor) ||
            string.IsNullOrWhiteSpace(assessment.Kind) || string.IsNullOrWhiteSpace(assessment.ScopeJson) ||
            string.IsNullOrWhiteSpace(assessment.ScopeDigest) || string.IsNullOrWhiteSpace(assessment.TokeniserName) ||
            string.IsNullOrWhiteSpace(assessment.TokeniserVersion) || string.IsNullOrWhiteSpace(assessment.BaselineToken))
        {
            throw new ArgumentException(
                "Assessment id, Assessor, Kind, scope, tokeniser identity, and Baseline token are required.",
                nameof(assessment));
        }
        ArgumentNullException.ThrowIfNull(assessment.Corpus);
        ArgumentNullException.ThrowIfNull(assessment.Words);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        InsertHeader(connection, transaction, assessment);
        InsertWordsAndAnalyses(connection, transaction, assessment.AssessmentId, assessment.Words);
        transaction.Commit();
    }

    /// <inheritdoc />
    public AssessmentRecord Get(string assessmentId)
    {
        using var connection = _database.OpenConnection();
        var header = ReadHeader(connection, null, assessmentId) ??
            throw new KeyNotFoundException($"Assessment '{assessmentId}' was not found.");
        return header with { Words = ReadWords(connection, assessmentId) };
    }

    /// <inheritdoc />
    public IReadOnlyList<AssessmentRecord> ListByProposal(CanonicalId proposalId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = HeaderSelectSql + " WHERE ProposalId = $proposalId ORDER BY SavedUtc, AssessmentId;";
        command.Parameters.AddWithValue("$proposalId", proposalId.Value);
        using var reader = command.ExecuteReader();
        var records = new List<AssessmentRecord>();
        while (reader.Read()) records.Add(ReadHeader(reader));
        return records;
    }

    /// <inheritdoc />
    public IReadOnlyList<AssessmentRecord> ListByKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("A kind is required.", nameof(kind));
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = HeaderSelectSql + " WHERE Kind = $kind ORDER BY SavedUtc, AssessmentId;";
        command.Parameters.AddWithValue("$kind", kind);
        using var reader = command.ExecuteReader();
        var records = new List<AssessmentRecord>();
        while (reader.Read()) records.Add(ReadHeader(reader));
        return records;
    }

    /// <inheritdoc />
    public void PromoteToCurrent(string assessmentId)
    {
        if (string.IsNullOrWhiteSpace(assessmentId))
            throw new ArgumentException("An Assessment id is required.", nameof(assessmentId));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT 1 FROM Assessments WHERE AssessmentId = $id;";
            check.Parameters.AddWithValue("$id", assessmentId);
            if (check.ExecuteScalar() is null)
                throw new KeyNotFoundException($"Assessment '{assessmentId}' was not found.");
        }
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE MotifMetadata SET CurrentAssessmentId = $id WHERE Id = 1;";
        update.Parameters.AddWithValue("$id", assessmentId);
        update.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <inheritdoc />
    public AssessmentRecord? GetCurrent()
    {
        using var connection = _database.OpenConnection();
        string? currentId;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT CurrentAssessmentId FROM MotifMetadata WHERE Id = 1;";
            currentId = command.ExecuteScalar() as string;
        }
        if (currentId is null) return null;

        var header = ReadHeader(connection, null, currentId) ?? throw new InvalidDataException(
            $"MotifMetadata points at current Assessment '{currentId}', which is not recorded " +
            "(store inconsistency).");
        return header with { Words = ReadWords(connection, currentId) };
    }

    /// <inheritdoc />
    public void DeleteByProposal(CanonicalId proposalId, string? exceptAssessmentId)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var sql in new[]
        {
            """
            DELETE FROM ParsedAnalyses WHERE AssessedWordId IN (
                SELECT AssessedWordId FROM AssessedWords WHERE AssessmentId IN (
                    SELECT AssessmentId FROM Assessments
                    WHERE ProposalId = $proposalId AND ($exceptId IS NULL OR AssessmentId != $exceptId)));
            """,
            """
            DELETE FROM AssessedWords WHERE AssessmentId IN (
                SELECT AssessmentId FROM Assessments
                WHERE ProposalId = $proposalId AND ($exceptId IS NULL OR AssessmentId != $exceptId));
            """,
            """
            DELETE FROM Assessments
            WHERE ProposalId = $proposalId AND ($exceptId IS NULL OR AssessmentId != $exceptId);
            """,
        })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$proposalId", proposalId.Value);
            command.Parameters.AddWithValue("$exceptId", (object?)exceptAssessmentId ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void InsertHeader(
        SqliteConnection connection, SqliteTransaction transaction, NewAssessmentRecord assessment)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Assessments
                (AssessmentId, CorpusId, CorpusWordsJson, CorpusSha256, CorpusProvenanceJson,
                 OutcomeDigest, SemanticDigest, GrammarSourceSha256, ModelFingerprint, Pipeline,
                 DiagnosticCount, SavedUtc, ProposalId, ProposalIntentDigest, Assessor, Kind,
                 ScopeJson, ScopeDigest, TokeniserName, TokeniserVersion, BaselineToken)
            VALUES
                ($id, $corpusId, $corpusWords, $corpusSha, $corpusProvenance,
                 $outcomeDigest, $semanticDigest, $grammarSha, $modelFingerprint, $pipeline,
                 $diagnosticCount, $savedUtc, $proposalId, $proposalIntentDigest, $assessor, $kind,
                 $scopeJson, $scopeDigest, $tokeniserName, $tokeniserVersion, $baselineToken);
            """;
        command.Parameters.AddWithValue("$id", assessment.AssessmentId);
        command.Parameters.AddWithValue("$corpusId", assessment.Corpus.CorpusId);
        command.Parameters.AddWithValue("$corpusWords", JsonSerializer.Serialize(assessment.Corpus.Words));
        command.Parameters.AddWithValue("$corpusSha", assessment.Corpus.Sha256);
        command.Parameters.AddWithValue("$corpusProvenance",
            assessment.Corpus.Provenance is null ? DBNull.Value : JsonSerializer.Serialize(assessment.Corpus.Provenance));
        command.Parameters.AddWithValue("$outcomeDigest", assessment.OutcomeDigest);
        command.Parameters.AddWithValue("$semanticDigest", assessment.SemanticDigest);
        command.Parameters.AddWithValue("$grammarSha", assessment.GrammarSourceSha256);
        command.Parameters.AddWithValue("$modelFingerprint", assessment.ModelFingerprint);
        command.Parameters.AddWithValue("$pipeline", assessment.Pipeline);
        command.Parameters.AddWithValue("$diagnosticCount", assessment.DiagnosticCount);
        command.Parameters.AddWithValue("$savedUtc",
            assessment.SavedUtc ?? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$proposalId", (object?)assessment.ProposalId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$proposalIntentDigest", (object?)assessment.ProposalIntentDigest ?? DBNull.Value);
        command.Parameters.AddWithValue("$assessor", assessment.Assessor);
        command.Parameters.AddWithValue("$kind", assessment.Kind);
        command.Parameters.AddWithValue("$scopeJson", assessment.ScopeJson);
        command.Parameters.AddWithValue("$scopeDigest", assessment.ScopeDigest);
        command.Parameters.AddWithValue("$tokeniserName", assessment.TokeniserName);
        command.Parameters.AddWithValue("$tokeniserVersion", assessment.TokeniserVersion);
        command.Parameters.AddWithValue("$baselineToken", assessment.BaselineToken);
        command.ExecuteNonQuery();
    }

    private static void InsertWordsAndAnalyses(
        SqliteConnection connection, SqliteTransaction transaction, string assessmentId,
        IReadOnlyList<AssessedWord> words)
    {
        using var insertWord = connection.CreateCommand();
        insertWord.Transaction = transaction;
        insertWord.CommandText = """
            INSERT INTO AssessedWords (AssessmentId, OrdinalIndex, Word, Outcome) VALUES ($id, $ordinal, $word, $outcome);
            """;
        var assessmentIdParam = insertWord.Parameters.Add("$id", SqliteType.Text);
        var wordOrdinalParam = insertWord.Parameters.Add("$ordinal", SqliteType.Integer);
        var wordTextParam = insertWord.Parameters.Add("$word", SqliteType.Text);
        var wordOutcomeParam = insertWord.Parameters.Add("$outcome", SqliteType.Text);

        using var lastRowId = connection.CreateCommand();
        lastRowId.Transaction = transaction;
        lastRowId.CommandText = "SELECT last_insert_rowid();";

        using var insertAnalysis = connection.CreateCommand();
        insertAnalysis.Transaction = transaction;
        insertAnalysis.CommandText = """
            INSERT INTO ParsedAnalyses (AssessedWordId, OrdinalIndex, CategoryGuid, MorphemeGuidsJson, RootIndex, IdentityDigest)
            VALUES ($wordId, $ordinal, $category, $morphemes, $rootIndex, $identity);
            """;
        var analysisWordIdParam = insertAnalysis.Parameters.Add("$wordId", SqliteType.Integer);
        var analysisOrdinalParam = insertAnalysis.Parameters.Add("$ordinal", SqliteType.Integer);
        var analysisCategoryParam = insertAnalysis.Parameters.Add("$category", SqliteType.Text);
        var analysisMorphemesParam = insertAnalysis.Parameters.Add("$morphemes", SqliteType.Text);
        var analysisRootIndexParam = insertAnalysis.Parameters.Add("$rootIndex", SqliteType.Integer);
        var analysisIdentityParam = insertAnalysis.Parameters.Add("$identity", SqliteType.Text);

        for (var wordIndex = 0; wordIndex < words.Count; wordIndex++)
        {
            var word = words[wordIndex];
            assessmentIdParam.Value = assessmentId;
            wordOrdinalParam.Value = wordIndex;
            wordTextParam.Value = word.Word;
            wordOutcomeParam.Value = word.Outcome;
            insertWord.ExecuteNonQuery();

            var assessedWordId = (long)lastRowId.ExecuteScalar()!;

            for (var analysisIndex = 0; analysisIndex < word.Analyses.Count; analysisIndex++)
            {
                var analysis = word.Analyses[analysisIndex];
                analysisWordIdParam.Value = assessedWordId;
                analysisOrdinalParam.Value = analysisIndex;
                analysisCategoryParam.Value = (object?)analysis.CategoryGuid ?? DBNull.Value;
                analysisMorphemesParam.Value = JsonSerializer.Serialize(analysis.MorphemeGuids);
                analysisRootIndexParam.Value = analysis.RootIndex;
                analysisIdentityParam.Value = analysis.IdentityDigest;
                insertAnalysis.ExecuteNonQuery();
            }
        }
    }

    private const string HeaderSelectSql = """
        SELECT AssessmentId, ProposalId, ProposalIntentDigest, Assessor, Kind, ScopeJson, ScopeDigest,
               TokeniserName, TokeniserVersion, BaselineToken, CorpusId, CorpusWordsJson, CorpusSha256,
               CorpusProvenanceJson, OutcomeDigest, SemanticDigest, GrammarSourceSha256, ModelFingerprint,
               Pipeline, DiagnosticCount, SavedUtc
        FROM Assessments
        """;

    private static AssessmentRecord? ReadHeader(SqliteConnection connection, SqliteTransaction? transaction, string assessmentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = HeaderSelectSql + " WHERE AssessmentId = $id;";
        command.Parameters.AddWithValue("$id", assessmentId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadHeader(reader) : null;
    }

    private static AssessmentRecord ReadHeader(SqliteDataReader reader)
    {
        var corpus = new CorpusDescriptor(
            reader.GetString(10),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(11))!,
            reader.GetString(12),
            reader.IsDBNull(13) ? null : JsonSerializer.Deserialize<CorpusProvenance>(reader.GetString(13)));
        return new AssessmentRecord(
            AssessmentId: reader.GetString(0),
            ProposalId: reader.IsDBNull(1) ? null : CanonicalId.Parse(reader.GetString(1)),
            ProposalIntentDigest: reader.IsDBNull(2) ? null : reader.GetString(2),
            Assessor: reader.GetString(3),
            Kind: reader.GetString(4),
            ScopeJson: reader.GetString(5),
            ScopeDigest: reader.GetString(6),
            TokeniserName: reader.GetString(7),
            TokeniserVersion: reader.GetString(8),
            BaselineToken: reader.GetString(9),
            Corpus: corpus,
            OutcomeDigest: reader.GetString(14),
            SemanticDigest: reader.GetString(15),
            GrammarSourceSha256: reader.GetString(16),
            ModelFingerprint: reader.GetString(17),
            Pipeline: reader.GetString(18),
            DiagnosticCount: reader.GetInt32(19),
            SavedUtc: reader.GetString(20));
    }

    // One streaming pass over a word/analysis join, grouped by word — no N+1 querying.
    private static List<AssessedWord> ReadWords(SqliteConnection connection, string assessmentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT aw.AssessedWordId, aw.Word, aw.Outcome, pa.CategoryGuid, pa.MorphemeGuidsJson, pa.RootIndex, pa.IdentityDigest
            FROM AssessedWords aw
            LEFT JOIN ParsedAnalyses pa ON pa.AssessedWordId = aw.AssessedWordId
            WHERE aw.AssessmentId = $id
            ORDER BY aw.OrdinalIndex, pa.OrdinalIndex;
            """;
        command.Parameters.AddWithValue("$id", assessmentId);

        var words = new List<AssessedWord>();
        long? currentWordId = null;
        string currentWord = "";
        string currentOutcome = "";
        List<ParsedAnalysis> currentAnalyses = [];

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var wordId = reader.GetInt64(0);
            if (wordId != currentWordId)
            {
                if (currentWordId is not null) words.Add(new AssessedWord(currentWord, currentOutcome, currentAnalyses));
                currentWordId = wordId;
                currentWord = reader.GetString(1);
                currentOutcome = reader.GetString(2);
                currentAnalyses = [];
            }

            if (!reader.IsDBNull(6)) // NULL here means no analysis row; the column itself is NOT NULL.
            {
                currentAnalyses.Add(new ParsedAnalysis(
                    CategoryGuid: reader.IsDBNull(3) ? null : reader.GetString(3),
                    MorphemeGuids: JsonSerializer.Deserialize<List<string>>(reader.GetString(4))!,
                    RootIndex: reader.GetInt32(5),
                    IdentityDigest: reader.GetString(6)));
            }
        }

        if (currentWordId is not null) words.Add(new AssessedWord(currentWord, currentOutcome, currentAnalyses));
        return words;
    }
}
