using System.Collections.Generic;
using SIL.LCAtom.Contract.Ids;
using SIL.LCAtom.Model.Snapshot;
using SIL.LCModel;

namespace SIL.LCAtom.Runner.Snapshotting;

/// <summary>
/// Reads a real <c>ILexSense</c> from LibLCM into the LibLCM-free <see cref="ObjectSnapshot"/>
/// projection. Stage C reads exactly one field, <see cref="SnapshotFields.LexSenseGloss"/>; more
/// fields are added here (never by changing <see cref="ObjectSnapshot"/>'s shape) as later stages
/// widen the operation vocabulary.
/// </summary>
public static class LexSenseSnapshotter
{
    public static ObjectSnapshot Snapshot(LcmCache cache, ILexSense sense)
    {
        var canonicalId = CanonicalId.FromGuid(sense.Guid);

        var fields = new Dictionary<string, IReadOnlyDictionary<string, string>>();

        var gloss = ReadMultiUnicode(cache, sense.Gloss);
        if (gloss.Count > 0)
            fields[SnapshotFields.LexSenseGloss] = gloss;

        return new ObjectSnapshot(canonicalId, fields);
    }

    /// <summary>
    /// Reads every populated alternative of a MultiUnicode accessor as ws-tag -&gt; text, per
    /// docs/change-set-contract.md, "Canonical Semantic Snapshot": LibLCM's own in-memory
    /// representation of a MultiUnicode alternative is already NFD, and empty alternatives are
    /// omitted rather than represented as an empty string.
    /// </summary>
    private static Dictionary<string, string> ReadMultiUnicode(LcmCache cache, IMultiUnicode accessor)
    {
        var result = new Dictionary<string, string>();

        foreach (var ws in accessor.AvailableWritingSystemIds)
        {
            var text = accessor.get_String(ws)?.Text;
            if (string.IsNullOrEmpty(text))
                continue; // Empty is indistinguishable from absent; omit rather than write "".

            var tag = cache.WritingSystemFactory.GetStrFromWs(ws);
            result[tag] = text;
        }

        return result;
    }
}
