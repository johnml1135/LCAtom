using System;

namespace SIL.Motif.Runner.Apply;

/// <summary>
/// Thrown by <see cref="ProposalApplier.Apply"/> when its ADR-0004-§3 precondition is not met: no
/// <c>BoundDryRunAnchor</c> was supplied at all (a bare apply — a hard error), or one was
/// supplied but the live project's footprint no longer matches it (drift — a hard stop, never a
/// silent proceed). See docs/adr/0004-prerequisite-graph-stable-ids-bound-apply.md, decision 3.
/// </summary>
public sealed class ApplyPreconditionException : InvalidOperationException
{
    public ApplyPreconditionException(string message) : base(message)
    {
    }
}
