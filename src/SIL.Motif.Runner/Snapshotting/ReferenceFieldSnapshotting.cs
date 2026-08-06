using System.Collections.Generic;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Model.Snapshot;
using SIL.LCModel;

namespace SIL.Motif.Runner.Snapshotting;

/// <summary>
/// Reads a single-valued reference accessor's current occupant (or absence of one) into the
/// <see cref="ReferenceFieldAlternatives"/> map shape — the reference-field analogue of
/// <see cref="MultiAlternativesFieldSnapshotting"/>, shared by every generated <c>rel/atomic</c> field's
/// snapshot read-back and by the hand-written <c>LexEntry.LexemeForm</c> owning/atomic slot (MOT-4
/// slice 2).
/// </summary>
internal static class ReferenceFieldSnapshotting
{
    public static IReadOnlyDictionary<string, string> ReadAlternatives(ICmObject? reference) =>
        ReferenceFieldAlternatives.ToAlternatives(
            reference is null ? (CanonicalId?)null : CanonicalId.FromGuid(reference.Guid));
}
