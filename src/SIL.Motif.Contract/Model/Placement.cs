using System;
using SIL.Motif.Contract.Ids;

namespace SIL.Motif.Contract.Model;

/// <summary>
/// Identity-relative anchor for ordered data (the Change Set contract's "Ordered data"). An edge
/// anchor may omit one side, but never both — a placement that anchors nowhere carries no intent.
/// Numeric indices are never canonical intent and have no representation here.
/// </summary>
public sealed record Placement
{
    public Placement(CanonicalId? after, CanonicalId? before)
    {
        if (after is null && before is null)
        {
            throw new ArgumentException(
                "A placement must anchor to at least one neighbor via 'after' and/or 'before'.");
        }

        After = after;
        Before = before;
    }

    /// <summary>The proposed or existing neighbor immediately before the placed item, if any.</summary>
    public CanonicalId? After { get; }

    /// <summary>The proposed or existing neighbor immediately after the placed item, if any.</summary>
    public CanonicalId? Before { get; }
}
