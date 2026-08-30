using System.Text.Json;
using System.Text.Json.Serialization;
using SIL.Motif.Contract;

namespace SIL.Motif.Host.Assess;

/// <summary>
/// The closed set of shapes a stored Assessment's scope can take (ADR 0042 decision 3: a scope is embedded
/// by content, not by reference). A Trial's scope describes a measurement; a Difference's describes a
/// comparison already made between two other Assessments — the two carry different information and are
/// never forced into one shape with everything nullable.
/// </summary>
public abstract record StoredScope
{
    private StoredScope() { }

    /// <summary>
    /// What a Trial told an Assessor to do, for any of the kinds a Trial run collects (<c>ParseTime</c>,
    /// <c>Correctness</c>, <c>ObjectTiming</c>, <c>EngineSize</c>): the declared query, the words it resolved
    /// to, the engine, which kinds were collected, and the per-word cap.
    /// </summary>
    public sealed record Trial(
        string Query,
        IReadOnlyList<string> Words,
        string Engine,
        IReadOnlyList<AssessmentKind> Collect,
        TimeSpan PerWordLimit) : StoredScope;

    /// <summary>
    /// What <c>compare</c> joined for a <c>Difference</c> Assessment: the two input Assessments' ids, word
    /// counts and grammar identities, and any tokeniser warning the join raised.
    /// </summary>
    public sealed record Difference(
        string FromAssessmentId,
        string ToAssessmentId,
        int FromWordCount,
        int ToWordCount,
        int SharedWordCount,
        string FromGrammarSourceSha256,
        string ToGrammarSourceSha256,
        bool TokeniserMismatch,
        string? TokeniserWarning) : StoredScope;
}

/// <summary>
/// The one module that writes a scope to <c>Assessments.ScopeJson</c> and reads it back (ADR 0042 decision
/// 3). A Trial's writer and <c>compare</c>'s writer both go through <see cref="Write"/>; every reader —
/// coverage, correctness, difference, and regression checking — goes through <see cref="ReadTrial"/> or
/// <see cref="ReadDifference"/>. None of those five needs to know the wire shape the other four use.
/// </summary>
public static class ScopeCodec
{
    private static readonly JsonSerializerOptions WireOptions = MotifJson.CreateOptions();

    /// <summary>Writes a scope in the shape <see cref="ReadTrial"/> or <see cref="ReadDifference"/> reads back.</summary>
    public static string Write(StoredScope scope) => scope switch
    {
        StoredScope.Trial trial => JsonSerializer.Serialize(new TrialWire(
            trial.Query, trial.Words, trial.Engine,
            trial.Collect.Select(kind => kind.ToString()).ToArray(),
            (long)trial.PerWordLimit.TotalMilliseconds), WireOptions),
        StoredScope.Difference difference => JsonSerializer.Serialize(new DifferenceWire(
            difference.FromAssessmentId, difference.ToAssessmentId, difference.FromWordCount,
            difference.ToWordCount, difference.SharedWordCount, difference.FromGrammarSourceSha256,
            difference.ToGrammarSourceSha256, difference.TokeniserMismatch, difference.TokeniserWarning),
            WireOptions),
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown stored scope shape."),
    };

    /// <exception cref="ReportRefusalException">The recorded scope is not readable as a Trial's measurement scope.</exception>
    public static StoredScope.Trial ReadTrial(string scopeJson, string reportKind) =>
        Read(scopeJson, reportKind) as StoredScope.Trial
            ?? throw new ReportRefusalException(reportKind, "the Assessment's recorded scope is a comparison, not a measurement.");

    /// <exception cref="ReportRefusalException">The recorded scope is not readable as a Difference's comparison scope.</exception>
    public static StoredScope.Difference ReadDifference(string scopeJson, string reportKind) =>
        Read(scopeJson, reportKind) as StoredScope.Difference
            ?? throw new ReportRefusalException(reportKind, "the Assessment's recorded scope is a measurement, not a comparison.");

    // Self-describing: a Difference scope is the only shape that ever carries "fromAssessmentId".
    private static StoredScope Read(string scopeJson, string reportKind)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(scopeJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw Unreadable(reportKind);
        }

        return root.TryGetProperty("fromAssessmentId", out _) ? ParseDifference(root, reportKind) : ParseTrial(root, reportKind);
    }

    private static StoredScope.Trial ParseTrial(JsonElement root, string reportKind)
    {
        var wire = root.Deserialize<TrialWire>(WireOptions);
        if (wire is null || string.IsNullOrWhiteSpace(wire.Engine)) throw Unreadable(reportKind);
        return new StoredScope.Trial(
            wire.Query ?? string.Empty, wire.Words ?? Array.Empty<string>(), wire.Engine,
            ParseCollect(wire.Collect, reportKind), TimeSpan.FromMilliseconds(wire.PerWordLimitMs));
    }

    private static StoredScope.Difference ParseDifference(JsonElement root, string reportKind)
    {
        var wire = root.Deserialize<DifferenceWire>(WireOptions);
        if (wire is null) throw Unreadable(reportKind);
        return new StoredScope.Difference(
            wire.FromAssessmentId, wire.ToAssessmentId, wire.FromWordCount, wire.ToWordCount, wire.SharedWordCount,
            wire.FromGrammarSourceSha256 ?? string.Empty, wire.ToGrammarSourceSha256 ?? string.Empty,
            wire.TokeniserMismatch, wire.TokeniserWarning);
    }

    private static IReadOnlyList<AssessmentKind> ParseCollect(IReadOnlyList<string>? collect, string reportKind)
    {
        if (collect is null || collect.Count == 0) return Array.Empty<AssessmentKind>();
        var kinds = new List<AssessmentKind>(collect.Count);
        foreach (var name in collect)
        {
            if (!Enum.TryParse<AssessmentKind>(name, ignoreCase: true, out var kind)) throw Unreadable(reportKind);
            kinds.Add(kind);
        }
        return kinds;
    }

    private static ReportRefusalException Unreadable(string reportKind) =>
        new(reportKind, "the Assessment's recorded scope could not be read.");

    private sealed record TrialWire(
        [property: JsonPropertyName("query")] string? Query,
        [property: JsonPropertyName("words")] IReadOnlyList<string>? Words,
        [property: JsonPropertyName("engine")] string? Engine,
        [property: JsonPropertyName("collect")] IReadOnlyList<string>? Collect,
        [property: JsonPropertyName("perWordLimitMs")] long PerWordLimitMs);

    private sealed record DifferenceWire(
        [property: JsonPropertyName("fromAssessmentId")] string FromAssessmentId,
        [property: JsonPropertyName("toAssessmentId")] string ToAssessmentId,
        [property: JsonPropertyName("fromWordCount")] int FromWordCount,
        [property: JsonPropertyName("toWordCount")] int ToWordCount,
        [property: JsonPropertyName("sharedWordCount")] int SharedWordCount,
        [property: JsonPropertyName("fromGrammarSourceSha256")] string? FromGrammarSourceSha256,
        [property: JsonPropertyName("toGrammarSourceSha256")] string? ToGrammarSourceSha256,
        [property: JsonPropertyName("tokeniserMismatch")] bool TokeniserMismatch,
        [property: JsonPropertyName("tokeniserWarning")] string? TokeniserWarning);
}
