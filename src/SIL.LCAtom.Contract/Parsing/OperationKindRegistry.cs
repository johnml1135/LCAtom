using System;
using System.Collections.Generic;
using SIL.LCAtom.Contract.Model;

namespace SIL.LCAtom.Contract.Parsing;

/// <summary>
/// The set of operation kinds strict parsing will accept. An unknown <c>kind</c> is a hard error
/// (docs/change-set-contract.md, "Operation vocabulary": "Closed schemas expose only meaningful,
/// supported properties").
/// </summary>
/// <remarks>
/// The exact v1 operation inventory is produced from use cases and the LibLCM coverage manifest in
/// a later phase (docs/implementation-plan.md, Phase 6) and is out of scope for the LibLCM-free
/// contract kernel. This registry is seeded only with the two kinds the normative contract
/// document itself uses as illustrations (<c>lexical/entry/create</c> in "Document shape",
/// <c>sequence/move</c> in "Ordered data"), so that strict-kind rejection is real and testable
/// without inventing vocabulary this repository has not yet ratified. It is deliberately an open,
/// mutable registry — not a closed enum — so later phases can register the full inventory without
/// changing the parsing or digest machinery.
/// </remarks>
public static class OperationKindRegistry
{
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "lexical/entry/create",
        "sequence/move",
    };

    public static bool IsKnown(string kind) => !string.IsNullOrEmpty(kind) && Kinds.Contains(kind);

    /// <summary>Registers an additional known kind. <paramref name="kind"/> must have the form <c>group/...</c>.</summary>
    public static void Register(string kind)
    {
        // Validates the 'group/...' shape; throws FormatException otherwise.
        _ = OperationKind.GetGroup(kind);
        Kinds.Add(kind);
    }

    public static IReadOnlyCollection<string> KnownKinds => Kinds;
}
