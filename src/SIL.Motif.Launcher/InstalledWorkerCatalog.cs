using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    private const int MaximumMetadataBytes = 64 * 1024;
    private readonly Func<string, FileAttributes> _fileAttributes;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Gates =
        new System.Collections.Concurrent.ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a catalog at the stable per-user worker installation root.</summary>
    public InstalledWorkerCatalog()
        : this(DefaultRoot())
    {
    }

    /// <summary>Creates a catalog at an injected root, primarily for isolated callers and tests.</summary>
    public InstalledWorkerCatalog(string root)
        : this(root, File.GetAttributes)
    {
    }

    /// <summary>Creates a catalog with an injectable file-attribute reader for validation tests.</summary>
    public InstalledWorkerCatalog(string root, Func<string, FileAttributes>? fileAttributes)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A catalog root is required.", nameof(root));
        if (!Path.IsPathRooted(root))
            throw new ArgumentException("The catalog root must be absolute.", nameof(root));
        Root = CanonicalPath(root);
        _fileAttributes = fileAttributes ?? File.GetAttributes;
    }

    /// <summary>The canonical directory containing versioned worker registrations.</summary>
    public string Root { get; }

    /// <summary>Registers an executable without replacing an existing version registration.</summary>
    public InstalledWorker Register(InstalledWorker worker)
    {
        if (worker is null)
            throw new ArgumentNullException(nameof(worker));
        return RegisterCore(worker, null);
    }

    /// <summary>Registers an executable after checking its compiled metadata record.</summary>
    public InstalledWorker Register(InstalledWorker worker, WorkerBuildMetadata compiled)
    {
        if (worker is null)
            throw new ArgumentNullException(nameof(worker));
        if (compiled is null)
            throw new ArgumentNullException(nameof(compiled));
        return RegisterCore(worker, compiled);
    }

    /// <summary>Reads metadata beside an executable and registers that compiled worker.</summary>
    public InstalledWorker Register(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("A worker executable path is required.", nameof(executablePath));
        var path = CanonicalPath(executablePath);
        var metadata = ReadMetadata(path);
        if (!Version.TryParse(metadata.ProductVersion, out var version) || version is null)
            throw new InvalidDataException("The worker build metadata has an invalid product version.");
        return Register(new InstalledWorker(version, path, metadata.Protocols, metadata.Capabilities), metadata);
    }

    private InstalledWorker RegisterCore(InstalledWorker worker, WorkerBuildMetadata? compiled)
    {
        ValidateWorker(worker, requireExecutable: true);
        var canonical = CanonicalWorker(worker);
        var versionDirectory = Path.Combine(Root, canonical.ProductVersion.ToString());
        var manifestPath = Path.Combine(versionDirectory, "manifest.json");
        ValidateBinding(canonical, versionDirectory);
        lock (Gates.GetOrAdd(Root, _ => new object()))
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(versionDirectory);
            var sidecar = ReadMetadata(canonical.ExecutablePath);
            WorkerMetadataAgreement.RequireMatch(sidecar, canonical);
            if (compiled is not null)
                WorkerMetadataAgreement.RequireMatch(compiled, canonical);
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
                File.WriteAllText(temporary, Serialize(canonical, versionDirectory));
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
        lock (Gates.GetOrAdd(Root, _ => new object()))
        {
            var workers = new List<InstalledWorker>();
            foreach (var directory in Directory.EnumerateDirectories(Root))
            {
                var manifest = Path.Combine(directory, "manifest.json");
                if (!File.Exists(manifest))
                    continue;
                workers.Add(ReadManifest(manifest));
            }
            return workers.OrderByDescending(worker => worker.ProductVersion).ToArray();
        }
    }

    /// <summary>Returns the registered workers; this name emphasizes that unregistered files are ignored.</summary>
    public IReadOnlyList<InstalledWorker> GetInstalled() => List();

    /// <summary>Revalidates a selected registration immediately before process startup.</summary>
    public InstalledWorker ValidateInstalled(InstalledWorker worker)
    {
        if (worker is null)
            throw new ArgumentNullException(nameof(worker));
        var canonical = CanonicalWorker(worker);
        var manifest = Path.Combine(Root, canonical.ProductVersion.ToString(), "manifest.json");
        lock (Gates.GetOrAdd(Root, _ => new object()))
        {
            if (!File.Exists(manifest))
                throw new InvalidDataException("The selected worker registration is missing.");
            var registered = ReadManifest(manifest);
            if (!Equivalent(registered, canonical))
                throw new InvalidDataException("The selected worker registration changed after selection.");
            var sidecar = ReadMetadata(registered.ExecutablePath);
            WorkerMetadataAgreement.RequireMatch(sidecar, registered);
            return registered;
        }
    }

    private WorkerBuildMetadata ReadMetadata(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidDataException("The worker build metadata sidecar is missing.");
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidDataException("The worker build metadata sidecar is missing.");
        var sidecar = Path.Combine(directory, WorkerCommands.BuildMetadataFileName);
        if (!File.Exists(sidecar))
            throw new InvalidDataException("The worker build metadata sidecar is missing.");
        try
        {
            var attributes = _fileAttributes(sidecar);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidDataException("The worker build metadata sidecar must be a regular file.");
            using var stream = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > MaximumMetadataBytes)
                throw new InvalidDataException("The worker build metadata sidecar exceeds its bound.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 1024, false);
            var buffer = new char[MaximumMetadataBytes + 1];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = reader.Read(buffer, count, buffer.Length - count);
                if (read == 0)
                    break;
                count += read;
            }
            if (count > MaximumMetadataBytes || reader.Peek() >= 0)
                throw new InvalidDataException("The worker build metadata sidecar exceeds its bound.");
            return WorkerBuildMetadata.Parse(new string(buffer, 0, count));
        }
        catch (Exception exception) when (exception is ArgumentException || exception is IOException)
        {
            throw new InvalidDataException("The worker build metadata sidecar is invalid.", exception);
        }
    }

    private void ValidateWorker(InstalledWorker worker, bool requireExecutable)
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
        if ((_fileAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("The registered worker executable must not be a reparse point.",
                nameof(worker));
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

    private void ValidateBinding(InstalledWorker worker, string versionDirectory)
    {
        var root = CanonicalPath(Root);
        var version = CanonicalPath(versionDirectory);
        var executable = CanonicalPath(worker.ExecutablePath);
        if (!IsWithin(version, root) || !string.Equals(Path.GetFileName(version),
                worker.ProductVersion.ToString(), PathComparison()) || !IsWithin(executable, version))
            throw new ArgumentException(
                "The worker executable must be inside its immutable product-version directory.",
                nameof(worker));
        for (var current = new DirectoryInfo(executable).Parent; current is not null &&
            IsWithin(current.FullName, root); current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Reparse points are not valid in the worker catalog.", nameof(worker));
        }
    }

    private static bool IsWithin(string path, string parent)
    {
        var normalizedParent = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedParent, PathComparison()) ||
            string.Equals(path, parent, PathComparison());
    }

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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

    private static string Serialize(InstalledWorker worker, string versionDirectory)
    {
        var executableHash = Hash(worker.ExecutablePath);
        return JsonSerializer.Serialize(new Manifest(worker.ProductVersion.ToString(), worker.ExecutablePath,
            new ManifestProtocols(worker.Protocols.Minimum, worker.Protocols.Maximum), worker.Capabilities,
            executableHash, Digest(worker, versionDirectory, executableHash)),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private InstalledWorker ReadManifest(string path)
    {
        if ((_fileAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The worker registration manifest must not be a reparse point.");
        try
        {
            return ReadManifestCore(path);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The worker registration manifest is invalid.", exception);
        }
    }

    private InstalledWorker ReadManifestCore(string path)
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
        var hash = RequiredString(root, "sha256");
        var digest = RequiredString(root, "digest");
        try
        {
            var worker = new InstalledWorker(productVersion, executable,
                new ProtocolRange(minimum, maximum), capabilities);
            ValidateWorker(worker, requireExecutable: true);
            var canonical = CanonicalWorker(worker);
            var versionDirectory = Path.GetDirectoryName(path)!;
            ValidateBinding(canonical, versionDirectory);
            var executableHash = Hash(canonical.ExecutablePath);
            if (!string.Equals(hash, executableHash, StringComparison.Ordinal))
                throw new InvalidDataException("The registered worker executable changed after publication.");
            if (!string.Equals(digest, Digest(canonical, versionDirectory, executableHash), StringComparison.Ordinal))
                throw new InvalidDataException("The worker registration metadata changed after publication.");
            return canonical;
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
        ManifestProtocols Protocols, IReadOnlyList<string> Capabilities, string Sha256, string Digest);

    private sealed record ManifestProtocols(int Minimum, int Maximum);

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static string Digest(InstalledWorker worker, string versionDirectory, string executableHash)
    {
        var relativePath = Path.GetRelativePath(versionDirectory, worker.ExecutablePath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        if (OperatingSystem.IsWindows())
            relativePath = relativePath.ToUpperInvariant();
        var canonical = string.Join("\n", worker.ProductVersion.ToString(), relativePath,
            worker.Protocols.Minimum.ToString(CultureInfo.InvariantCulture),
            worker.Protocols.Maximum.ToString(CultureInfo.InvariantCulture),
            string.Join("\n", worker.Capabilities), executableHash);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }
}
