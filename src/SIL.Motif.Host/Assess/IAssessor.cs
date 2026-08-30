namespace SIL.Motif.Host.Assess;

/// <summary>
/// The closed set of measurements an Assessor can produce (ADR 0042's amendment "an Assessment is one
/// <i>kind</i> of measurement"). Adding a member is a deliberate act, not a convenience: it is what lets a
/// Report later say a kind was never collected, rather than guess at one nobody declared.
/// </summary>
public enum AssessmentKind
{
    /// <summary>The compiled engine's size.</summary>
    EngineSize,

    /// <summary>The time to parse a subset of words.</summary>
    ParseTime,

    /// <summary>Per-morpheme and per-rule timing over a subset — PanGloss's own per-object counters.</summary>
    ObjectTiming,

    /// <summary>Correctness of parses against manual analysis.</summary>
    Correctness,

    /// <summary>The difference between two sets of automatic analysis. A delta is a measurement like any other.</summary>
    Difference,

    /// <summary>Which words now complete that did not before.</summary>
    Completion,
}

/// <summary>
/// What a run was told to do (ADR 0042 decision 3): which words, what to collect, and what limit to apply.
/// </summary>
/// <remarks>
/// <b>Deliberately excludes which grammar was measured.</b> Grammar is the one axis decision 3 allows to
/// differ between a Baseline's Assessment and a candidate's; everything held equal is the scope, so the
/// grammar itself is a parameter to <see cref="IAssessor.ProduceAsync"/>, never a field here. Which Assessor
/// produced a run is likewise not a field: it is the identity of whichever <see cref="IAssessor"/> a caller
/// resolved, not a property of the request handed to it.
/// </remarks>
public sealed record AssessmentScope
{
    public AssessmentScope(
        IReadOnlyList<string> words, string engine, IReadOnlyList<AssessmentKind> collect, TimeSpan perWordLimit)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (string.IsNullOrWhiteSpace(engine))
            throw new ArgumentException("A non-blank engine name is required.", nameof(engine));
        ArgumentNullException.ThrowIfNull(collect);
        if (perWordLimit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(perWordLimit), "A per-word limit must be positive.");

        Words = words;
        Engine = engine;
        Collect = collect;
        PerWordLimit = perWordLimit;
    }

    /// <summary>The resolved word list this run was told to try — not a query, the words themselves.</summary>
    public IReadOnlyList<string> Words { get; }

    /// <summary>
    /// Which of the Assessor's own engines to run under, named in the Assessor's own vocabulary (for example
    /// PanGloss's <c>"fast"</c> and <c>"accurate"</c>). Opaque here on purpose: a scope must stay meaningful
    /// for an Assessor that is not PanGloss and may have different engines or none at all.
    /// </summary>
    public string Engine { get; }

    /// <summary>
    /// Which kinds this run wants. Empty means the Assessor's own default for its kind, because no default
    /// collection set is settled across Assessors yet (mirrors <c>AssessmentScopeConfiguration.Collect</c>).
    /// </summary>
    public IReadOnlyList<AssessmentKind> Collect { get; }

    /// <summary>The per-word cap. Coverage measured under one cap is not comparable with another.</summary>
    public TimeSpan PerWordLimit { get; }
}

/// <summary>
/// One Assessment in raw form (ADR 0042's amendment on Reports): what an Assessor returned for one kind,
/// before any presentation is computed from it.
/// </summary>
/// <param name="Kind">Which kind this is.</param>
/// <param name="CachePath">
/// The path to a file the Assessor itself owns the format of, when this kind's raw material lives in one
/// (ADR 0041 decision 9, ADR 0042 decision 8) — for example PanGloss's stats cache. <c>null</c> when this
/// kind's raw material needs no such file.
/// </param>
/// <param name="CacheDigest">
/// The digest of what was actually written to <see cref="CachePath"/> at production time, hashed from the
/// bytes on disk rather than trusted from the Assessor's exit code — the same discipline
/// <c>BaselineRefresh</c> applies to a published bundle. <c>null</c> exactly when <see cref="CachePath"/> is.
/// </param>
public sealed record ProducedAssessment(AssessmentKind Kind, string? CachePath, string? CacheDigest);

/// <summary>
/// Raised when an Assessor will not produce a requested kind. Carries the kind and, in
/// <see cref="Exception.Message"/>, the reason — the mechanism ADR 0042 decision 4 needs so a caller can
/// say <i>this scope did not collect X</i> rather than receive a zero indistinguishable from a real one.
/// </summary>
public sealed class AssessorRefusalException : Exception
{
    public AssessorRefusalException(string assessor, AssessmentKind kind, string reason)
        : base($"'{assessor}' will not produce {kind}: {reason}")
    {
        Kind = kind;
    }

    /// <summary>The kind that was refused.</summary>
    public AssessmentKind Kind { get; }
}

/// <summary>
/// Produces Assessments of declared kinds under a scope. PanGloss is the common Assessor; a C# HermitCrab
/// or an alignment model is another, and the whole point of this seam is that adding one is exactly that —
/// an addition, never a redesign of a caller that already speaks this interface.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one property that shapes everything here: an Assessor declares which kinds it can produce for a
/// given scope, before producing any of them.</b> The declaration is scope-dependent rather than fixed,
/// because what a scope collected constrains what can honestly be reported back — a scope whose
/// <see cref="AssessmentScope.Collect"/> never asked for <see cref="AssessmentKind.ObjectTiming"/> never ran
/// the pass that would have produced it, so it must not be declared producible after the fact.
/// </para>
/// <para>
/// That declaration is what lets a Report later refuse with <i>this scope did not collect per-object
/// counters</i> — naming the reason — rather than returning zeros, which is indistinguishable from a grammar
/// whose rules cost nothing.
/// </para>
/// </remarks>
public interface IAssessor
{
    /// <summary>This Assessor's name, as cited on every Assessment it produces and resolved through a catalog.</summary>
    string Name { get; }

    /// <summary>
    /// The kinds this Assessor can produce for <paramref name="scope"/> — not necessarily every kind it can
    /// ever produce under some other scope.
    /// </summary>
    IReadOnlyList<AssessmentKind> KindsFor(AssessmentScope scope);

    /// <summary>
    /// Produces one Assessment per kind in <paramref name="scope"/>'s <see cref="AssessmentScope.Collect"/>
    /// (or this Assessor's own default kinds, when it is empty).
    /// </summary>
    /// <param name="scope">What this run was told to do.</param>
    /// <param name="exportedCandidate">
    /// A directory holding exactly the grammar to measure, already saved and needing no open cache — the
    /// same shape <see cref="SIL.Motif.Host.Parser.IPanGlossCandidateExporter"/> produces.
    /// </param>
    /// <exception cref="AssessorRefusalException">
    /// <paramref name="scope"/> asks for a kind not in <see cref="KindsFor"/> for it, thrown before
    /// producing anything so a caller never receives a partial answer for a request it could not fully
    /// satisfy (pinned by `AskingForAnUndeclaredKind_RefusesNamingTheKindAndTheReason`).
    /// </exception>
    Task<IReadOnlyList<ProducedAssessment>> ProduceAsync(
        AssessmentScope scope, string exportedCandidate, CancellationToken cancellationToken);
}
