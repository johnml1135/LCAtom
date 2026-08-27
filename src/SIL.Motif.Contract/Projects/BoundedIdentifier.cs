using System;
using System.Linq;

namespace SIL.Motif.Contract.Projects;

/// <summary>Bounds an opaque identifier that crosses a durable record boundary.</summary>
/// <remarks>
/// A host session identity is chosen by whoever reports it, so it is untrusted input even though nothing
/// remote sends it any more. The bound and the control-character ban stop an unbounded or unprintable value
/// reaching a stored record and, from there, a diagnostic.
/// </remarks>
internal static class BoundedIdentifier
{
    internal const int MaximumLength = 256;

    internal static string Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Identifier must not be empty.", parameterName);
        if (value.Length > MaximumLength)
            throw new ArgumentException("Identifier is longer than its bound.", parameterName);
        if (value.Any(char.IsControl))
            throw new ArgumentException("Identifier must not contain control characters.", parameterName);
        return value;
    }
}
