using System;

namespace SIL.Motif.Runner.Apply;

/// <summary>
/// Thrown by <see cref="ProposalApplier.Apply"/> when its ADR 0004 decision 3 precondition is not
/// met: no <c>BoundDryRunAnchor</c> was supplied at all (a bare apply — a hard error), or one was
/// supplied but the live project's footprint no longer matches it (drift — a hard stop, never a
/// silent proceed).
/// </summary>
public sealed class ApplyPreconditionException : InvalidOperationException
{
    public ApplyPreconditionException(string message) : base(message)
    {
    }
}
