using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SIL.LCModel;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Model.Snapshot;
using SIL.Motif.Runner.Snapshotting;

namespace SIL.Motif.LiveHost.Baselines;

/// <summary>Hashes the generated model-coverage projection of an already-loaded live model.</summary>
public static class BaselineSemanticDigest
{
    /// <summary>The shape version shared with the generated semantic snapshot projection.</summary>
    public const string ProjectionVersion = SnapshotFields.ProjectionVersion;

    private static readonly IReadOnlyList<MethodInfo> SnapshotMethods = typeof(LexEntrySnapshotter).Assembly
        .GetTypes()
        .Where(type => type.IsClass && type.IsAbstract && type.IsSealed &&
            type.Namespace == typeof(LexEntrySnapshotter).Namespace)
        .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        .Where(method => method.Name == "Snapshot" && method.ReturnType == typeof(ObjectSnapshot))
        .Where(method =>
        {
            var parameters = method.GetParameters();
            return parameters.Length == 2 && parameters[0].ParameterType == typeof(LcmCache);
        })
        .OrderBy(method => method.DeclaringType!.FullName, StringComparer.Ordinal)
        .ToArray();

    /// <summary>Computes canonical SHA-256 over all populated fields exposed by generated snapshotters.</summary>
    public static string Compute(LcmCache cache)
    {
        if (cache is null) throw new ArgumentNullException(nameof(cache));

        using var sha = SHA256.Create();
        using (var crypto = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
        using (var writer = new Utf8JsonWriter(crypto, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            var objects = cache.ServiceLocator.GetInstance<ICmObjectRepository>().AllInstances()
                .Where(value => SnapshotMethods.Any(method =>
                    method.GetParameters()[1].ParameterType.IsInstanceOfType(value)))
                .OrderBy(value => CanonicalId.FromGuid(value.Guid).Value, StringComparer.Ordinal);
            foreach (var value in objects)
            {
                var snapshot = ProjectObject(cache, value);
                writer.WritePropertyName(snapshot.CanonicalId.Value);
                writer.WriteStartObject();
                foreach (var field in snapshot.AlternativesFields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(field.Key);
                    writer.WriteStartObject();
                    foreach (var alternative in field.Value.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                        writer.WriteString(alternative.Key, alternative.Value);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        return FormatDigest(sha.Hash!);
    }

    private static ObjectSnapshot ProjectObject(LcmCache cache, ICmObject value)
    {
        var canonicalId = CanonicalId.FromGuid(value.Guid);
        var fields = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var method in SnapshotMethods)
        {
            if (!method.GetParameters()[1].ParameterType.IsInstanceOfType(value)) continue;
            var part = (ObjectSnapshot)method.Invoke(null, new object[] { cache, value })!;
            if (part.CanonicalId != canonicalId)
                throw new InvalidOperationException("A generated snapshotter changed the object's canonical identity.");

            foreach (var field in part.AlternativesFields)
            {
                if (fields.ContainsKey(field.Key))
                    throw new InvalidOperationException($"Semantic field '{field.Key}' was projected more than once.");
                fields.Add(field.Key, field.Value);
            }
        }

        return new ObjectSnapshot(canonicalId, fields);
    }

    internal static string FormatDigest(byte[] hash)
    {
        var builder = new StringBuilder("sha256:", 71);
        foreach (var value in hash) builder.Append(value.ToString("x2"));
        return builder.ToString();
    }
}
