using System.Collections.Generic;
using System.Linq;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Model.Snapshot;
using SIL.LCModel;

namespace SIL.Motif.Runner.Snapshotting;

/// <summary>
/// Reads a reference collection accessor's full current membership into the
/// <see cref="ReferenceCollectionAlternatives"/> map shape — shared by every generated <c>rel/col</c>/
/// <c>rel/seq</c> field's snapshot read-back.
/// </summary>
internal static class ReferenceCollectionFieldSnapshotting
{
    public static IReadOnlyDictionary<string, string> ReadAlternatives(IEnumerable<ICmObject> members) =>
        ReferenceCollectionAlternatives.ToAlternatives(members.Select(m => CanonicalId.FromGuid(m.Guid)));
}
