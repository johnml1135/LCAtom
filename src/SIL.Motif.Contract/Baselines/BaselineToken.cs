using System;
using System.Globalization;
using System.IO;
using System.Text;
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
        CapturedHostSessionId = string.IsNullOrWhiteSpace(capturedHostSessionId)
            ? null
            : RequireNonBlank(capturedHostSessionId, nameof(capturedHostSessionId));
        if (capturedEditGeneration is < 0)
            throw new ArgumentOutOfRangeException(
                nameof(capturedEditGeneration), "Captured edit generation cannot be negative.");
        CapturedEditGeneration = capturedEditGeneration;
    }

    /// <summary>The opaque FieldWorks project identity whose state was captured.</summary>
    public string ProjectIdentity { get; }

    /// <summary>The canonical digest of the semantic snapshot projection.</summary>
    public string SemanticSnapshotDigest { get; }

    /// <summary>The version of the semantic snapshot projection that produced the digest.</summary>
    public string ProjectionVersion { get; }

    /// <summary>The UTC instant at which the saved state was captured.</summary>
    public string CapturedUtc { get; }

    /// <summary>The canonical digest of the transferred baseline bundle.</summary>
    public string BundleDigest { get; }

    /// <summary>The optional live-host session that supplied freshness evidence.</summary>
    public string? CapturedHostSessionId { get; }

    /// <summary>The optional live-host edit generation that supplied freshness evidence.</summary>
    public long? CapturedEditGeneration { get; }

    /// <summary>The project, semantic state, and projection version that define this baseline's identity.</summary>
    [JsonIgnore]
    public BaselineSemanticIdentity SemanticIdentity =>
        new(ProjectIdentity, SemanticSnapshotDigest, ProjectionVersion);

    /// <summary>The deterministic digest of <see cref="SemanticIdentity"/>.</summary>
    [JsonIgnore]
    public string SemanticIdentityDigest => ComputeSemanticIdentityDigest(SemanticIdentity);

    /// <summary>Compares semantic state without considering capture freshness or bundle integrity.</summary>
    public bool HasSameSemanticIdentity(BaselineToken other) =>
        other is not null && SemanticIdentity == other.SemanticIdentity;

    private static string ComputeSemanticIdentityDigest(BaselineSemanticIdentity identity)
    {
        using var stream = new MemoryStream();
        WriteFrame(stream, identity.ProjectIdentity);
        WriteFrame(stream, identity.SemanticSnapshotDigest);
        WriteFrame(stream, identity.ProjectionVersion);
        return IntentDigest.Sha256Of(stream.ToArray());
    }

    private static void WriteFrame(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var length = bytes.Length;
        stream.WriteByte((byte)(length >> 24));
        stream.WriteByte((byte)(length >> 16));
        stream.WriteByte((byte)(length >> 8));
        stream.WriteByte((byte)length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string RequireNonBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A nonblank value is required.", parameterName);
        return value!;
    }

    private static string RequireUtc(string? value, string parameterName)
    {
        RequireNonBlank(value, parameterName);
        if (value!.Trim() != value ||
            !DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp) ||
            timestamp.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A UTC ISO-8601 timestamp is required.", parameterName);
        }

        return value;
    }
}
