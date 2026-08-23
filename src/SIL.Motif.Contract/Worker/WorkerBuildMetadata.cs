using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SIL.Motif.Contract.Worker;

/// <summary>Closed, compiled identity and wire surface of one worker build.</summary>
public sealed record WorkerBuildMetadata
{
    /// <summary>Creates validated build metadata and computes its canonical digest.</summary>
    public WorkerBuildMetadata(string productVersion, ProtocolRange protocols,
        IReadOnlyList<string> capabilities)
    {
        ProductVersion = WorkerProtocolValidation.Identifier(productVersion, nameof(productVersion));
        Protocols = protocols ?? throw new ArgumentNullException(nameof(protocols));
        Capabilities = new CapabilityList(WorkerProtocolValidation.CopyCapabilities(capabilities, nameof(capabilities)));
        MetadataDigest = Digest(ToCanonicalJson());
    }

    /// <summary>The product version compiled into the worker.</summary>
    public string ProductVersion { get; }

    /// <summary>The inclusive protocol interval compiled into the worker.</summary>
    public ProtocolRange Protocols { get; }

    /// <summary>The ordinal-sorted, duplicate-free capabilities compiled into the worker.</summary>
    public IReadOnlyList<string> Capabilities { get; }

    /// <summary>The SHA-256 digest of the canonical metadata JSON.</summary>
    public string MetadataDigest { get; }

    /// <summary>Creates the wire offer, optionally adding the server-issued connection identity.</summary>
    public WorkerHandshakeOffer ToHandshakeOffer(string? connectionId = null) =>
        new WorkerHandshakeOffer(ProductVersion, Protocols, Capabilities, connectionId);

    /// <summary>Serializes product version, protocol endpoints, and capabilities in canonical order.</summary>
    public string ToCanonicalJson() => JsonSerializer.Serialize(new CanonicalMetadata(
        ProductVersion, Protocols.Minimum, Protocols.Maximum, Capabilities));

    /// <summary>Parses required metadata fields while ignoring additive unknown properties.</summary>
    public static WorkerBuildMetadata Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Worker build metadata JSON is required.", nameof(json));
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Worker build metadata must be an object.");
            var productVersion = RequiredString(root, "productVersion");
            var minimum = RequiredInt(root, "min");
            var maximum = RequiredInt(root, "max");
            if (!root.TryGetProperty("capabilities", out var capabilityElement) ||
                capabilityElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Worker build metadata capabilities are required.");
            var capabilities = new List<string>();
            foreach (var value in capabilityElement.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Worker build metadata contains an invalid capability.");
                capabilities.Add(value.GetString()!);
            }
            return new WorkerBuildMetadata(productVersion, new ProtocolRange(minimum, maximum), capabilities);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Worker build metadata JSON is invalid.", nameof(json), exception);
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(exception.Message, nameof(json), exception);
        }
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("Worker build metadata is missing " + name + ".");
        return value.GetString()!;
    }

    private static int RequiredInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result))
            throw new InvalidDataException("Worker build metadata is missing " + name + ".");
        return result;
    }

    private static string Digest(string json)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
            builder.Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private sealed class CanonicalMetadata
    {
        public CanonicalMetadata(string productVersion, int minimum, int maximum,
            IReadOnlyList<string> capabilities)
        {
            ProductVersion = productVersion;
            Minimum = minimum;
            Maximum = maximum;
            Capabilities = capabilities;
        }

        [JsonPropertyName("productVersion")]
        [JsonPropertyOrder(0)]
        public string ProductVersion { get; }

        [JsonPropertyName("min")]
        [JsonPropertyOrder(1)]
        public int Minimum { get; }

        [JsonPropertyName("max")]
        [JsonPropertyOrder(2)]
        public int Maximum { get; }

        [JsonPropertyName("capabilities")]
        [JsonPropertyOrder(3)]
        public IReadOnlyList<string> Capabilities { get; }
    }

    private sealed class CapabilityList : IReadOnlyList<string>
    {
        private readonly IReadOnlyList<string> _values;

        public CapabilityList(IReadOnlyList<string> values) => _values = values;

        public int Count => _values.Count;

        public string this[int index] => _values[index];

        public IEnumerator<string> GetEnumerator() => _values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override bool Equals(object? obj) => obj is IEnumerable<string> values &&
            _values.SequenceEqual(values, StringComparer.Ordinal);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                foreach (var value in _values)
                    hash = hash * 31 + StringComparer.Ordinal.GetHashCode(value);
                return hash;
            }
        }
    }

    private sealed class InvalidDataException : Exception
    {
        public InvalidDataException(string message) : base(message) { }
    }
}
