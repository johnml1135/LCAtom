using System.Collections.Generic;

namespace SIL.Motif.Contract.Responses;

/// <summary>The <c>config show</c> report: a project's fully-resolved Assessment configuration.</summary>
public sealed record ProjectConfigurationProjection(
    bool GateOnRegression,
    bool PurgeOnApply,
    IReadOnlyList<AssessmentScopeProjection> Scopes);

/// <summary>One declared Assessment scope, as reported by <c>config show</c>.</summary>
public sealed record AssessmentScopeProjection(
    string Name,
    string Query,
    string Assessor,
    string Engine,
    IReadOnlyList<string> Collect,
    long PerWordLimitMs);
