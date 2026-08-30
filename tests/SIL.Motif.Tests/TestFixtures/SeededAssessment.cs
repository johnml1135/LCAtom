using System;
using System.IO;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Analysis;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Tests.TestFixtures;

/// <summary>
/// Puts an Assessment into a project database the way the worker does, so a test that only wants one to read
/// back does not have to know the fields a Trial fills in. The CLI reads Assessments and never writes them, so
/// seeding any other way would exercise a shape production never produces.
/// </summary>
internal static class SeededAssessment
{
    public static string Record(string fwDataPath, StoredAssessment assessment, string assessmentId)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        var project = new ProjectLocator(fwDataPath, Path.GetFileNameWithoutExtension(fwDataPath));
        using var database = MotifDatabase.OpenOwned(
            ProjectDatabaseCatalog.DatabasePathFor(project), project, MotifSchema.CurrentSchema, new Version(1, 0));

        new AssessmentRepository(database).Record(new NewAssessmentRecord(
            AssessmentId: assessmentId,
            ProposalId: null,
            ProposalIntentDigest: null,
            Assessor: "test",
            Kind: "ParseTime",
            ScopeJson: "{}",
            ScopeDigest: "sha256:scope",
            TokeniserName: "whitespace-and-punctuation",
            TokeniserVersion: "1",
            BaselineToken: "{}",
            Selection: assessment.Selection,
            OutcomeDigest: assessment.Report.OutcomeDigest,
            SemanticDigest: assessment.Report.SemanticDigest,
            GrammarSourceSha256: assessment.Report.GrammarSourceSha256,
            ModelFingerprint: assessment.Report.ModelFingerprint,
            Pipeline: assessment.Report.Pipeline,
            DiagnosticCount: assessment.Report.DiagnosticCount,
            Words: assessment.Report.Words));

        return assessmentId;
    }
}
