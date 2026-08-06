using System.Collections.Generic;
using SIL.LCModel;
using SIL.LCModel.Core.KernelInterfaces;

namespace SIL.Motif.Runner.Snapshotting;

/// <summary>
/// Reads every populated writing-system alternative of a MultiUnicode/MultiString accessor into a
/// ws-tag -&gt; text map — the read half of <see cref="Operations.MultiAlternativesFieldLowering"/>,
/// generalized from <c>LexSenseSnapshotter.ReadMultiUnicode</c>'s pre-MOT-4 body (see that type's own
/// remarks for the "empty is indistinguishable from absent" rule this preserves verbatim).
/// </summary>
internal static class MultiAlternativesFieldSnapshotting
{
    public static Dictionary<string, string> ReadAlternatives<TAccessor>(LcmCache cache, TAccessor accessor)
        where TAccessor : IMultiAccessorBase, ITsMultiString
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
