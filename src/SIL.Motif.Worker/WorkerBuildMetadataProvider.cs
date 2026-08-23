using System;
using System.Text;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Worker;

/// <summary>Supplies the one metadata record compiled into this worker executable.</summary>
public static class WorkerBuildMetadataProvider
{
    private static readonly WorkerBuildMetadata Metadata = WorkerBuildMetadata.Parse(
        Encoding.UTF8.GetString(Convert.FromBase64String(WorkerBuildMetadataGenerated.CanonicalJsonBase64)));

    /// <summary>The immutable build metadata used by handshake and publication.</summary>
    public static WorkerBuildMetadata Current => Metadata;
}
