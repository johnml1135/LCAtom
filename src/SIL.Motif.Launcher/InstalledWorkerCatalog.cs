using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Launcher;

/// <summary>Describes one immutable, registered worker installation.</summary>
public sealed record InstalledWorker(
    Version ProductVersion,
    string ExecutablePath,
    ProtocolRange Protocols,
    IReadOnlyList<string> Capabilities);

/// <summary>Publishes and reads immutable worker registrations under one user-owned root.</summary>
public sealed class InstalledWorkerCatalog
{
    private const int MaximumManifestBytes = 64 * 1024;
    private readonly object _gate = new object();

    /// <summary>Creates a catalog at the stable per-user worker installation root.</summary>
    public InstalledWorkerCatalog()
        : this(DefaultRoot())
    {
    }

    /// <summary>Creates a catalog at an injected root, primarily for isolated callers and tests.</summary>
    public InstalledWorkerCatalog(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A catalog root is required.", nameof(root));
        if (!Path.IsPathRooted(root))
            throw new ArgumentException("The catalog root must be absolute.", nameof(root));
        Root = CanonicalPath(root);
    }

    /// <summary>The canonical directory containing versioned worker registrations.</summary>
    public string Root { get; }

    /// <summary>Registers an executable without replacing an existing version registration.</summary>
    public InstalledWorker Register(InstalledWorker worker)
    {
        ValidateWorker(worker, requireExecutable: true);
        var canonical = CanonicalWorker(worker);
        var versionDirectory = Path.Combine(Root, canonical.ProductVersion.ToString());
        var manifestPath = Path.Combine(versionDirectory, "manifest.json");
        lock (_gate)
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(versionDirectory);
            if (File.Exists(manifestPath))
            {
                var existing = ReadManifest(manifestPath);
                if (!Equivalent(existing, canonical))
                    throw new InvalidOperationException(
                        "The worker product version is already registered with different metadata.");
                return existing;
            }

            var temporary = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, Serialize(canonical));
                File.Move(temporary, manifestPath);
            }
            catch (IOException)
            {
                if (!File.Exists(manifestPath))
                    throw;
                var existing = ReadManifest(manifestPath);
                if (!Equivalent(existing, canonical))
                    throw new InvalidOperationException(
                        "The worker product version is already registered with different metadata.");
                return existing;
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        return canonical;
    }

    /// <summary>Returns only workers backed by registered manifests, never arbitrary files.</summary>
    public IReadOnlyList<InstalledWorker> List()
    {
        if (!Directory.Exists(Root))
            return Array.Empty<InstalledWorker>();
        lock (_gate)
        {
            var workers = new List<InstalledWorker>();
            foreach (var directory in Directory.EnumerateDirectories(Root))
            {
                var manifest = Path.Combine(directory, "manifest.json");
                if (File.Exists(manifest))
                    workers.Add(ReadManifest(manifest));
            }
            return workers.OrderByDescending(worker => worker.ProductVersion).ToArray();
        }
    }

    /// <summary>Returns the registered workers; this name emphasizes that unregistered files are ignored.</summary>
    public IReadOnlyList<InstalledWorker> GetInstalled() => List();

    private static void ValidateWorker(InstalledWorker worker, bool requireExecutable)
    {
        if (worker is null)
            throw new ArgumentNullException(nameof(worker));
        if (worker.ProductVersion is null || worker.ProductVersion.Major < 0 ||
            worker.ProductVersion.Minor < 0 || worker.ProductVersion.Build < -1 ||
            worker.ProductVersion.Revision < -1)
            throw new ArgumentException("The worker product version is invalid.", nameof(worker));
        if (worker.Protocols is null)
            throw new ArgumentException("The worker protocol range is required.", nameof(worker));
        if (string.IsNullOrWhiteSpace(worker.ExecutablePath) || !Path.IsPathRooted(worker.ExecutablePath))
            throw new ArgumentException("The worker executable path must be absolute.", nameof(worker));
        var path = CanonicalPath(worker.ExecutablePath);
        if (requireExecutable && (!File.Exists(path) || !IsExecutable(path)))
            throw new FileNotFoundException("The registered worker executable does not exist or is not executable.", path);
        ValidateCapabilities(worker.Capabilities);
    }

    private static bool IsExecutable(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".com", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateCapabilities(IReadOnlyList<string> capabilities)
    {
        if (capabilities is null)
            throw new ArgumentNullException(nameof(capabilities));
        if (capabilities.Count > 128)
            throw new ArgumentException("The capability list exceeds its bound.", nameof(capabilities));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            if (string.IsNullOrWhiteSpace(capability) || capability.Length > 128 ||
                capability.Any(char.IsControl) || !seen.Add(capability))
                throw new ArgumentException("The worker capabilities are invalid.", nameof(capabilities));
        }
    }

    private static InstalledWorker CanonicalWorker(InstalledWorker worker)
    {
        return worker with
        {
            ExecutablePath = CanonicalPath(worker.ExecutablePath),
            Capabilities = worker.Capabilities.OrderBy(value => value, StringComparer.Ordinal).ToArray()
        };
    }

    private static string CanonicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (root is not null && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return full;
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool Equivalent(InstalledWorker left, InstalledWorker right)
    {
        return left.ProductVersion == right.ProductVersion &&
            string.Equals(left.ExecutablePath, right.ExecutablePath,
                StringComparison.OrdinalIgnoreCase) &&
            left.Protocols.Minimum == right.Protocols.Minimum &&
            left.Protocols.Maximum == right.Protocols.Maximum &&
            left.Capabilities.SequenceEqual(right.Capabilities, StringComparer.Ordinal);
    }

    private static string Serialize(InstalledWorker worker)
    {
        return JsonSerializer.Serialize(new Manifest(worker.ProductVersion.ToString(), worker.ExecutablePath,
            new ManifestProtocols(worker.Protocols.Minimum, worker.Protocols.Maximum), worker.Capabilities),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private static InstalledWorker ReadManifest(string path)
    {
        var information = new FileInfo(path);
        if (information.Length <= 0 || information.Length > MaximumManifestBytes)
            throw new InvalidDataException("The worker registration manifest exceeds its bound.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var product = RequiredString(root, "productVersion");
        if (!Version.TryParse(product, out var productVersion) || productVersion is null)
            throw new InvalidDataException("The worker registration has an invalid product version.");
        var executable = RequiredString(root, "executablePath");
        var protocolElement = RequiredObject(root, "protocols");
        var minimum = RequiredInt(protocolElement, "minimum");
        var maximum = RequiredInt(protocolElement, "maximum");
        var capabilitiesElement = RequiredArray(root, "capabilities");
        var capabilities = new List<string>();
        foreach (var value in capabilitiesElement.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("The worker registration has an invalid capability.");
            capabilities.Add(value.GetString()!);
        }
        try
        {
            var worker = new InstalledWorker(productVersion, executable,
                new ProtocolRange(minimum, maximum), capabilities);
            ValidateWorker(worker, requireExecutable: true);
            return CanonicalWorker(worker);
        }
        catch (Exception exception) when (exception is ArgumentException || exception is IOException)
        {
            throw new InvalidDataException("The worker registration manifest is invalid.", exception);
        }
    }

    private static string RequiredString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("The worker registration is missing " + property + ".");
        return value.GetString()!;
    }

    private static JsonElement RequiredObject(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The worker registration is missing " + property + ".");
        return value;
    }

    private static JsonElement RequiredArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() > 128)
            throw new InvalidDataException("The worker registration has an invalid " + property + ".");
        return value;
    }

    private static int RequiredInt(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || !value.TryGetInt32(out var result))
            throw new InvalidDataException("The worker registration is missing " + property + ".");
        return result;
    }

    private static string DefaultRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            local = Path.GetTempPath();
        return Path.Combine(local, "Motif", "workers");
    }

    private sealed record Manifest(string ProductVersion, string ExecutablePath,
        ManifestProtocols Protocols, IReadOnlyList<string> Capabilities);

    private sealed record ManifestProtocols(int Minimum, int Maximum);
}
