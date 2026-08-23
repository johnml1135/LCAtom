using System;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Worker;

/// <summary>Supplies the one metadata record compiled into this worker executable.</summary>
public static class WorkerBuildMetadataProvider
{
    private static readonly WorkerBuildMetadata Metadata = new WorkerBuildMetadata(
        WorkerBuildMetadataGenerated.ProductVersion,
        new ProtocolRange(WorkerBuildMetadataGenerated.ProtocolMinimum,
            WorkerBuildMetadataGenerated.ProtocolMaximum),
        Array.Empty<string>());

    /// <summary>The immutable build metadata used by handshake and publication.</summary>
    public static WorkerBuildMetadata Current => Metadata;
}
