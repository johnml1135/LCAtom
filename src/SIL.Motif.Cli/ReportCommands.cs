using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using SIL.Motif.Contract;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Responses;
using SIL.Motif.Host.Assess;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Cli;

/// <summary>The <c>report</c> verb: computing, storing and rendering one report kind over one Assessment.</summary>
/// <remarks>
/// <para>
/// <see cref="Catalog"/> is the whole registry: adding a kind means registering another
/// <see cref="IReportProducer"/> there, never adding a case here or a new verb (ADR 0042's Reports
/// amendment). <see cref="Assessors"/> deliberately registers no <see cref="SIL.Motif.Host.Parser.PanGlossParser"/>-backed
/// Assessor — every kind registered so far renders from an Assessment's own stored rows, and needs none.
/// </para>
/// <para>
/// A computed report is stored the moment it is produced (<see cref="ReportRecord.RenderedText"/>), so a
/// later reader of that row never has to ask an Assessor — whose binary may by then be gone — to read
/// yesterday's evidence again.
/// </para>
/// </remarks>
public static class ReportCommands
{
    /// <summary>Every registered report kind. Extend by adding an <see cref="IReportProducer"/> here.</summary>
    public static readonly ReportCatalog Catalog = new(new IReportProducer[]
    {
        new CoverageReportProducer(),
        new CorrectnessReportProducer(),
        new DifferenceReportProducer(),
    });

    /// <summary>Every report kind that may be asked for, as <c>report --list-kinds</c> prints it.</summary>
    public static CommandResult ListKinds(bool asJson)
    {
        var response = new ReportKindListResponse(
            Catalog.All.Select(producer => new ReportKindResponse(producer.Kind, producer.Description)).ToArray());
        return new CommandResult(0, asJson
            ? ProjectionJson.Serialize(response) + Environment.NewLine
            : RenderKindList(response));
    }

    /// <summary>
    /// Computes one report kind over one Assessment, stores the rendering, and prints it. Refuses, naming
    /// the reason, when the kind is unregistered (pinned by `AskingForAKindOutsideTheRegistry_Refuses`) or
    /// the Assessment was not collected in a way the kind can report from (pinned by
    /// `ACorrectnessReportOverAParseTimeAssessment_RefusesNamingTheReason`).
    /// </summary>
    public static CommandResult Produce(string fwDataPath, string productVersion, string assessmentId,
        string kind, string? word, string? text, bool asJson)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, _) =>
        {
            AssessmentRecord record;
            try
            {
                record = new AssessmentRepository(database).Get(assessmentId);
            }
            catch (KeyNotFoundException exception)
            {
                return ProjectStoreCommand.Refuse(FailureReason.NotFound, exception.Message);
            }

            IReportProducer producer;
            try
            {
                producer = Catalog.Resolve(kind);
            }
            catch (KeyNotFoundException exception)
            {
                return ProjectStoreCommand.Refuse(FailureReason.InvalidArgument, exception.Message);
            }

            RenderedReport rendered;
            try
            {
                rendered = producer.Produce(record.ToReportable(), new ReportQuery(word, text), AssessorCatalog.Empty);
            }
            catch (ReportRefusalException exception)
            {
                return ProjectStoreCommand.Refuse(FailureReason.Refused, exception.Message);
            }

            var reportId = CanonicalId.Mint("report/").Value;
            var reportJson = JsonSerializer.Serialize(
                new { kind = rendered.Kind, assessmentId, text = rendered.Text }, MotifJson.CreateOptions());
            var evidenceJson = JsonSerializer.Serialize(new
            {
                assessmentId,
                selectionSha256 = record.Selection.Sha256,
                grammarSourceSha256 = record.GrammarSourceSha256,
            }, MotifJson.CreateOptions());
            new ReportRepository(database).Save(new ReportRecord(
                reportId, record.ProposalId, assessmentId, reportJson, evidenceJson, rendered.Kind, rendered.Text));

            var response = new ReportResponse(reportId, assessmentId, rendered.Kind, rendered.Text);
            return new CommandResult(0, asJson
                ? ProjectionJson.Serialize(response) + Environment.NewLine
                : Render(response));
        });
    }

    private static string Render(ReportResponse response)
    {
        var text = new StringBuilder();
        text.AppendLine("Report " + response.ReportId);
        text.AppendLine("  Assessment: " + response.AssessmentId);
        text.AppendLine("  Kind:       " + response.Kind);
        text.AppendLine(response.Text);
        return text.ToString();
    }

    private static string RenderKindList(ReportKindListResponse response)
    {
        var text = new StringBuilder();
        if (response.Kinds.Count == 0)
        {
            text.AppendLine("No report kinds registered.");
            return text.ToString();
        }
        foreach (var kind in response.Kinds)
            text.AppendLine(kind.Kind + "  " + kind.Description);
        return text.ToString();
    }
}
