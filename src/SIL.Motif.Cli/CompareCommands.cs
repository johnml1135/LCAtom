using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Responses;
using SIL.Motif.Host.Assess;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Cli;

/// <summary>
/// The <c>compare</c> verb. ADR 0042's amendment: comparison is a join on the word, and <c>compare</c> is
/// sugar over producing an Assessment of the <c>Difference</c> kind, stored and citable — never a separate
/// transient mechanism. This computes the join, records the result through
/// <see cref="AssessmentRepository"/> exactly like any other Assessment, and renders it through the same
/// <see cref="DifferenceReportProducer"/> that <c>report --kind difference</c> would use to read it back
/// later, so the preview printed here and a later reading of the stored row never disagree.
/// </summary>
public static class CompareCommands
{
    // A Difference is Motif's own join over two already-stored Assessments, never a fresh measurement.
    private static readonly AssessorCatalog NoAssessorsRegistered = new(Array.Empty<IAssessor>());

    /// <summary>
    /// Loads the two named Assessments, joins them on the word, stores the result as a new Assessment of the
    /// <c>Difference</c> kind, and prints it.
    /// </summary>
    /// <remarks>
    /// The stored Difference inherits the Assessor its two inputs share: a difference between two
    /// measurements made by one Assessor is itself a measurement of that Assessor's making, and requiring
    /// the two inputs to agree is what makes the shared-Assessor rule (ADR 0042 decision 1) enforce itself —
    /// a difference cannot be produced from inputs that disagree, because there would be no single Assessor
    /// left to attribute it to.
    /// </remarks>
    public static CommandResult Produce(string fwDataPath, string productVersion,
        string fromAssessmentId, string toAssessmentId, bool asJson)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, _) =>
        {
            var repository = new AssessmentRepository(database);
            AssessmentRecord from;
            AssessmentRecord to;
            try
            {
                from = repository.Get(fromAssessmentId);
                to = repository.Get(toAssessmentId);
            }
            catch (KeyNotFoundException exception)
            {
                return ProjectStoreCommand.Refuse(FailureReason.NotFound, exception.Message);
            }

            AssessmentComparison comparison;
            try
            {
                comparison = AssessmentComparer.Compare(ToComparable(from), ToComparable(to));
            }
            catch (ComparisonRefusalException exception)
            {
                return ProjectStoreCommand.Refuse(FailureReason.Refused, exception.Message);
            }

            var assessmentId = CanonicalId.Mint("assessment/").Value;
            var scopeJson = ScopeCodec.Write(new StoredScope.Difference(
                fromAssessmentId, toAssessmentId, comparison.FromWordCount, comparison.ToWordCount,
                comparison.SharedWords.Count, from.GrammarSourceSha256, to.GrammarSourceSha256,
                comparison.TokeniserMismatch, comparison.TokeniserWarning));
            var words = comparison.Changes
                .Select(change => new AssessedWord(
                    change.Word, $"{change.Kind}:{change.FromOutcome}->{change.ToOutcome}",
                    Array.Empty<ParsedAnalysis>()))
                .ToArray();
            var corpus = CorpusDescriptor.Create(
                $"difference:{fromAssessmentId}..{toAssessmentId}", comparison.SharedWords);
            var (tokeniserName, tokeniserVersion) = comparison.TokeniserMismatch
                ? ("mixed", "mixed")
                : (from.TokeniserName, from.TokeniserVersion);

            repository.Record(new NewAssessmentRecord(
                AssessmentId: assessmentId,
                ProposalId: null,
                ProposalIntentDigest: null,
                Assessor: from.Assessor,
                Kind: AssessmentKind.Difference.ToString(),
                ScopeJson: scopeJson,
                ScopeDigest: Digest(scopeJson),
                TokeniserName: tokeniserName,
                TokeniserVersion: tokeniserVersion,
                BaselineToken: "{\"from\":" + from.BaselineToken + ",\"to\":" + to.BaselineToken + "}",
                Corpus: corpus,
                OutcomeDigest: Digest(scopeJson),
                SemanticDigest: Digest(string.Join('\n', words.Select(w => w.Word + "|" + w.Outcome))),
                GrammarSourceSha256: string.Empty,
                ModelFingerprint: "motif-comparison",
                Pipeline: "compare",
                DiagnosticCount: comparison.TokeniserMismatch ? 1 : 0,
                Words: words));

            var reportable = new ReportableAssessment(
                assessmentId, from.Assessor, AssessmentKind.Difference.ToString(), scopeJson,
                corpus.CorpusId, corpus.Words, corpus.Sha256, string.Empty, words);
            var rendered = ReportCommands.Catalog.Resolve(DifferenceReportProducer.KindName)
                .Produce(reportable, new ReportQuery(), NoAssessorsRegistered);

            var response = new CompareResponse(assessmentId, fromAssessmentId, toAssessmentId, from.Assessor,
                comparison.FromWordCount, comparison.ToWordCount, comparison.SharedWords.Count,
                comparison.TokeniserMismatch, comparison.TokeniserWarning, rendered.Text);
            return new CommandResult(0, asJson
                ? ProjectionJson.Serialize(response) + Environment.NewLine
                : Render(response));
        });
    }

    private static ComparableAssessment ToComparable(AssessmentRecord record) => new(
        record.AssessmentId, record.Assessor, record.Kind, record.TokeniserName, record.TokeniserVersion,
        record.Words ?? Array.Empty<AssessedWord>());

    private static string Render(CompareResponse response)
    {
        var text = new StringBuilder();
        text.AppendLine("Comparison " + response.AssessmentId);
        text.Append(response.Text);
        return text.ToString();
    }

    private static string Digest(string json)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
