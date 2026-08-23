using System;
using System.Globalization;
using System.Text.Json.Serialization;
using SIL.Motif.Contract.Canonicalization;

namespace SIL.Motif.Contract.Baselines;

/// <summary>Semantic identity of a saved project state, excluding capture freshness and transfer evidence.</summary>
public sealed record BaselineSemanticIdentity(
    string ProjectIdentity,
    string SemanticSnapshotDigest,
    string ProjectionVersion);

/// <summary>Identifies one saved semantic project state and the evidence used to capture it.</summary>
public sealed record BaselineToken
{
    [JsonConstructor]
    public BaselineToken(
        string projectIdentity,
        string semanticSnapshotDigest,
        string projectionVersion,
        string capturedUtc,
        string bundleDigest,
        string? capturedHostSessionId = null,
        long? capturedEditGeneration = null)
    {
        ProjectIdentity = RequireNonBlank(projectIdentity, nameof(projectIdentity));
        Sha256Value.RequireCanonical(semanticSnapshotDigest, nameof(semanticSnapshotDigest));
        SemanticSnapshotDigest = semanticSnapshotDigest;
        ProjectionVersion = RequireNonBlank(projectionVersion, nameof(projectionVersion));
        CapturedUtc = RequireUtc(capturedUtc, nameof(capturedUtc));
        Sha256Value.RequireCanonical(bundleDigest, nameof(bundleDigest));
        BundleDigest = bundleDigest;
        var hasHostSession = !string.IsNullOrWhiteSpace(capturedHostSessionId);
        var hasEditGeneration = capturedEditGeneration.HasValue;
        if (capturedEditGeneration is < 0)
            throw new ArgumentOutOfRangeException(
                nameof(capturedEditGeneration), "Captured edit generation cannot be negative.");
        if (hasHostSession != hasEditGeneration)
            throw new ArgumentException(
                "Captured host session and edit generation must be supplied together.");

        CapturedHostSessionId = hasHostSession
            ? RequireNonBlank(capturedHostSessionId, nameof(capturedHostSessionId))
            : null;
        CapturedEditGeneration = capturedEditGeneration;
    }

    /// <summary>The opaque FieldWorks project identity whose state was captured.</summary>
    [JsonPropertyOrder(0)]
    public string ProjectIdentity { get; }

    /// <summary>The canonical digest of the semantic snapshot projection.</summary>
    [JsonPropertyOrder(1)]
    public string SemanticSnapshotDigest { get; }

    /// <summary>The version of the semantic snapshot projection that produced the digest.</summary>
    [JsonPropertyOrder(2)]
    public string ProjectionVersion { get; }

    /// <summary>The UTC instant at which the saved state was captured.</summary>
    [JsonPropertyOrder(3)]
    public string CapturedUtc { get; }

    /// <summary>The canonical digest of the transferred baseline bundle.</summary>
    [JsonPropertyOrder(4)]
    public string BundleDigest { get; }

    /// <summary>The optional live-host session that supplied freshness evidence.</summary>
    [JsonPropertyOrder(5)]
    public string? CapturedHostSessionId { get; }

    /// <summary>The optional live-host edit generation that supplied freshness evidence.</summary>
    [JsonPropertyOrder(6)]
    public long? CapturedEditGeneration { get; }

    /// <summary>The project, semantic state, and projection version that define this baseline's identity.</summary>
    [JsonIgnore]
    public BaselineSemanticIdentity SemanticIdentity =>
        new(ProjectIdentity, SemanticSnapshotDigest, ProjectionVersion);

    /// <summary>Compares semantic state without considering capture freshness or bundle integrity.</summary>
    public bool HasSameSemanticIdentity(BaselineToken? other) =>
        other is not null && SemanticIdentity == other.SemanticIdentity;

    private static string RequireNonBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A nonblank value is required.", parameterName);
        return value!;
    }

    private static string RequireUtc(string? value, string parameterName)
    {
        RequireNonBlank(value, parameterName);
        if (!HasCanonicalUtcShape(value!) ||
            !DateTime.TryParseExact(
                value,
                new[] { "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _))
        {
            throw new ArgumentException("A UTC ISO-8601 timestamp is required.", parameterName);
        }

        return value!;
    }

    private static bool HasCanonicalUtcShape(string value)
    {
        var hasFraction = value.Length > 20;
        if (value.Length < 20 || value.Length > 28 || (hasFraction && value.Length < 22) ||
            value[19 + (hasFraction ? value.Length - 20 : 0)] != 'Z')
            return false;

        var fixedSeparators = new[] { 4, 7, 10, 13, 16 };
        foreach (var index in fixedSeparators)
        {
            var expected = index == 10 ? 'T' : index == 4 || index == 7 ? '-' : ':';
            if (value[index] != expected)
                return false;
        }

        if (hasFraction && value[19] != '.')
            return false;

        var firstFractionDigit = hasFraction ? 20 : 19;
        var lastDigit = hasFraction ? value.Length - 1 : 19;
        for (var index = 0; index < 19; index++)
        {
            if (index is 4 or 7 or 10 or 13 or 16)
                continue;
            if (value[index] < '0' || value[index] > '9')
                return false;
        }

        for (var index = firstFractionDigit; index < lastDigit; index++)
        {
            if (value[index] < '0' || value[index] > '9')
                return false;
        }

        return true;
    }
}
