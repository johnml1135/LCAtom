using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SIL.Motif.Contract.Worker;

/// <summary>Client-reported result of one binary transfer stream.</summary>
public sealed record BinaryTransferCompletion
{
    [JsonConstructor]
    public BinaryTransferCompletion(string transferId, long byteCount, string sha256)
    {
        TransferId = WorkerProtocolValidation.Identifier(transferId, nameof(transferId));
        if (byteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(byteCount), "Transferred byte count cannot be negative.");
        ByteCount = byteCount;
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64 || !IsHex(sha256))
            throw new ArgumentException("SHA-256 must be exactly 64 hexadecimal characters.", nameof(sha256));
        Sha256 = sha256;
    }

    /// <summary>The transfer identifier being completed.</summary>
    public string TransferId { get; }

    /// <summary>The number of bytes observed by the sender.</summary>
    public long ByteCount { get; }

    /// <summary>The hexadecimal SHA-256 digest observed by the sender.</summary>
    public string Sha256 { get; }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f') &&
                !(character >= 'A' && character <= 'F'))
                return false;
        }
        return true;
    }
}
