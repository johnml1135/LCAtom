using System;

namespace SIL.Motif.Contract.Parsing;

/// <summary>
/// Raised by strict closed JSON parsing: an unknown operation kind, an unknown semantic property,
/// a malformed canonical id, or any other structural violation of docs/change-set-contract.md.
/// Always a hard error — a Proposal that fails to parse is not ingestible, never partially
/// accepted.
/// </summary>
public sealed class ContractParseException : Exception
{
    public ContractParseException(string message) : base(message)
    {
    }

    public ContractParseException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
