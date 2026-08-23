using System;
using System.IO;
using System.Linq;
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
