using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SIL.Motif.Contract.Worker;

internal static class WorkerProtocolValidation
{
    internal const int MaximumIdentifierLength = 256;
    internal const int MaximumCapabilityLength = 128;
    internal const int MaximumCapabilities = 128;

    internal static string Identifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Identifier must not be empty.", parameterName);
        if (value.Length > MaximumIdentifierLength)
            throw new ArgumentException("Identifier is longer than the protocol bound.", parameterName);
        if (value.Any(char.IsControl))
            throw new ArgumentException("Identifier must not contain control characters.", parameterName);
        return value;
    }

    internal static IReadOnlyList<string> Capabilities(
        IEnumerable<string> values, string parameterName)
    {
        if (values is null)
            throw new ArgumentNullException(parameterName);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var enumerator = values.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (result.Count >= MaximumCapabilities)
                throw new ArgumentException("Capability list is longer than the protocol bound.", parameterName);

            var capability = enumerator.Current;
            if (string.IsNullOrWhiteSpace(capability) || capability.Length > MaximumCapabilityLength ||
                capability.Any(char.IsControl))
            {
                throw new ArgumentException("Capability name is invalid or exceeds the protocol bound.", parameterName);
            }
            if (!seen.Add(capability))
                throw new ArgumentException("Capability names must be unique.", parameterName);

            result.Add(capability);
        }

        result.Sort(StringComparer.Ordinal);
        return new ReadOnlyCollection<string>(result);
    }

    internal static IReadOnlyList<string> CopyCapabilities(
        IEnumerable<string> values, string parameterName) => Capabilities(values, parameterName);
}

/// <summary>The inclusive wire-protocol versions a peer understands.</summary>
public sealed record ProtocolRange
{
    /// <summary>Creates an inclusive protocol-version interval.</summary>
    public ProtocolRange(int minimum, int maximum)
    {
        if (minimum < 1)
            throw new ArgumentOutOfRangeException(nameof(minimum), "Protocol versions start at one.");
        if (maximum < minimum)
            throw new ArgumentException("The maximum protocol version must not precede the minimum.", nameof(maximum));
        if (maximum > 10000)
            throw new ArgumentOutOfRangeException(nameof(maximum), "Protocol version is outside the supported bound.");
        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>The lowest protocol version in the interval.</summary>
    public int Minimum { get; }

    /// <summary>The highest protocol version in the interval.</summary>
    public int Maximum { get; }

    internal int HighestCommon(ProtocolRange other)
    {
        var selected = Math.Min(Maximum, other.Maximum);
        return selected >= Math.Max(Minimum, other.Minimum) ? selected : 0;
    }
}
