using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Launcher;
using SIL.Motif.Worker;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class WorkerMetadataManifestTests
{
    [Fact]
    public void CurrentMetadataIsClosedAndCanonical()
    {
        var metadata = WorkerBuildMetadataProvider.Current;

        Assert.False(string.IsNullOrWhiteSpace(metadata.ProductVersion));
        Assert.True(metadata.Protocols.Minimum >= 1);
        Assert.True(metadata.Protocols.Maximum >= metadata.Protocols.Minimum);
        Assert.Equal(metadata.Capabilities.OrderBy(value => value, StringComparer.Ordinal), metadata.Capabilities);
        Assert.Equal(metadata.Capabilities.Count, metadata.Capabilities.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(metadata.MetadataDigest, WorkerBuildMetadata.Parse(metadata.ToCanonicalJson()).MetadataDigest);
    }

    [Fact]
    public void GenericMetadataRoundTripsWithStableDigest()
    {
        var metadata = new WorkerBuildMetadata("3.4.2", new ProtocolRange(1, 1), new[] { "jobs.v1" });

        Assert.Equal("3.4.2", metadata.ProductVersion);
        Assert.Equal(metadata, WorkerBuildMetadata.Parse(metadata.ToCanonicalJson()));
        Assert.Equal(metadata.MetadataDigest, WorkerBuildMetadata.Parse(metadata.ToCanonicalJson()).MetadataDigest);
        Assert.Equal(metadata.MetadataDigest, new WorkerBuildMetadata(
            "3.4.2", new ProtocolRange(1, 1), new[] { "jobs.v1" }).MetadataDigest);
    }

    [Fact]
    public void AdditiveMetadataPropertiesAreIgnored()
    {
        const string json =
            "{\"productVersion\":\"3.4.2\",\"min\":1,\"max\":1,\"capabilities\":[],\"future\":true}";

        var metadata = WorkerBuildMetadata.Parse(json);

        Assert.Equal("3.4.2", metadata.ProductVersion);
        Assert.Equal(new ProtocolRange(1, 1), metadata.Protocols);
    }

    [Fact]
    public void MatchingManifestMetadataIsAccepted()
    {
        var metadata = new WorkerBuildMetadata("3.4.2", new ProtocolRange(1, 1), new[] { "jobs.v1" });
        var manifest = new InstalledWorker(new Version(3, 4, 2), "C:\\Motif\\3.4.2\\worker.exe",
            metadata.Protocols, metadata.Capabilities);

        WorkerMetadataAgreement.RequireMatch(metadata, manifest);
    }

    [Fact]
    public void OneManifestFieldChangeIsRejected()
    {
        var metadata = new WorkerBuildMetadata("3.4.2", new ProtocolRange(1, 1), new[] { "jobs.v1" });
        var manifest = new InstalledWorker(new Version(3, 4, 3), "C:\\Motif\\3.4.2\\worker.exe",
            metadata.Protocols, metadata.Capabilities);

        Assert.Throws<InvalidDataException>(() => WorkerMetadataAgreement.RequireMatch(metadata, manifest));
    }

    [Fact]
    public void CapabilityMismatchIsRejected()
    {
        var metadata = new WorkerBuildMetadata("3.4.2", new ProtocolRange(1, 1), new[] { "jobs.v1" });
        var manifest = new InstalledWorker(new Version(3, 4, 2), "C:\\Motif\\3.4.2\\worker.exe",
            metadata.Protocols, Array.Empty<string>());

        Assert.Throws<InvalidDataException>(() => WorkerMetadataAgreement.RequireMatch(metadata, manifest));
    }

    [Fact]
    public void CatalogReadsSidecarAndRejectsChangedCompiledMetadata()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "3.4.2", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var metadata = new WorkerBuildMetadata("3.4.2", new ProtocolRange(1, 1), Array.Empty<string>());
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(executable)!, WorkerCommands.BuildMetadataFileName),
            metadata.ToCanonicalJson());
        var worker = new InstalledWorker(new Version(3, 4, 2), executable,
            metadata.Protocols, metadata.Capabilities);
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));

        catalog.Register(worker);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(executable)!, WorkerCommands.BuildMetadataFileName),
            new WorkerBuildMetadata("3.4.3", new ProtocolRange(1, 1), Array.Empty<string>()).ToCanonicalJson());

        Assert.Throws<InvalidDataException>(() => catalog.ValidateInstalled(worker));
    }

    [Fact]
    public void CatalogRejectsRegistrationWhenSidecarIsMissing()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "3.4.2", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var metadata = new WorkerBuildMetadata("3.4.2", new ProtocolRange(1, 1), Array.Empty<string>());
        var worker = new InstalledWorker(new Version(3, 4, 2), executable,
            metadata.Protocols, metadata.Capabilities);
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));

        Assert.Throws<InvalidDataException>(() => catalog.Register(worker));
        Assert.False(File.Exists(Path.Combine(root.Path, "catalog", "3.4.2", "manifest.json")));
    }

    [Fact]
    public void ValidateInstalledRejectsDeletedSidecar()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "3.4.2", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var metadata = new WorkerBuildMetadata("3.4.2", new ProtocolRange(1, 1), Array.Empty<string>());
        var worker = new InstalledWorker(new Version(3, 4, 2), executable,
            metadata.Protocols, metadata.Capabilities);
        var sidecar = Path.Combine(Path.GetDirectoryName(executable)!, WorkerCommands.BuildMetadataFileName);
        File.WriteAllText(sidecar, metadata.ToCanonicalJson());
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        catalog.Register(worker);
        File.Delete(sidecar);

        Assert.Throws<InvalidDataException>(() => catalog.ValidateInstalled(worker));
    }

    [Fact]
    public void CatalogRejectsOversizedMetadataSidecar()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "3.4.2", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var worker = new InstalledWorker(new Version(3, 4, 2), executable,
            new ProtocolRange(1, 1), Array.Empty<string>());
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(executable)!, WorkerCommands.BuildMetadataFileName),
            new string('x', 64 * 1024 + 1));
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));

        Assert.Throws<InvalidDataException>(() => catalog.Register(worker));
    }

    [Fact]
    public void CatalogRejectsMetadataSidecarReparsePoint()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "3.4.2", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var sidecar = Path.Combine(Path.GetDirectoryName(executable)!, WorkerCommands.BuildMetadataFileName);
        File.WriteAllText(sidecar, new WorkerBuildMetadata("3.4.2", new ProtocolRange(1, 1),
            Array.Empty<string>()).ToCanonicalJson());
        var worker = new InstalledWorker(new Version(3, 4, 2), executable,
            new ProtocolRange(1, 1), Array.Empty<string>());
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"), path =>
            string.Equals(path, sidecar, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.ReparsePoint
                : File.GetAttributes(path));

        Assert.Throws<InvalidDataException>(() => catalog.Register(worker));
    }

    [Fact]
    public void RegisterWithCompiledMetadataRejectsNullArguments()
    {
        using var root = TemporaryDirectory.Create();
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        var metadata = new WorkerBuildMetadata("3.4.2", new ProtocolRange(1, 1), Array.Empty<string>());
        var worker = new InstalledWorker(new Version(3, 4, 2), Path.Combine(root.Path, "worker.exe"),
            metadata.Protocols, metadata.Capabilities);

        Assert.Throws<ArgumentNullException>(() => catalog.Register(null!, metadata));
        Assert.Throws<ArgumentNullException>(() => catalog.Register(worker, null!));
    }

    [Fact]
    public void PublishedSidecarBytesAreExactCanonicalUtf8()
    {
        using var root = TemporaryDirectory.Create();
        var project = FindRepositoryFile("src", "SIL.Motif.Worker", "SIL.Motif.Worker.csproj");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "publish \"" + project + "\" --configuration Debug --no-restore --output \"" +
                root.Path + "\"",
            WorkingDirectory = FindRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, output + Environment.NewLine + error);
        var sidecar = File.ReadAllBytes(Path.Combine(root.Path, WorkerCommands.BuildMetadataFileName));
        Assert.Equal(Encoding.UTF8.GetBytes(WorkerBuildMetadataProvider.Current.ToCanonicalJson()), sidecar);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Motif.sln")))
            directory = directory.Parent!;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static string FindRepositoryFile(params string[] parts) =>
        parts.Aggregate(FindRepositoryRoot(), Path.Combine);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "motif-metadata-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public static TemporaryDirectory Create() => new TemporaryDirectory();

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
