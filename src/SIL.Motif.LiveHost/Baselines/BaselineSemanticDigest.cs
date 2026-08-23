using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
    public static string Compute(LcmCache cache, CancellationToken cancellationToken = default) =>
        Compute(cache, cancellationToken, null);

    internal static string Compute(
        LcmCache cache,
        CancellationToken cancellationToken,
        Action<ObjectSnapshot>? objectProjected)
    {
        if (cache is null) throw new ArgumentNullException(nameof(cache));
        cancellationToken.ThrowIfCancellationRequested();

        using var sha = SHA256.Create();
        using (var crypto = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
        {
            crypto.WriteByte((byte)'{');
            var objects = cache.ServiceLocator.GetInstance<ICmObjectRepository>().AllInstances()
                .Where(value => IsProjected(value, cancellationToken))
                .OrderBy(value => CanonicalId.FromGuid(value.Guid).Value, StringComparer.Ordinal);
            var index = 0;
            foreach (var value in objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = ProjectObject(cache, value, cancellationToken);
                index++;
                objectProjected?.Invoke(snapshot);
                cancellationToken.ThrowIfCancellationRequested();
                if (index > 1) crypto.WriteByte((byte)',');
                var fragment = CanonicalJson.CanonicalizeToUtf8(
                    ObjectSnapshotJsonWriter.WriteJson(new[] { snapshot }));
                crypto.Write(fragment, 1, fragment.Length - 2);
            }
            crypto.WriteByte((byte)'}');
        }

        return FormatDigest(sha.Hash!);
    }

    private static bool IsProjected(ICmObject value, CancellationToken cancellationToken)
    {
        foreach (var method in SnapshotMethods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (method.GetParameters()[1].ParameterType.IsInstanceOfType(value)) return true;
        }
        return false;
    }

    private static ObjectSnapshot ProjectObject(
        LcmCache cache,
        ICmObject value,
        CancellationToken cancellationToken)
    {
        var canonicalId = CanonicalId.FromGuid(value.Guid);
        var fields = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var method in SnapshotMethods)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
