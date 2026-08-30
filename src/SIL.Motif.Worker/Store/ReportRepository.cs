using System.Globalization;
using Microsoft.Data.Sqlite;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Host.Store;

namespace SIL.Motif.Worker.Store;

/// <summary>A durable advisory Report and its exact evidence bindings.</summary>
/// <param name="Kind">
/// Which registered report kind produced this, or <c>null</c> for a Report predating the registry.
/// </param>
/// <param name="RenderedText">
/// The presentation computed at measurement time, stored so a later reader never needs the Assessor that
/// made the underlying Assessment to still be reachable (ADR 0042's Report amendment).
/// </param>
public sealed record ReportRecord(
    string ReportId,
    CanonicalId? ProposalId,
    string? AssessmentId,
    string ReportJson,
    string? EvidenceJson,
    string? Kind = null,
    string? RenderedText = null,
    string? CreatedUtc = null);

/// <summary>Stores Reports without interpreting their advisory findings.</summary>
public sealed class ReportRepository
{
    private readonly MotifDatabase _database;

    /// <summary>Creates a repository over an already worker-owned database.</summary>
    public ReportRepository(MotifDatabase database) => _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>Gets one Report by its durable id.</summary>
    public ReportRecord? Get(string reportId)
    {
        if (string.IsNullOrWhiteSpace(reportId)) throw new ArgumentException("A report id is required.", nameof(reportId));
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ReportId, ProposalId, AssessmentId, ReportJson, EvidenceJson, Kind, RenderedText, CreatedUtc
            FROM Reports WHERE ReportId = $id;
            """;
        command.Parameters.AddWithValue("$id", reportId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>Stores a Report and keeps its report/evidence JSON bytes, kind and rendering unchanged.</summary>
    public void Save(ReportRecord report)
    {
        if (string.IsNullOrWhiteSpace(report.ReportId) || report.ReportJson is null)
            throw new ArgumentException("Report id and report JSON are required.", nameof(report));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Reports (ReportId, ProposalId, AssessmentId, ReportJson, EvidenceJson, Kind, RenderedText, CreatedUtc)
            VALUES ($id, $proposal, $assessment, $report, $evidence, $kind, $renderedText, $created)
            ON CONFLICT(ReportId) DO UPDATE SET
                ProposalId = excluded.ProposalId, AssessmentId = excluded.AssessmentId,
                ReportJson = excluded.ReportJson, EvidenceJson = excluded.EvidenceJson,
                Kind = excluded.Kind, RenderedText = excluded.RenderedText;
            """;
        command.Parameters.AddWithValue("$id", report.ReportId);
        command.Parameters.AddWithValue("$proposal", report.ProposalId is null ? DBNull.Value : report.ProposalId.Value.Value);
        command.Parameters.AddWithValue("$assessment", (object?)report.AssessmentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$report", report.ReportJson);
        command.Parameters.AddWithValue("$evidence", (object?)report.EvidenceJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", (object?)report.Kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$renderedText", (object?)report.RenderedText ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", report.CreatedUtc ?? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static ReportRecord Read(SqliteDataReader reader)
    {
        CanonicalId? proposalId = reader.IsDBNull(1) ? null : CanonicalId.Parse(reader.GetString(1));
        return new ReportRecord(reader.GetString(0), proposalId, reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7));
    }
}
